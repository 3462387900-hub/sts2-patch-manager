param([Parameter(Mandatory=$true)][string]$ApplicationDirectory)

$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($ApplicationDirectory)
$original = Join-Path $app 'SpirePatch.exe'
$chinese = Join-Path $app '00-启动尖塔修补站.exe'
if (!(Test-Path -LiteralPath $original -PathType Leaf)) {
    throw 'SpirePatch.exe is missing; cannot create the Chinese launcher.'
}

# .NET apphost contains the assembly name SpirePatch.dll in its own binary.
# Copying the WinExe apphost does not rename the DLL or change application paths.
# Keep the original executable because the existing verified updater requires it.
[IO.File]::Copy($original, $chinese, $true)
if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $chinese -Algorithm SHA256).Hash) {
    throw 'Chinese launcher differs from original Windows apphost.'
}

# Documentation is safe to relocate: it is not a runtime or updater dependency.
$documents = @(
    '使用说明.md', '发布指南.md',
    '补丁发布指南.md', '本版更新内容.md',
    '鸣谢与来源.md', 'THIRD-PARTY-NOTICES.md'
)
$docs = Join-Path $app '使用说明'
foreach ($name in $documents) {
    $source = Join-Path $app $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    [IO.Directory]::CreateDirectory($docs) | Out-Null
    $target = Join-Path $docs $name
    if (Test-Path -LiteralPath $target) {
        throw ('Documentation already exists in both locations: ' + $name)
    }
    [IO.File]::Move($source, $target)
}

Write-Output ('Portable Chinese launcher ready: ' + $chinese)
