param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference='Stop'
$Directory=[IO.Path]::GetFullPath($Directory).TrimEnd('\')
$version=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $Directory 'SpirePatch.dll')).Version.ToString(3)
$files=[ordered]@{}
Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative=$_.FullName.Substring($Directory.Length+1).Replace('\','/')
    if($relative -ne 'manager-files.json' -and !$relative.StartsWith('数据/') -and !$relative.StartsWith('.') -and $relative -ne 'startup-error.txt') {
        $files[$relative]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest=[ordered]@{Schema=1;Product='SpirePatch';Version=$version;Files=$files}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'manager-files.json') -Encoding utf8
Write-Output ('已生成 '+$version+' 程序清单，共 '+$files.Count+' 个文件。')
