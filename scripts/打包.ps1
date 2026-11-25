param([string]$OutputRoot = '', [switch]$BuildOnly, [string]$Bundle = '', [string]$Fixture = '', [string]$InformationalVersion = '')
if (!$BuildOnly -and (!$Bundle -or !$Fixture)) { throw '正式打包必须提供 -Bundle 和 -Fixture 运行完整隔离集成测试；仅构建检查请指定 -BuildOnly（不会生成发布 ZIP）。' }
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputRoot) { $OutputRoot = Join-Path $sourceRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
[xml]$project = Get-Content -LiteralPath (Join-Path $sourceRoot 'SpirePatch.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$projectInfoVersion = [string]$project.Project.PropertyGroup.InformationalVersion
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw '需要规范的三段基础版本号。' }
if ($projectInfoVersion -cne $version) { throw 'SpirePatch.csproj 的 InformationalVersion 必须与 Version 保持相同的基础三段版本；Beta 后缀由发布工作流在编译时注入。' }
if ([string]::IsNullOrWhiteSpace($InformationalVersion)) { $InformationalVersion = $projectInfoVersion }
$infoMatch = [regex]::Match($InformationalVersion, '^(?<base>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?<suffix>-beta\.[1-9][0-9]{0,8})?$')
if (!$infoMatch.Success -or $infoMatch.Groups['base'].Value -cne $version) { throw '程序集 InformationalVersion 必须是项目基础版本，或该基础版本加 -beta.N。' }
$runRoot = Join-Path $OutputRoot ('发布-' + $version + '-' + [Guid]::NewGuid().ToString('N'))
$appFolder = Join-Path $runRoot ('尖塔修补站-v' + $version)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
dotnet publish (Join-Path $sourceRoot 'SpirePatch.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None "-p:InformationalVersion=$InformationalVersion" -o $appFolder
if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
$testRoot = Join-Path $runRoot '验证记录'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
foreach ($mode in @('safety-self-test','self-test','recommendation-self-test','online-self-test','github-mod-install-self-test','github-mod-discovery-self-test','github-network-self-test', 'supply-station-self-test', 'supply-station-manifest-self-test','supply-station-content-self-test','workshop-mod-install-self-test','workshop-mod-update-self-test','update-self-test','auto-update-self-test','coop-self-test','coop-actions-self-test','catalog-v3-self-test','patch-organizer-self-test','patch-lifecycle-self-test','workspace-integration-self-test','manager-update-cleanup-self-test','workshop-zip-self-test','neon-p1-self-test','neon-p2-self-test','neon-perf-self-test')) {
    $result = Join-Path $testRoot ($mode + '.txt')
    $process = Start-Process -FilePath (Join-Path $appFolder 'SpirePatch.exe') -ArgumentList @('--' + $mode, ('"' + $result + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result)) {
        $errorFile = Join-Path $appFolder 'startup-error.txt'
        if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result | Write-Output }
        if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Output }
        throw ('验证失败：' + $mode)
    }
    Write-Output ('PASS --' + $mode)
}
# Verify the real WPF navigation and row actions against isolated mock game folders.
$uiFixtures = Join-Path $testRoot 'ui-fixtures'
$uiOutput = Join-Path $testRoot 'ui-capture'
New-Item -ItemType Directory -Path $uiFixtures, $uiOutput -Force | Out-Null
$uiProcess = Start-Process -FilePath (Join-Path $appFolder 'SpirePatch.exe') -ArgumentList @('--capture-workspace', ('"' + $uiFixtures + '"'), ('"' + $uiOutput + '"')) -WindowStyle Hidden -Wait -PassThru
$uiProof = Join-Path $uiOutput '四项功能界面验证.txt'
if ($uiProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $uiProof)) {
    if (Test-Path -LiteralPath $uiProof) { Get-Content -LiteralPath $uiProof | Write-Output }
    $errorFile = Join-Path $appFolder 'startup-error.txt'
    if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Output }
    throw 'WPF 界面回归验证失败。'
}
Write-Output 'PASS --capture-workspace'
$neonWbFixtures = Join-Path $testRoot 'neon-workbench-fixtures'
$neonWbOutput = Join-Path $testRoot 'neon-workbench-capture'
New-Item -ItemType Directory -Path $neonWbFixtures, $neonWbOutput -Force | Out-Null
$neonWbProcess = Start-Process -FilePath (Join-Path $appFolder 'SpirePatch.exe') -ArgumentList @('--capture-neon-workbench', ('"' + $neonWbFixtures + '"'), ('"' + $neonWbOutput + '"')) -WindowStyle Hidden -Wait -PassThru
$neonWbProof = Join-Path $neonWbOutput '工作台验证报告.txt'
if ($neonWbProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $neonWbProof)) {
    if (Test-Path -LiteralPath $neonWbProof) { Get-Content -LiteralPath $neonWbProof | Write-Output }
    $errorFile = Join-Path $appFolder 'startup-error.txt'
    if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Output }
    throw 'Neon 工作台界面回归验证失败。'
}
Write-Output 'PASS --capture-neon-workbench'
$neonMoFixtures = Join-Path $testRoot 'neon-motion-fixtures'
$neonMoOutput = Join-Path $testRoot 'neon-motion-capture'
New-Item -ItemType Directory -Path $neonMoFixtures, $neonMoOutput -Force | Out-Null
$neonMoProcess = Start-Process -FilePath (Join-Path $appFolder 'SpirePatch.exe') -ArgumentList @('--capture-neon-motion', ('"' + $neonMoFixtures + '"'), ('"' + $neonMoOutput + '"')) -WindowStyle Hidden -Wait -PassThru
$neonMoProof = Join-Path $neonMoOutput '动效验收报告.txt'
if ($neonMoProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $neonMoProof)) {
    if (Test-Path -LiteralPath $neonMoProof) { Get-Content -LiteralPath $neonMoProof | Write-Output }
    $errorFile = Join-Path $appFolder 'startup-error.txt'
    if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Output }
    throw 'Neon 动效回归验证失败。'
}
Write-Output 'PASS --capture-neon-motion'
if ($BuildOnly) { Write-Output ('构建与检查完成，未生成发布包：' + $runRoot); return }
$integrationResult = Join-Path $testRoot 'integration-check.txt'
$process = Start-Process -FilePath (Join-Path $appFolder 'SpirePatch.exe') -ArgumentList @('--integration-check', ('"' + [IO.Path]::GetFullPath($Bundle) + '"'), ('"' + [IO.Path]::GetFullPath($Fixture) + '"'), ('"' + $integrationResult + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $integrationResult)) { throw '完整隔离集成测试失败，不生成发布 ZIP。' }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'README.md') -Destination (Join-Path $appFolder '使用说明.md')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/发布指南.md') -Destination (Join-Path $appFolder '发布指南.md')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/补丁发布指南.md') -Destination (Join-Path $appFolder '补丁发布指南.md')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'THIRD-PARTY-NOTICES.md') -Destination $appFolder
Copy-Item -LiteralPath (Join-Path $sourceRoot '鸣谢与来源.md') -Destination $appFolder
$thirdParty = Join-Path $appFolder 'third_party'
New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'third_party/WorkshopAndroidDownloader-LICENSE.txt') -Destination $thirdParty
Copy-Item -LiteralPath (Join-Path $sourceRoot 'third_party/SharpCompress-LICENSE.txt') -Destination $thirdParty
$notes = Join-Path $sourceRoot ('releases/v' + $version + '.md')
if (!(Test-Path -LiteralPath $notes)) { throw '缺少本版本发布说明。' }
Copy-Item -LiteralPath $notes -Destination (Join-Path $appFolder '本版更新内容.md')
& (Join-Path $PSScriptRoot '验证发布声明.ps1') -Directory $appFolder -SourceRoot $sourceRoot
& (Join-Path $PSScriptRoot '生成程序清单.ps1') -Directory $appFolder
$archive = Join-Path $runRoot ('SpirePatch-v' + $version + '-win-x64.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($appFolder, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
& (Join-Path $PSScriptRoot '验证发布声明.ps1') -Directory $appFolder -SourceRoot $sourceRoot -ArchivePath $archive
$checksum = [ordered]@{ version=$version; file_name=[IO.Path]::GetFileName($archive); bytes=(Get-Item -LiteralPath $archive).Length; sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
$checksum | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot '校验信息.json') -Encoding utf8
Write-Output ('发布文件位于：' + $runRoot)
