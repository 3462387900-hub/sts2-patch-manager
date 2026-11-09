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
  $layout = Join-Path $work '尖塔修补站测试目录'
  [IO.Directory]::CreateDirectory($layout) | Out-Null
  [IO.File]::WriteAllText((Join-Path $layout 'SpirePatch.exe'),'apphost fixture')
  [IO.Directory]::CreateDirectory((Join-Path $layout 'third_party')) | Out-Null
  [IO.Directory]::CreateDirectory((Join-Path $layout '数据')) | Out-Null
  [IO.File]::WriteAllText((Join-Path $layout 'third_party/license.txt'),'keep third-party license')
  [IO.File]::WriteAllText((Join-Path $layout '数据/个人设置.json'),'keep user data')
  $documentNames = @('使用说明.md','发布指南.md','补丁发布指南.md','本版更新内容.md','鸣谢与来源.md','THIRD-PARTY-NOTICES.md')
  foreach ($name in $documentNames) {
   [IO.File]::WriteAllText((Join-Path $layout $name),('fixture '+$name))
  }
  & $layoutScript -ApplicationDirectory $layout
  $shortcut = Join-Path $layout '00-启动尖塔修补站.lnk'
  if (!(Test-Path -LiteralPath $shortcut -PathType Leaf)) { throw 'Chinese Windows shortcut was not created.' }
  if (Test-Path -LiteralPath (Join-Path $layout '00-启动尖塔修补站.exe')) { throw 'A duplicate executable launcher must not be created.' }
  $shortcutProbe = Join-Path $work 'portable-layout-shortcut-inspect.lnk'
  [IO.File]::Copy($shortcut, $shortcutProbe, $false)
  $shell = New-Object -ComObject WScript.Shell
  try {
   $link = $shell.CreateShortcut($shortcutProbe)
   try {
    if ([IO.Path]::GetFullPath($link.TargetPath) -ne [IO.Path]::GetFullPath((Join-Path $layout 'SpirePatch.exe'))) { throw 'Chinese shortcut target is incorrect.' }
    if ([IO.Path]::GetFullPath($link.WorkingDirectory) -ne [IO.Path]::GetFullPath($layout)) { throw 'Chinese shortcut working directory is incorrect.' }
   } finally {
    if ([Runtime.InteropServices.Marshal]::IsComObject($link)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
   }
  } finally {
   if ([Runtime.InteropServices.Marshal]::IsComObject($shell)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
  }
  [IO.File]::Delete($shortcutProbe)
  foreach ($name in $documentNames) {
   if (Test-Path -LiteralPath (Join-Path $layout $name)) { throw ('Document remained in the application root: '+$name) }
   if (!(Test-Path -LiteralPath (Join-Path $layout ('使用说明/'+$name)) -PathType Leaf)) { throw ('Grouped document is missing: '+$name) }
  }
  if (!(Test-Path -LiteralPath (Join-Path $layout 'third_party/license.txt')) -or
      (Get-Content -LiteralPath (Join-Path $layout '数据/个人设置.json') -Raw) -ne 'keep user data') {
   throw 'Directory cleanup moved or modified runtime data or third-party licenses.'
  }
  # Running the organizer twice must be safe and must not duplicate or damage the shortcut.
  & $layoutScript -ApplicationDirectory $layout

  $conflict = Join-Path $work '快捷方式冲突测试目录'
  [IO.Directory]::CreateDirectory($conflict) | Out-Null
  [IO.File]::WriteAllText((Join-Path $conflict 'SpirePatch.exe'),'apphost fixture')
  $wrongTarget = Join-Path $work 'wrong-target.exe'
  [IO.File]::WriteAllText($wrongTarget,'do not overwrite shortcut')
  $conflictShortcut = Join-Path $conflict '00-启动尖塔修补站.lnk'
  $conflictSource = Join-Path $work 'conflict-source.lnk'
  $shell = New-Object -ComObject WScript.Shell
  try {
   $link = $shell.CreateShortcut($conflictSource)
   try { $link.TargetPath = $wrongTarget; $link.WorkingDirectory = $work; $link.Save() }
   finally { if ([Runtime.InteropServices.Marshal]::IsComObject($link)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) } }
  } finally {
   if ([Runtime.InteropServices.Marshal]::IsComObject($shell)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
  }
  [IO.File]::Move($conflictSource, $conflictShortcut)
  $rejected = $false
  try { & $layoutScript -ApplicationDirectory $conflict } catch { $rejected = $true }
  if (!$rejected) { throw 'Organizer overwrote a pre-existing shortcut with a different target.' }
  $conflictInspect = Join-Path $work 'conflict-inspect.lnk'
  [IO.File]::Copy($conflictShortcut, $conflictInspect, $false)
  $shell = New-Object -ComObject WScript.Shell
  try {
   $link = $shell.CreateShortcut($conflictInspect)
   try {
    if ([IO.Path]::GetFullPath($link.TargetPath) -ne [IO.Path]::GetFullPath($wrongTarget)) { throw 'Conflicting shortcut was modified.' }
   } finally {
    if ([Runtime.InteropServices.Marshal]::IsComObject($link)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
   }
  } finally {
   if ([Runtime.InteropServices.Marshal]::IsComObject($shell)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
  }
  Write-Output 'PASS portable layout creates a verified Chinese shortcut, groups docs, preserves runtime data and refuses shortcut collisions.'
 }
 Write-Output 'PASS clean release packaging excludes data, unlisted files and rejects unsafe manifests.'
}finally{
 if ([IO.Directory]::Exists($work)) { [IO.Directory]::Delete($work,$true) }
}
