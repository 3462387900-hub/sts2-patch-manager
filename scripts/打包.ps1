param([string]$OutputRoot = '')
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputRoot) { $OutputRoot = Join-Path $sourceRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
[xml]$project = Get-Content -LiteralPath (Join-Path $sourceRoot 'SpirePatch.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '需要三段正式版本号。' }
$runRoot = Join-Path $OutputRoot ('发布-' + $version + '-' + [Guid]::NewGuid().ToString('N'))
$appFolder = Join-Path $runRoot ('尖塔修补站-v' + $version)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
dotnet publish (Join-Path $sourceRoot 'SpirePatch.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $appFolder
if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
$testRoot = Join-Path $runRoot '验证记录'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
foreach ($mode in @('self-test','recommendation-self-test','online-self-test','update-self-test','auto-update-self-test','coop-self-test')) {
    $result = Join-Path $testRoot ($mode + '.txt')
    $process = Start-Process -FilePath (Join-Path $appFolder 'SpirePatch.exe') -ArgumentList @('--' + $mode, ('"' + $result + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result)) { throw ('验证失败：' + $mode) }
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'README.md') -Destination (Join-Path $appFolder '使用说明.md')
Copy-Item -LiteralPath (Join-Path $sourceRoot '更新日志.md') -Destination $appFolder
$notes = Join-Path $sourceRoot ('releases/v' + $version + '.md')
if (!(Test-Path -LiteralPath $notes)) { throw '缺少本版本发布说明。' }
Copy-Item -LiteralPath $notes -Destination (Join-Path $appFolder '本版更新内容.md')
& (Join-Path $PSScriptRoot '生成程序清单.ps1') -Directory $appFolder
$archive = Join-Path $runRoot ('SpirePatch-v' + $version + '-win-x64.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($appFolder, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
$checksum = [ordered]@{ version=$version; file_name=[IO.Path]::GetFileName($archive); bytes=(Get-Item -LiteralPath $archive).Length; sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
$checksum | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot '校验信息.json') -Encoding utf8
Write-Output ('发布文件位于：' + $runRoot)
