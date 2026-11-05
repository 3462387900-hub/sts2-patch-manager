param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'

$source = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "游戏目录不存在：$source" }
if (!(Test-Path -LiteralPath (Join-Path $source 'SlayTheSpire2.exe') -PathType Leaf)) {
    throw 'GameRoot 中没有 SlayTheSpire2.exe，请选择游戏安装根目录。'
}
if ($source.Equals($output, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '隔离样本必须位于游戏目录之外，不能覆盖或嵌套在原游戏目录中。'
}
if (Test-Path -LiteralPath $output) { throw "输出目录已存在。为避免覆盖，请选择一个新的空路径：$output" }

foreach ($name in @('mods', 'data_sts2_windows_x86_64')) {
    $path = Join-Path $source $name
    if (!(Test-Path -LiteralPath $path -PathType Container)) {
        throw "游戏目录缺少必需文件夹：$name。请确认 GameRoot 指向游戏安装根目录。"
    }
}

function Assert-NoReparsePoints([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "检测到符号链接或目录联接，拒绝复制：$entry"
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            }
        }
    }
}

function Copy-TreeContents([string]$SourceDirectory, [string]$DestinationDirectory) {
    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($SourceDirectory)) {
        $target = Join-Path $DestinationDirectory ([IO.Path]::GetFileName($entry))
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            Copy-TreeContents $entry $target
        } else {
            [IO.File]::Copy($entry, $target, $false)
        }
    }
}

New-Item -ItemType Directory -Path $output | Out-Null
try {
    Assert-NoReparsePoints (Join-Path $source 'mods')
    Assert-NoReparsePoints (Join-Path $source 'data_sts2_windows_x86_64')
    Copy-TreeContents (Join-Path $source 'mods') (Join-Path $output 'mods')
    Copy-TreeContents (Join-Path $source 'data_sts2_windows_x86_64') (Join-Path $output 'data_sts2_windows_x86_64')
    [IO.File]::WriteAllText((Join-Path $output 'SlayTheSpire2.exe'), '')
    [IO.File]::WriteAllText((Join-Path $output '.spirepatch-fixture'), 'isolated release fixture')
    if ((Get-Item -LiteralPath (Join-Path $output 'SlayTheSpire2.exe')).Length -ne 0) {
        throw '隔离样本游戏标记文件不是零字节。'
    }
    Write-Output "隔离样本已创建：$output"
    Write-Output '只复制 mods 与 data_sts2_windows_x86_64；未复制游戏 EXE、存档、日志或管理器数据。'
    Write-Output '请仅在本机使用，不要将样本目录提交到公开仓库或发布包。'
} catch {
    try { Remove-Item -LiteralPath $output -Recurse -Force } catch {}
    throw
}
