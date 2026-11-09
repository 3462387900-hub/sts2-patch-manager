param()
$ErrorActionPreference='Stop'
$work=Join-Path ([IO.Path]::GetTempPath()) ('spirepatch-release-fixture-'+[Guid]::NewGuid().ToString('N'))
$app=Join-Path $work 'app'
[IO.Directory]::CreateDirectory($app) | Out-Null
try {
 $files=@{}
 foreach ($name in @('SpirePatch.exe','SpirePatch.dll','SpirePatch.deps.json','SpirePatch.runtimeconfig.json','subfolder/allowed.json')) {
  $path=Join-Path $app ($name.Replace('/','\'))
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
  [IO.File]::WriteAllText($path,'test program content '+$name)
  $files[$name]=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
 }
 [IO.Directory]::CreateDirectory((Join-Path $app '数据')) | Out-Null
 [IO.File]::WriteAllText((Join-Path $app '数据/个人设置.json'),'SHOULD-NOT-BE-PACKAGED')
 [IO.File]::WriteAllText((Join-Path $app 'startup-error.txt'),'SHOULD-NOT-BE-PACKAGED')
 [IO.File]::WriteAllText((Join-Path $app 'unlisted.tmp'),'SHOULD-NOT-BE-PACKAGED')
 $manifest=@{Schema=1;Product='SpirePatch';Version='9.9.9';Files=$files}
 $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $app 'manager-files.json') -Encoding UTF8
 $archive=Join-Path $work 'release.zip'
 & (Join-Path $PSScriptRoot '创建干净发布包.ps1') -ApplicationDirectory $app -ArchivePath $archive -Version '9.9.9'
 if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Package script failed.' }
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $zip=[IO.Compression.ZipFile]::OpenRead($archive)
 try {
  $names=@($zip.Entries | Where-Object { !$_.FullName.EndsWith('/') } | ForEach-Object {$_.FullName})
  if ($names.Count -ne 6 -or $names -notcontains '尖塔修补站-v9.9.9/manager-files.json') { throw 'Unexpected entries in clean release.' }
  if (@($names | Where-Object {$_ -match '数据|个人设置|startup-error|unlisted'}).Count -ne 0) { throw 'Personal data included in release.' }
 }finally{$zip.Dispose()}
 $files['数据/个人设置.json']=(Get-FileHash -LiteralPath (Join-Path $app '数据/个人设置.json') -Algorithm SHA256).Hash
 $manifest.Files=$files
 $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $app 'manager-files.json') -Encoding UTF8
 try { & (Join-Path $PSScriptRoot '创建干净发布包.ps1') -ApplicationDirectory $app -ArchivePath (Join-Path $work 'bad.zip') -Version '9.9.9'; throw 'Unsafe manifest path accepted.' }
 catch { if ($_.Exception.Message -eq 'Unsafe manifest path accepted.') { throw } }
 $layoutScript = Join-Path $PSScriptRoot '整理玩家发布目录.ps1'
 if (Test-Path -LiteralPath $layoutScript -PathType Leaf) {
  $layout = Join-Path $work 'portable-layout'
  [IO.Directory]::CreateDirectory($layout) | Out-Null
  [IO.File]::WriteAllText((Join-Path $layout 'SpirePatch.exe'),'apphost fixture')
  $documentNames = @('使用说明.md','发布指南.md','补丁发布指南.md','本版更新内容.md','鸣谢与来源.md','THIRD-PARTY-NOTICES.md')
  foreach ($name in $documentNames) {
   [IO.File]::WriteAllText((Join-Path $layout $name),('fixture '+$name))
  }
  & $layoutScript -ApplicationDirectory $layout
  $originalHash = (Get-FileHash -LiteralPath (Join-Path $layout 'SpirePatch.exe') -Algorithm SHA256).Hash
  $launcherHash = (Get-FileHash -LiteralPath (Join-Path $layout '00-启动尖塔修补站.exe') -Algorithm SHA256).Hash
  if ($originalHash -ne $launcherHash) { throw 'Chinese launcher does not match the original apphost.' }
  foreach ($name in $documentNames) {
   if (Test-Path -LiteralPath (Join-Path $layout $name)) { throw ('Document remained in the application root: '+$name) }
   if (!(Test-Path -LiteralPath (Join-Path $layout ('使用说明/'+$name)) -PathType Leaf)) { throw ('Grouped document is missing: '+$name) }
  }
  Write-Output 'PASS portable release layout preserves both launch paths and groups documentation.'
 }
 Write-Output 'PASS clean release packaging excludes data, unlisted files and rejects unsafe manifests.'
}finally{
 if ([IO.Directory]::Exists($work)) { [IO.Directory]::Delete($work,$true) }
}
