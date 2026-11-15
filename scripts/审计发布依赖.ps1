param(
    [string]$ArtifactsRoot = 'artifacts'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sourceRoot = Split-Path -Parent $PSScriptRoot
if (![IO.Path]::IsPathRooted($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $sourceRoot $ArtifactsRoot
}
$ArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
[xml]$project = Get-Content -LiteralPath (Join-Path $sourceRoot 'SpirePatch.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid manager version.' }

function Find-Application([string]$directoryPattern) {
    $candidates = @(Get-ChildItem -LiteralPath $ArtifactsRoot -Directory |
        Where-Object { $_.Name -like $directoryPattern } |
        Sort-Object LastWriteTimeUtc -Descending)
    foreach ($candidate in $candidates) {
        $app = Join-Path $candidate.FullName ('尖塔修补站-v' + $version)
        if (Test-Path -LiteralPath (Join-Path $app 'SpirePatch.exe') -PathType Leaf) {
            return $app
        }
    }
    throw ('Windows application output not found for ' + $directoryPattern)
}

# Detect PE files without loading an assembly or executing the published program.
function Is-PortableExecutable([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    try {
        if ($stream.Length -lt 64) { return $false }
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or ([long]$offset + 4) -gt $stream.Length) { return $false }
        $stream.Position = $offset
        return ($reader.ReadUInt32() -eq 0x00004550)
    } finally {
        $stream.Dispose()
    }
}

function Get-FileRole([string]$relative, [string]$kind) {
    if ($relative -eq 'SpirePatch.exe') { return 'launcher' }
    if ($relative -eq 'SpirePatch.dll') { return 'main-assembly' }
    if ($relative -eq 'manager-files.json') { return 'updater-manifest' }
    if ($relative -like 'SpirePatch.*.json') { return 'runtime-configuration' }
    if ($relative -eq 'SharpCompress.dll') { return 'third-party-managed' }
    if ($relative -like 'third_party/*' -or $relative -like '使用说明/*' -or
        $relative -match '\.(md|txt)$') { return 'documentation' }
    if ($relative -match '\.lnk$') { return 'launcher-shortcut' }
    if ($relative -like '数据/*' -or $relative -eq 'startup-error.txt') { return 'generated-runtime-data' }
    if ($kind -eq 'managed-assembly' -and
        $relative -match '^(System\.|Microsoft\.|Presentation|WindowsBase\.|netstandard\.|UIAutomation|DirectWriteForwarder\.)') {
        return 'dotnet-managed-runtime'
    }
    if ($kind -eq 'managed-assembly') { return 'other-managed-assembly' }
    if ($kind -eq 'native-pe') { return 'native-runtime-or-tool' }
    if ($relative -match '\.(json|config)$') { return 'configuration-or-data' }
    return 'resource-or-other'
}

function Get-Inventory([string]$applicationDirectory) {
    $prefix = [IO.Path]::GetFullPath($applicationDirectory).TrimEnd('\') + '\'
    Get-ChildItem -LiteralPath $applicationDirectory -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
            $kind = 'non-pe'
            $assemblyName = $null
            $assemblyVersion = $null
            if ($_.Extension -in @('.exe', '.dll')) {
                try {
                    $identity = [Reflection.AssemblyName]::GetAssemblyName($_.FullName)
                    $kind = 'managed-assembly'
                    $assemblyName = $identity.Name
                    $assemblyVersion = $identity.Version.ToString()
                } catch [System.BadImageFormatException] {
                    if (Is-PortableExecutable $_.FullName) { $kind = 'native-pe' }
                } catch [System.IO.FileLoadException] {
                    if (Is-PortableExecutable $_.FullName) { $kind = 'native-pe' }
                }
            }
            [pscustomobject][ordered]@{
                Path = $relative
                Bytes = $_.Length
                SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                Kind = $kind
                Role = (Get-FileRole $relative $kind)
                AssemblyName = $assemblyName
                AssemblyVersion = $assemblyVersion
            }
        }
}

function Write-Json([string]$path, [object]$value) {
    $json = ConvertTo-Json -InputObject $value -Depth 12
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
}

$normalRoot = Find-Application ('发布-' + $version + '-*')
$singleRoot = Find-Application 'single-file-validation-*'
$normal = @(Get-Inventory $normalRoot)
$single = @(Get-Inventory $singleRoot)

foreach ($files in @($normal, $single)) {
    foreach ($required in @('SpirePatch.exe', 'SpirePatch.dll', 'SpirePatch.deps.json',
                            'SpirePatch.runtimeconfig.json')) {
        if (@($files | Where-Object Path -eq $required).Count -ne 1) {
            throw ('A required updater compatibility file is missing or duplicated: ' + $required)
        }
    }
}

$normalByPath = @{}
$singleByPath = @{}
foreach ($f in $normal) { $normalByPath[$f.Path] = $f }
foreach ($f in $single) { $singleByPath[$f.Path] = $f }
$allPaths = @(@($normalByPath.Keys) + @($singleByPath.Keys) | Sort-Object -Unique)
$comparison = @(
    foreach ($path in $allPaths) {
        $n = $normalByPath[$path]
        $s = $singleByPath[$path]
        $status = if ($null -eq $n) { 'only-in-single-file-layout' }
                  elseif ($null -eq $s) { 'not-visible-in-single-file-layout' }
                  elseif ($n.SHA256 -eq $s.SHA256) { 'same-file' }
                  else { 'different-content' }
        [pscustomobject][ordered]@{
            Path = $path
            Status = $status
            NormalBytes = if ($n) { $n.Bytes } else { $null }
            SingleFileBytes = if ($s) { $s.Bytes } else { $null }
            NormalKind = if ($n) { $n.Kind } else { $null }
            NormalRole = if ($n) { $n.Role } else { $null }
            SingleFileKind = if ($s) { $s.Kind } else { $null }
            SingleFileRole = if ($s) { $s.Role } else { $null }
        }
    }
)

$output = Join-Path $ArtifactsRoot ('dependency-inventory-' + $version)
[IO.Directory]::CreateDirectory($output) | Out-Null
Write-Json (Join-Path $output 'normal-publish.json') ([ordered]@{
    Schema = 1; Version = $version; Layout = 'normal-self-contained'
    Files = $normal
})
Write-Json (Join-Path $output 'single-file-experiment.json') ([ordered]@{
    Schema = 1; Version = $version; Layout = 'single-file-validation'
    Files = $single
})
Write-Json (Join-Path $output 'layout-comparison.json') ([ordered]@{
    Schema = 1; Version = $version; Files = $comparison
})

$normalDll = @($normal | Where-Object { $_.Path -match '\.dll$' })
$singleDll = @($single | Where-Object { $_.Path -match '\.dll$' })
$normalRootDll = @($normalDll | Where-Object { $_.Path -notlike '*/*' })
$singleRootDll = @($singleDll | Where-Object { $_.Path -notlike '*/*' })
$notVisible = @($comparison | Where-Object Status -eq 'not-visible-in-single-file-layout')
$md = @(
    '# Windows 发布依赖清单对照'
    ''
    ('版本：' + $version)
    ''
    '| 指标 | 普通自包含发布 | 单文件实验发布 |'
    '|---|---:|---:|'
    ('| 可见文件总数 | ' + $normal.Count + ' | ' + $single.Count + ' |')
    ('| DLL 文件数量 | ' + $normalDll.Count + ' | ' + $singleDll.Count + ' |')
    ('| 根目录 DLL | ' + $normalRootDll.Count + ' | ' + $singleRootDll.Count + ' |')
    ('| 托管程序集 | ' + @($normal | Where-Object Kind -eq 'managed-assembly').Count +
     ' | ' + @($single | Where-Object Kind -eq 'managed-assembly').Count + ' |')
    ('| 原生 PE 文件 | ' + @($normal | Where-Object Kind -eq 'native-pe').Count +
     ' | ' + @($single | Where-Object Kind -eq 'native-pe').Count + ' |')
    ''
    ('普通发布有、单文件目录未显示的文件：' + $notVisible.Count + ' 项。')
    ''
    '**注意**：不在单文件目录中可见，不等于文件可以删除。它可能已打包进 EXE、由运行时提取，或本身不属于最终打包步骤。'
    '此审计只检查静态文件和程序集元数据；不判断运行时加载路径，也不改变发布包、更新清单或游戏数据。'
    ''
    '完整路径、大小、SHA-256、PE 类型、程序集名称和版本见两份 JSON；逐文件对照见 layout-comparison.json。'
)
[IO.File]::WriteAllText((Join-Path $output 'README.md'),
    ($md -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Output ('PASS release dependency inventories: normal=' + $normal.Count +
              ', single-file=' + $single.Count + ', non-visible=' + $notVisible.Count)
Write-Output ('Reports: ' + $output)
