#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [string]$SourceRoot=(Split-Path -Parent $PSScriptRoot),
    [string]$ArchivePath=''
)
$ErrorActionPreference='Stop'
$Directory=[IO.Path]::GetFullPath($Directory)
$SourceRoot=[IO.Path]::GetFullPath($SourceRoot)
$requiredFiles=@(
    '鸣谢与来源.md',
    'THIRD-PARTY-NOTICES.md',
    'third_party/WorkshopAndroidDownloader-LICENSE.txt',
    'third_party/SharpCompress-LICENSE.txt'
)

$sourceHashes=@{}
foreach($relativePath in $requiredFiles){
    $sourcePath=Join-Path $SourceRoot $relativePath
    $packagePath=Join-Path $Directory $relativePath
    if(!(Test-Path -LiteralPath $sourcePath -PathType Leaf)){
        throw ("仓库缺少必须分发的来源/许可证文件："+$relativePath)
    }
    if(!(Test-Path -LiteralPath $packagePath -PathType Leaf)){
        throw ("发布目录缺少必须分发的来源/许可证文件："+$relativePath)
    }
    if((Get-Item -LiteralPath $sourcePath).Length -le 0 -or
       (Get-Item -LiteralPath $packagePath).Length -le 0){
        throw ("来源/许可证文件为空："+$relativePath)
    }
    $sourceHash=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    $packageHash=(Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    if($packageHash -cne $sourceHash){
        throw ("发布目录中的来源/许可证文件与仓库源文件不一致："+$relativePath)
    }
    $sourceHashes[$relativePath]=$sourceHash
}

if($ArchivePath){
    $ArchivePath=[IO.Path]::GetFullPath($ArchivePath)
    if(!(Test-Path -LiteralPath $ArchivePath -PathType Leaf) -or
       (Get-Item -LiteralPath $ArchivePath).Length -le 0){
        throw '发布 ZIP 不存在或为空。'
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try{
        foreach($relativePath in $requiredFiles){
            $entries=@($archive.Entries | Where-Object {
                $entryPath=$_.FullName.Replace([string][char]92,'/').TrimStart('/')
                $entryPath -ceq $relativePath -or
                    $entryPath.EndsWith('/'+$relativePath,[StringComparison]::Ordinal)
            })
            if($entries.Count -ne 1 -or $entries[0].Length -le 0){
                throw ("发布 ZIP 中的来源/许可证文件缺失、为空或重复："+$relativePath)
            }
            $stream=$entries[0].Open()
            $algorithm=[Security.Cryptography.SHA256]::Create()
            try{
                $archiveHash=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')
            }
            finally{
                $algorithm.Dispose()
                $stream.Dispose()
            }
            if($archiveHash -cne $sourceHashes[$relativePath]){
                throw ("发布 ZIP 中的来源/许可证文件与仓库源文件不一致："+$relativePath)
            }
        }
    }
    finally{$archive.Dispose()}
    Write-Output 'PASS 发布目录与 ZIP 均完整包含来源标注、第三方声明和两份许可证。'
}
else{
    Write-Output 'PASS 发布目录完整包含来源标注、第三方声明和两份许可证。'
}
