param([Parameter(Mandatory=$true)][string]$ApplicationDirectory)

$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($ApplicationDirectory)
if (!(Test-Path -LiteralPath $app -PathType Container)) {
    throw 'Application directory does not exist.'
}
$original = Join-Path $app 'SpirePatch.exe'
$shortcut = Join-Path $app '00-启动尖塔修补站.lnk'
if (!(Test-Path -LiteralPath $original -PathType Leaf)) {
    throw 'SpirePatch.exe is missing; cannot create the Chinese shortcut.'
}

# Use a real Windows shortcut rather than a second apphost executable.
# The shortcut starts the canonical SpirePatch.exe, so the updater can wait
# for that process to exit and safely replace the canonical executable.
# WScript.Shell has trouble saving non-ASCII shortcut filenames on some
# Windows locales. Create/inspect an ASCII temporary link, then move it with
# .NET's Unicode-safe file APIs to the final Chinese filename.
$tempShortcut = Join-Path $app ('SpirePatchLauncher-' + [Guid]::NewGuid().ToString('N') + '.lnk')
$shell = New-Object -ComObject WScript.Shell
try {
    if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
        [IO.File]::Copy($shortcut, $tempShortcut, $false)
    }

    $link = $shell.CreateShortcut($tempShortcut)
    try {
        if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
            $target = [IO.Path]::GetFullPath([string]$link.TargetPath)
            $working = [IO.Path]::GetFullPath([string]$link.WorkingDirectory)
            if (![string]::Equals($target, $original, [StringComparison]::OrdinalIgnoreCase) -or
                ![string]::Equals($working, $app, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The Chinese shortcut already exists but points to a different target; refusing to overwrite it.'
            }
        }
        else {
            $link.TargetPath = $original
            $link.WorkingDirectory = $app
            $link.Description = '启动尖塔修补站'
            $link.IconLocation = $original + ',0'
            $link.WindowStyle = 1
            $link.Save()
        }
    }
    finally {
        if ([Runtime.InteropServices.Marshal]::IsComObject($link)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
        }
    }

    # Re-open and verify the persisted shortcut before publishing it.
    $verify = $shell.CreateShortcut($tempShortcut)
    try {
        $target = [IO.Path]::GetFullPath([string]$verify.TargetPath)
        $working = [IO.Path]::GetFullPath([string]$verify.WorkingDirectory)
        if (![string]::Equals($target, $original, [StringComparison]::OrdinalIgnoreCase) -or
            ![string]::Equals($working, $app, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The saved Chinese shortcut failed target or working-directory verification.'
        }
    }
    finally {
        if ([Runtime.InteropServices.Marshal]::IsComObject($verify)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($verify)
        }
    }

    if (!(Test-Path -LiteralPath $shortcut -PathType Leaf)) {
        [IO.File]::Move($tempShortcut, $shortcut)
    }
}
finally {
    if (Test-Path -LiteralPath $tempShortcut -PathType Leaf) {
        [IO.File]::Delete($tempShortcut)
    }
    if ([Runtime.InteropServices.Marshal]::IsComObject($shell)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
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

Write-Output ('Portable Chinese shortcut ready: ' + $shortcut)
