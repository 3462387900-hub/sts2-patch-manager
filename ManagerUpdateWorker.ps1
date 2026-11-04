param([Parameter(Mandatory=$true)][string]$Request,[Parameter(Mandatory=$true)][string]$RequestHash,[switch]$Recover,[switch]$TestMode,[int]$FailAfter=0,[switch]$RestartProbe,[switch]$FailStartup)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$utf8=New-Object Text.UTF8Encoding($false)
$job=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Request))
$root=$null; $held=$false; $mutex=$null; $changed=$false; $canRestart=$false; $exitCode=1
function Child([string]$base,[string]$relative) {
 if([IO.Path]::IsPathRooted($relative) -or $relative -match '[:<>"|?*\x00-\x1f]'){throw 'Unsafe update path.'}
 foreach($part in ($relative -split '[/\\]')){if(!$part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)'){throw 'Unsafe update path.'}}
 $full=[IO.Path]::GetFullPath([IO.Path]::Combine($base,$relative)); $prefix=[IO.Path]::GetFullPath($base).TrimEnd('\')+'\'
 if(!$full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Update path escaped its directory.'}
 for($p=$full;$p;$p=[IO.Path]::GetDirectoryName($p)){if([IO.File]::Exists($p) -or [IO.Directory]::Exists($p)){if(([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Linked paths are unsupported.'}}}
 return $full
}
function Hash([string]$path){if(![IO.File]::Exists($path)){return $null};$stream=[IO.File]::OpenRead($path);$algorithm=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$algorithm.Dispose()}}
function WriteAtomic([string]$path,[string]$text){$temp=$path+'.tmp';[IO.File]::WriteAllText($temp,$text,$utf8);if([IO.File]::Exists($path)){[IO.File]::Replace($temp,$path,[NullString]::Value)}else{[IO.File]::Move($temp,$path)}}
function Record([string]$status,[string]$message){
 $value=[ordered]@{status=$status;message=$message;job=$job;version=$plan.ToVersion;time=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json
 WriteAtomic (Child $job 'result.json') $value
 WriteAtomic (Child $root '数据/管理器更新/最近结果.json') $value
}
function VerifyPlan {
 if($plan.Schema -ne 1 -or $plan.FromVersion -notmatch '^\d+\.\d+\.\d+$' -or $plan.ToVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$plan.ToVersion -le [version]$plan.FromVersion){throw 'Invalid update plan.'}
 if(@($plan.Files).Count -lt 4 -or @($plan.Files).Count -gt 2000){throw 'Invalid file list.'}
 $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
 foreach($f in $plan.Files){
  $first=($f.Path -split '/')[0]
  if($first -in @('数据','mods','mod_fix_backups','SlayTheSpire2.exe') -or $first.StartsWith('.') -or $f.Path.Contains('\') -or !$seen.Add($f.Path)){throw 'Reserved or duplicate update path.'}
  if(!$f.OldHash -and !$f.NewHash){throw 'Empty file plan.'}
  foreach($h in @($f.OldHash,$f.NewHash)){if($h -and $h -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Invalid file hash.'}}
  $null=Child $root $f.Path; $null=Child $payload $f.Path; $null=Child $backup $f.Path
 }
 if(!$seen.Contains('manager-files.json') -or !$seen.Contains('SpirePatch.exe')){throw 'Missing application files.'}
}
function VerifyBeforeUpdate {
 foreach($f in $plan.Files){
  $destination=Child $root $f.Path
  if((Hash $destination) -ne $f.OldHash -or [IO.Directory]::Exists($destination)){throw ('程序文件发生变化，请重新检查更新：'+$f.Path)}
  if($f.NewHash -and (Hash (Child $payload $f.Path)) -ne $f.NewHash){throw ('更新文件校验失败：'+$f.Path)}
 }
}
function PutFile([string]$source,[string]$destination,[string]$expected,[string]$oldExpected){
 $before=Hash $destination
 if(($oldExpected -and $before -ne $oldExpected) -or (!$oldExpected -and ([IO.File]::Exists($destination) -or [IO.Directory]::Exists($destination)))){throw 'Destination changed while updating.'}
 $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
 $temp=$destination+'.'+[Guid]::NewGuid().ToString('N')+'.part'
 try{[IO.File]::Copy($source,$temp,$false);if((Hash $temp) -ne $expected){throw 'Copied file checksum mismatch.'};if($oldExpected){if((Hash $destination) -ne $oldExpected){throw 'Destination changed while copying.'};[IO.File]::Replace($temp,$destination,[NullString]::Value)}else{[IO.File]::Move($temp,$destination)}}finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function RestoreFiles {
 # Validate everything first: recovery must never overwrite a later/manual edit.
 foreach($f in $plan.Files){
  if([IO.Directory]::Exists((Child $root $f.Path))){throw ('文件位置被目录占用，停止恢复：'+$f.Path)}
  $actual=Hash (Child $root $f.Path)
  if($actual -and $actual -ne $f.OldHash -and $actual -ne $f.NewHash){throw ('文件已被后续修改，停止恢复：'+$f.Path)}
  if($f.OldHash -and $f.OldHash -ne $f.NewHash -and (Hash (Child $backup $f.Path)) -ne $f.OldHash){throw ('备份校验失败：'+$f.Path)}
 }
 foreach($f in $plan.Files){$target=Child $root $f.Path;$actual=Hash $target;if($actual -eq $f.OldHash){continue};if($actual -and $actual -ne $f.NewHash){throw 'Recovery target changed.'};if($f.OldHash){PutFile (Child $backup $f.Path) $target $f.OldHash $actual}elseif([IO.File]::Exists($target)){[IO.File]::Delete($target)}}
 foreach($f in $plan.Files){if((Hash (Child $root $f.Path)) -ne $f.OldHash){throw 'Recovery verification failed.'}}
}
try {
 if($RequestHash -notmatch '^[A-Fa-f0-9]{64}$' -or (Hash $Request) -ne $RequestHash){throw 'Update request checksum mismatch.'}
 $plan=Get-Content -LiteralPath $Request -Raw -Encoding UTF8 | ConvertFrom-Json
 $root=[IO.Path]::GetFullPath($plan.Root).TrimEnd('\')
 if([IO.File]::Exists([IO.Path]::Combine($root,'SlayTheSpire2.exe'))){throw 'The manager must be in its own directory.'}
 $expectedJob=Child $root ('数据/管理器更新/'+[IO.Path]::GetFileName($job))
 if($job -ne $expectedJob -or [IO.Path]::GetFileName($job) -notmatch '^[a-f0-9]{32}$'){throw 'Invalid updater working directory.'}
 $payload=Child $job 'payload';$backup=Child $job 'backup'
 if($TestMode -or $FailAfter -gt 0 -or $RestartProbe -or $FailStartup -or $plan.Fixture){if(!$TestMode -or !$plan.Fixture -or ![IO.File]::Exists((Child $root '.spirepatch-update-fixture'))){throw 'Test switches require an isolated fixture.'}}
 VerifyPlan
 if(!$Recover){VerifyBeforeUpdate;[IO.File]::WriteAllText((Child $job 'ready'),$RequestHash,$utf8)}
 $canRestart=$true
 if((!$TestMode -or $RestartProbe) -and !$Recover){
  $wait=[Diagnostics.Stopwatch]::StartNew()
  while($true){
   if([IO.File]::Exists((Child $job 'cancel'))){$canRestart=$false;throw '更新已取消，程序保持不变。'}
   try{$parent=[Diagnostics.Process]::GetProcessById([int]$plan.ParentId)}catch{break}
   try{if($parent.StartTime.ToUniversalTime().Ticks -ne [long]$plan.ParentStartTicks){break}}finally{$parent.Dispose()}
   if($wait.Elapsed.TotalSeconds -gt 90){$canRestart=$false;throw '旧窗口未退出，未替换程序。'};Start-Sleep -Milliseconds 150
  }
 }
 $hasher=[Security.Cryptography.SHA256]::Create();try{$digest=[BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))).Replace('-','')}finally{$hasher.Dispose()}
 $mutex=New-Object Threading.Mutex($false,('Local\SpireManager-'+$digest))
 try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true}
 if(!$held){$canRestart=$false;throw '另一个管理器窗口正在运行，请先关闭后重试。'}
 if($Recover){RestoreFiles;Record 'restored' '已恢复上一版管理器，设置与游戏文件保持不变。';$exitCode=0}
 else {
  VerifyBeforeUpdate
  if([IO.Directory]::Exists($backup)){throw 'Backup directory already exists.'}
  $null=[IO.Directory]::CreateDirectory($backup)
  foreach($f in $plan.Files){if($f.OldHash -and $f.OldHash -ne $f.NewHash){$save=Child $backup $f.Path;$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($save));[IO.File]::Copy((Child $root $f.Path),$save,$false);if((Hash $save) -ne $f.OldHash){throw 'Backup checksum mismatch.'}}}
  VerifyBeforeUpdate;Record 'applying' '正在替换管理器程序文件。';$changed=$true;$count=0
  foreach($f in $plan.Files){
   if($f.OldHash -eq $f.NewHash){continue}
   $destination=Child $root $f.Path
   if((Hash $destination) -ne $f.OldHash){throw 'Destination changed before replacement.'}
   if($f.NewHash){PutFile (Child $payload $f.Path) $destination $f.NewHash $f.OldHash}elseif([IO.File]::Exists($destination)){[IO.File]::Delete($destination)}
   $count++;if($TestMode -and $FailAfter -gt 0 -and $count -ge $FailAfter){throw 'Injected isolated update failure.'}
  }
  foreach($f in $plan.Files){if((Hash (Child $root $f.Path)) -ne $f.NewHash){throw 'Installed file checksum mismatch.'}}
  Record 'complete' '管理器已更新，设置、缓存和游戏文件保持不变。';$exitCode=0
 }
}catch{
 $message=$_.Exception.Message
 try{[IO.File]::WriteAllText([IO.Path]::Combine($job,'error.txt'),($_ | Out-String),$utf8)}catch{}
 if($changed){try{RestoreFiles;Record 'restored' ('更新未完成，已自动恢复上一版：'+$message)}catch{$canRestart=$false;try{Record 'restore-failed' ('自动恢复未完成，请保留更新备份：'+$_.Exception.Message)}catch{}}}
 elseif($root){try{Record 'failed' $message}catch{}}
}finally{
 if($held){$mutex.ReleaseMutex()};if($mutex){$mutex.Dispose()}
}
if($canRestart -and (!$TestMode -or $RestartProbe)){
 try{
  $exe=Child $root 'SpirePatch.exe'
  if($exitCode -ne 0 -or $Recover){
   if($RestartProbe){Start-Process -FilePath $exe -WorkingDirectory $root -ArgumentList @('--update-restart-probe',('"'+(Child $job 'rollback-proof.json')+'"')) -WindowStyle Hidden | Out-Null}else{Start-Process -FilePath $exe -WorkingDirectory $root | Out-Null}
  }else{
   $health=Child $job 'restart-proof.json'
   if([IO.File]::Exists($health)){[IO.File]::Delete($health)}
   if($RestartProbe){$startArgs=@('--update-restart-probe',('"'+$health+'"'));if($FailStartup){$startArgs=@('--update-restart-fail')};$launched=Start-Process -FilePath $exe -WorkingDirectory $root -ArgumentList $startArgs -WindowStyle Hidden -PassThru}
   else{$launched=Start-Process -FilePath $exe -WorkingDirectory $root -ArgumentList @('--manager-update-complete',('"'+$job+'"')) -PassThru}
   $confirmed=$false;$watch=[Diagnostics.Stopwatch]::StartNew()
   while($watch.Elapsed.TotalSeconds -lt 30){
    if([IO.File]::Exists($health)){try{$proof=[IO.File]::ReadAllText($health)|ConvertFrom-Json;if($proof.version -eq $plan.ToVersion -and $proof.pid -eq $launched.Id){$confirmed=$true;break}}catch{}}
    if($launched.HasExited){break};Start-Sleep -Milliseconds 150
   }
   if(!$confirmed){
    if(!$launched.HasExited){$null=$launched.CloseMainWindow();$null=$launched.WaitForExit(5000)}
    if(!$launched.HasExited){throw '新版未确认启动且仍在运行，请关闭窗口后使用恢复上一版命令。'}
    $mutex=New-Object Threading.Mutex($false,('Local\SpireManager-'+$digest));$held=$false
    try{try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true};if(!$held){throw '恢复时仍有管理器运行。'};RestoreFiles;Record 'restored' '新版未能正常打开，已恢复上一版管理器。'}finally{if($held){$mutex.ReleaseMutex()};$mutex.Dispose();$held=$false}
    if($RestartProbe){Start-Process -FilePath $exe -WorkingDirectory $root -ArgumentList @('--update-restart-probe',('"'+(Child $job 'rollback-proof.json')+'"')) -WindowStyle Hidden | Out-Null}else{Start-Process -FilePath $exe -WorkingDirectory $root | Out-Null};$exitCode=1
   }else{Record 'complete' '新版已正常打开，设置、缓存和游戏文件保持不变。'}
   $launched.Dispose()
  }
 }catch{try{Record 'restart-failed' ('自动打开未确认，请保留更新备份：'+$_.Exception.Message)}catch{}}
}
exit $exitCode
