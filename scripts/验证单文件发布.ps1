param(
    [string]$BaselineRoot = 'artifacts',
    [string]$OutputRoot = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$BaselineRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot $BaselineRoot))
$OutputRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot $OutputRoot))
[xml]$project = Get-Content -LiteralPath (Join-Path $sourceRoot 'SpirePatch.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid manager version.' }

$baseline = Get-ChildItem -LiteralPath $BaselineRoot -Directory |
    Where-Object { $_.Name -like ('发布-' + $version + '-*') } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $baseline) { throw 'A successful normal publish baseline is required. Run scripts/打包.ps1 -BuildOnly first.' }
$baselineApp = Join-Path $baseline.FullName ('尖塔修补站-v' + $version)
foreach ($name in @('SpirePatch.exe', 'SpirePatch.dll', 'SpirePatch.deps.json', 'SpirePatch.runtimeconfig.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $baselineApp $name) -PathType Leaf)) {
        throw ('Normal publish baseline is missing updater compatibility file: ' + $name)
    }
}

$experimentRoot = Join-Path $OutputRoot ('single-file-validation-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $experimentRoot ('尖塔修补站-v' + $version)
$testRoot = Join-Path $experimentRoot '验证记录'
New-Item -ItemType Directory -Path $app, $testRoot -Force | Out-Null

$publishProperties = @(
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=false',
    '-p:SatelliteResourceLanguages=zh-Hans%3Bzh-Hant',
    '-p:DebugType=None'
)
& dotnet publish (Join-Path $sourceRoot 'SpirePatch.csproj') -c Release -r win-x64 --self-contained true @publishProperties -o $app
if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }

# Keep these three small sidecars for the existing updater's manifest/version contract.
# The apphost bundle itself contains the executable application and managed dependencies.
foreach ($name in @('SpirePatch.dll', 'SpirePatch.deps.json', 'SpirePatch.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $baselineApp $name) -Destination $app
}
if ((Get-Item -LiteralPath (Join-Path $app 'SpirePatch.exe')).Length -lt 20MB) {
    throw 'The output executable is too small to be the expected self-contained bundle.'
}

$modes = @(
    'safety-self-test', 'self-test', 'recommendation-self-test', 'online-self-test',
    'github-mod-install-self-test', 'github-mod-discovery-self-test', 'github-network-self-test',
    'supply-station-self-test', 'supply-station-manifest-self-test', 'supply-station-content-self-test',
    'workshop-mod-install-self-test', 'workshop-mod-update-self-test', 'update-self-test',
    'auto-update-self-test', 'coop-self-test', 'coop-actions-self-test', 'catalog-v3-self-test',
    'patch-organizer-self-test', 'patch-lifecycle-self-test', 'workspace-integration-self-test',
    'manager-update-cleanup-self-test', 'workshop-zip-self-test'
)
foreach ($mode in $modes) {
    $result = Join-Path $testRoot ($mode + '.txt')
    $process = Start-Process -FilePath (Join-Path $app 'SpirePatch.exe') -ArgumentList @('--' + $mode, ('"' + $result + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result)) {
        $errorFile = Join-Path $app 'startup-error.txt'
        if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result | Write-Output }
        if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Output }
        throw ('Single-file validation failed: ' + $mode)
    }
}

$uiFixtures = Join-Path $testRoot 'atlas-native-fixtures'
$uiOutput = Join-Path $testRoot 'atlas-native-capture'
New-Item -ItemType Directory -Path $uiFixtures, $uiOutput -Force | Out-Null
$uiProcess = Start-Process -FilePath (Join-Path $app 'SpirePatch.exe') -ArgumentList @('--capture-atlas-native', ('"' + $uiFixtures + '"'), ('"' + $uiOutput + '"')) -WindowStyle Hidden -Wait -PassThru
$uiProof = Join-Path $uiOutput 'ATLAS 原生界面验证.txt'
if ($uiProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $uiProof)) {
    if (Test-Path -LiteralPath $uiProof) { Get-Content -LiteralPath $uiProof | Write-Output }
    throw 'Single-file WPF startup/navigation capture failed.'
}

# Build the same player-facing layout as a release candidate.
Copy-Item -LiteralPath (Join-Path $sourceRoot 'README.md') -Destination (Join-Path $app '使用说明.md')
foreach ($name in @('发布指南.md', '补丁发布指南.md', 'THIRD-PARTY-NOTICES.md', '鸣谢与来源.md')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $app
}
$releaseVersion = [string]$project.Project.PropertyGroup.InformationalVersion
if ([string]::IsNullOrWhiteSpace($releaseVersion)) { $releaseVersion = $version }
$releaseVersion = $releaseVersion.Split('+')[0]
$notesCandidates = @(
    (Join-Path $sourceRoot ('releases/v' + $releaseVersion + '.md')),
    (Join-Path $sourceRoot ('releases/v' + $version + '.md'))
)
$notes = $notesCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ($null -eq $notes) { throw ('Release notes are missing for ' + $releaseVersion + '.') }
Copy-Item -LiteralPath $notes -Destination (Join-Path $app '本版更新内容.md')
$thirdParty = Join-Path $app 'third_party'
New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'third_party/WorkshopAndroidDownloader-LICENSE.txt') -Destination $thirdParty
Copy-Item -LiteralPath (Join-Path $sourceRoot 'third_party/SharpCompress-LICENSE.txt') -Destination $thirdParty
& (Join-Path $PSScriptRoot '整理玩家发布目录.ps1') -ApplicationDirectory $app
& (Join-Path $PSScriptRoot '生成程序清单.ps1') -Directory $app
$layoutReport = Join-Path $testRoot 'single-file-layout-self-test.json'
$layoutProcess = Start-Process -FilePath (Join-Path $app 'SpirePatch.exe') -ArgumentList @('--single-file-layout-self-test', ('"' + $layoutReport + '"')) -WindowStyle Hidden -Wait -PassThru
if ($layoutProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $layoutReport)) {
    if (Test-Path -LiteralPath $layoutReport) { Get-Content -LiteralPath $layoutReport | Write-Output }
    $errorFile = Join-Path $app 'startup-error.txt'
    if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Output }
    throw 'Single-file updater manifest and canonical launcher validation failed.'
}

$baselineItems = @(Get-ChildItem -LiteralPath $baselineApp -Force -Recurse).Count
$singleItems = @(Get-ChildItem -LiteralPath $app -Force -Recurse).Count
$baselineTop = @(Get-ChildItem -LiteralPath $baselineApp -Force).Count
$singleTop = @(Get-ChildItem -LiteralPath $app -Force).Count
$rootDlls = @(Get-ChildItem -LiteralPath $app -File -Filter '*.dll')
if ($singleItems -ge [Math]::Ceiling($baselineItems * 0.5)) {
    throw ("Single-file publishing did not reduce the visible package items enough. Baseline={0}, single-file={1}" -f $baselineItems, $singleItems)
}
if ($singleTop -gt 30 -or $rootDlls.Count -gt 5) {
    throw ("Single-file package still has too many root entries. RootItems={0}, rootDLLs={1}" -f $singleTop, $rootDlls.Count)
}

$archive = Join-Path $experimentRoot ('SpirePatch-v' + $version + '-single-file-validation-win-x64.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
$report = @(
    ('Baseline total items: ' + $baselineItems),
    ('Single-file total items: ' + $singleItems),
    ('Baseline root items: ' + $baselineTop),
    ('Single-file root items: ' + $singleTop),
    ('Root DLL sidecars: ' + $rootDlls.Count),
    ('Single-file EXE bytes: ' + (Get-Item -LiteralPath (Join-Path $app 'SpirePatch.exe')).Length),
    ('ZIP bytes: ' + (Get-Item -LiteralPath $archive).Length),
    '',
    'Player root entries:',
    (Get-ChildItem -LiteralPath $app -Force | Select-Object Name, Mode, Length | Format-Table -AutoSize | Out-String)
) -join [Environment]::NewLine
$reportPath = Join-Path $experimentRoot 'single-file-layout-report.txt'
[IO.File]::WriteAllText($reportPath, $report, [Text.UTF8Encoding]::new($false))
Write-Output $report
Write-Output ('Single-file validation package: ' + $archive)
