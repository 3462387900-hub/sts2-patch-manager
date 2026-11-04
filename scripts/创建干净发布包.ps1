param(
 [Parameter(Mandatory=$true)][string]$ApplicationDirectory,
 [Parameter(Mandatory=$true)][string]$ArchivePath,
 [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference='Stop'
$application=[IO.Path]::GetFullPath($ApplicationDirectory).TrimEnd('\')
$archive=[IO.Path]::GetFullPath($ArchivePath)
$manifestPath=Join-Path $application 'manager-files.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Missing manager-files.json.' }
$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.Schema -ne 1 -or $manifest.Product -ne 'SpirePatch' -or $manifest.Version -ne $Version) { throw 'Invalid release manifest.' }
$entries=@($manifest.Files.PSObject.Properties)
if ($entries.Count -lt 4 -or $entries.Count -gt 2000) { throw 'Unexpected number of release files.' }
$names=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$archiveDir=[IO.Path]::GetDirectoryName($archive)
[IO.Directory]::CreateDirectory($archiveDir) | Out-Null
$stage=Join-Path $archiveDir ('release-stage-'+[Guid]::NewGuid().ToString('N'))
$payload=Join-Path $stage ('尖塔修补站-v'+$Version)
try {
 [IO.Directory]::CreateDirectory($payload) | Out-Null
 foreach ($entry in $entries) {
  $name=[string]$entry.Name
  $first=($name -split '/')[0]
  if ($name.Contains('\') -or $name.StartsWith('/') -or $name.Contains(':') -or $name.Contains('//') -or
      $name.Split('/') -contains '..' -or $name.Split('/') -contains '.' -or
      $first -in @('数据','mods','mod_fix_backups','SlayTheSpire2.exe') -or
      $first.StartsWith('.') -or $first.EndsWith('.part') -or
      !$names.Add($name) -or $name -eq 'manager-files.json') { throw ('Unsafe release manifest entry: '+$name) }
  $source=[IO.Path]::GetFullPath((Join-Path $application ($name.Replace('/','\'))))
  $target=[IO.Path]::GetFullPath((Join-Path $payload ($name.Replace('/','\'))))
  if (!$source.StartsWith(($application+'\'),[StringComparison]::OrdinalIgnoreCase) -or
      !$target.StartsWith(($payload+'\'),[StringComparison]::OrdinalIgnoreCase) -or
      !(Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing or unsafe release file: '+$name) }
  $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
  if ($hash -ne [string]$entry.Value) { throw ('Release file changed after manifest creation: '+$name) }
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
  [IO.File]::Copy($source,$target,$false)
  if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw ('Staged file mismatch: '+$name) }
 }
 foreach ($required in @('SpirePatch.exe','SpirePatch.dll','SpirePatch.deps.json','SpirePatch.runtimeconfig.json')) {
  if (!$names.Contains($required)) { throw ('Missing essential program file: '+$required) }
 }
 [IO.File]::Copy($manifestPath,(Join-Path $payload 'manager-files.json'),$false)
 if ([IO.File]::Exists($archive)) { throw 'Release archive already exists.' }
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 [IO.Compression.ZipFile]::CreateFromDirectory($payload,$archive,[IO.Compression.CompressionLevel]::Optimal,$true)
 $zip=[IO.Compression.ZipFile]::OpenRead($archive)
 try {
  $archivePrefix='尖塔修补站-v'+$Version+'/'
  $found=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach ($item in $zip.Entries) {
   if (!$item.FullName.StartsWith($archivePrefix,[StringComparison]::Ordinal) -or $item.FullName.Contains('\')) { throw 'Archive root is invalid.' }
   if ($item.FullName.EndsWith('/')) { continue }
   $relative=$item.FullName.Substring($archivePrefix.Length)
   if (!$found.Add($relative) -or ($relative -ne 'manager-files.json' -and !$names.Contains($relative))) { throw ('Unexpected entry in update archive: '+$relative) }
  }
  if ($found.Count -ne ($names.Count+1) -or !$found.Contains('manager-files.json')) { throw 'Release archive is incomplete.' }
 } finally {$zip.Dispose()}
 Write-Output ('Release archive contains only '+$names.Count+' listed program files and manager-files.json.')
} finally {
 if ([IO.Directory]::Exists($stage)) { [IO.Directory]::Delete($stage,$true) }
}
