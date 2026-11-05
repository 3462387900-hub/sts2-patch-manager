#requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

$source = [IO.Path]::GetFullPath($GameRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)

$sourceExists = Test-Path -LiteralPath $source -PathType Container
if (-not $sourceExists) {
    throw "游戏目录不存在：$source"
}

$sourceAttributes = [IO.File]::GetAttributes($source)
if (($sourceAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw '游戏根目录本身是符号链接或目录联接，拒绝创建隔离样本。'
}

$gameExecutable = Join-Path -Path $source -ChildPath 'SlayTheSpire2.exe'
$hasGameExecutable = Test-Path -LiteralPath $gameExecutable -PathType Leaf
if (-not $hasGameExecutable) {
    throw 'GameRoot 中没有 SlayTheSpire2.exe，请选择游戏安装根目录。'
}

$sourcePrefix = $source + [IO.Path]::DirectorySeparatorChar
$outputPrefix = $output + [IO.Path]::DirectorySeparatorChar
if ($source.Equals($output, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '隔离样本必须位于游戏目录之外，不能覆盖或嵌套在原游戏目录中。'
}

$outputExists = Test-Path -LiteralPath $output
if ($outputExists) {
    throw "输出目录已存在。为避免覆盖，请选择一个新的空路径：$output"
}

$requiredFolders = @('mods', 'data_sts2_windows_x86_64')
foreach ($name in $requiredFolders) {
    $folderPath = Join-Path -Path $source -ChildPath $name
    $folderExists = Test-Path -LiteralPath $folderPath -PathType Container
    if (-not $folderExists) {
        throw "游戏目录缺少必需文件夹：$name。请确认 GameRoot 指向游戏安装根目录。"
    }
}

function Assert-NoReparsePoints {
    param([string]$Root)

    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Root)

    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $entries = [IO.Directory]::EnumerateFileSystemEntries($directory)

        foreach ($entry in $entries) {
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

function Copy-TreeContents {
    param(
        [string]$SourceDirectory,
        [string]$DestinationDirectory
    )

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $entries = [IO.Directory]::EnumerateFileSystemEntries($SourceDirectory)

    foreach ($entry in $entries) {
        $leafName = [IO.Path]::GetFileName($entry)
        $target = Join-Path -Path $DestinationDirectory -ChildPath $leafName
        $attributes = [IO.File]::GetAttributes($entry)

        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            Copy-TreeContents -SourceDirectory $entry -DestinationDirectory $target
        }
        else {
            [IO.File]::Copy($entry, $target, $false)
        }
    }
}

New-Item -ItemType Directory -Path $output | Out-Null

try {
    $modsSource = Join-Path -Path $source -ChildPath 'mods'
    $dataSource = Join-Path -Path $source -ChildPath 'data_sts2_windows_x86_64'
    $modsDestination = Join-Path -Path $output -ChildPath 'mods'
    $dataDestination = Join-Path -Path $output -ChildPath 'data_sts2_windows_x86_64'
    $fixtureExecutable = Join-Path -Path $output -ChildPath 'SlayTheSpire2.exe'
    $fixtureMarker = Join-Path -Path $output -ChildPath '.spirepatch-fixture'

    Assert-NoReparsePoints -Root $modsSource
    Assert-NoReparsePoints -Root $dataSource

    Copy-TreeContents -SourceDirectory $modsSource -DestinationDirectory $modsDestination
    Copy-TreeContents -SourceDirectory $dataSource -DestinationDirectory $dataDestination

    [IO.File]::WriteAllText($fixtureExecutable, '')
    [IO.File]::WriteAllText($fixtureMarker, 'isolated release fixture')

    $fixtureExecutableInfo = Get-Item -LiteralPath $fixtureExecutable
    if ($fixtureExecutableInfo.Length -ne 0) {
        throw '隔离样本游戏标记文件不是零字节。'
    }

    Write-Output "隔离样本已创建：$output"
    Write-Output '只复制 mods 与 data_sts2_windows_x86_64；未复制游戏 EXE、存档、日志或管理器数据。'
    Write-Output '请仅在本机使用，不要将样本目录提交到公开仓库或发布包。'
}
catch {
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    throw
}
