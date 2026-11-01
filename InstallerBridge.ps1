param(
 [Parameter(Mandatory=$true)][ValidateSet('Preview','Install','RollbackPreview','Rollback')][string]$Mode,
 [Parameter(Mandatory=$true)][string]$PackageDirectory,
 [Parameter(Mandatory=$true)][string]$GameRoot,
 [Parameter(Mandatory=$true)][string]$ResultPath,
 [string]$RequestPath
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$mutex=$null;$held=$false
function Digest($Value) {
 $bytes=[Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Depth 15 -Compress))
 $alg=[Security.Cryptography.SHA256]::Create()
 try{return [BitConverter]::ToString($alg.ComputeHash($bytes)).Replace('-','')}finally{$alg.Dispose()}
}
function Latest-Journal([string]$Root) {
 $parent=Safe-Child $Root 'mod_fix_backups'
 if(-not(Test-Path -LiteralPath $parent)){return $null}
 foreach($dir in @(Get-ChildItem -LiteralPath $parent -Directory -Filter 'STS2-AllFixes-20261102-*'|Sort-Object Name -Descending)){
  $journal=Safe-Child $dir.FullName 'restore.json'
  if(-not(Test-Path -LiteralPath $journal)){continue}
  $r=Read-Json $journal
  if($r.status -eq 'prepared'){throw 'Incomplete previous transaction: inspect the retained backup before continuing.'}
  if($r.status -eq 'installed'){
   if($r.version -notin @('20261102.1','20261102.2')){throw 'Unsupported backup version.'}
   return [pscustomobject]@{folder=$dir.FullName;journal=$journal;record=$r}
  }
 }
 return $null
}
function Rollback-State([string]$Root,$Chosen) {
 if(-not $Chosen){return @()}
 $rows=@();$seen=@{}
 foreach($f in @($Chosen.record.files)){
  if(-not $f.relative.StartsWith('mods\',[StringComparison]::OrdinalIgnoreCase) -or $f.operation -notin @('write','remove')){throw 'Invalid backup target or operation.'}
  if($seen.ContainsKey($f.relative)){throw 'Duplicate backup target.'};$seen[$f.relative]=$true
  $target=Safe-Child $Root $f.relative
  if($f.operation -eq 'remove'){if(Test-Path -LiteralPath $target){throw "Retired file reappeared; refusing rollback: $target"}}
  elseif(-not(Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Sha $target) -ne $f.newHash){throw "File changed since installation; refusing rollback: $target"}
  if($f.existed){$original=Safe-Child $Chosen.folder $f.backup;if(-not(Test-Path -LiteralPath $original -PathType Leaf) -or (Get-Sha $original) -ne $f.oldHash){throw 'Original backup checksum failed.'}}
  $action=if($f.existed){'restore'}else{'remove-added'}
  $rows+=,[pscustomobject]@{relative=$f.relative;operation=$action;existed=$f.existed;oldHash=$f.newHash;newHash=$f.oldHash}
 }
 return $rows
}
try {
 if([string]::IsNullOrWhiteSpace($GameRoot)){throw 'Game folder must be explicitly selected.'}
 . (Join-Path $PackageDirectory 'Common.ps1')
 . (Join-Path (Split-Path -Parent $ResultPath) 'SelectionBridge.ps1')
 $request=Read-Json $RequestPath
 $selected=@($request.selectedIds | Where-Object {$_})
 function Plan-For([string]$Root){if($selected.Count -gt 0){return Get-SelectedPlan $Root $selected}else{return Get-Plan $Root}}
 $root=Select-Game $GameRoot
 $mutexName='Local\SpirePatch-'+(Digest $root.ToLowerInvariant())
 $mutex=New-Object Threading.Mutex($false,$mutexName)
 try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true}
 if(-not $held){throw 'Another manager transaction is using this game folder.'}
 Assert-Closed $root
 Check-Package
 if($Mode -in @('Preview','Install')){
  $disabledRoot=Safe-Child $root '已停用模组_尖塔修补站'
  $scope=if($selected.Count -gt 0){$selected}else{@((Read-Json (Join-Path $PackageRoot 'plan.json')).components|ForEach-Object{$_.id})}
  if(Test-Path -LiteralPath $disabledRoot){foreach($dir in Get-ChildItem -LiteralPath $disabledRoot -Directory){
   $record=Read-Json (Safe-Child $root ($dir.FullName.Substring($root.Length+1)+'\记录.json'))
   if($record.Status -notin @('enabled','failed-restored') -and $record.ModId -in $scope){throw "Mod is disabled: $($record.ModId). Enable it in the mod manager first."}
  }}
 }
 $result=[ordered]@{success=$true;mode=$Mode;root=$root;version='20261102.2';token='';backup='';message='';files=@();changed=0;checkedFiles=@();selectedIds=$selected}
 if($Mode -in @('Preview','Install')){
  $null=Latest-Journal $root
  $plan=Plan-For $root
  $context=if($plan.PSObject.Properties['context']){@($plan.context)}else{@()}
  $snapshot=[ordered]@{root=$root;version=$plan.version;selected=@($selected|Sort-Object);context=$context;all=@($plan.all|Select-Object relative,existed,oldHash,newHash,operation);changes=@($plan.changes|Select-Object relative,existed,oldHash,newHash,operation)}
  $result.token=Digest $snapshot
  $result.files=@($plan.changes|Select-Object relative,existed,oldHash,newHash,operation)
  $result.checkedFiles=@($plan.all|Select-Object relative,existed,oldHash,newHash,operation)
  $result.changed=@($plan.changes).Count
  if($Mode -eq 'Install'){
   $request=Read-Json $RequestPath
   if($request.token -ne $result.token){throw 'Preview changed: check the new plan before confirming again.'}
   if($selected.Count -gt 0){
    Apply-Plan $root $plan 6>&1 | ForEach-Object {
     if($_.ToString().StartsWith('Installed 11-component fix bundle;')){Write-Host "Installed selected batch: $($selected.Count) patches; $(@($plan.changes).Count) files changed."}
     else{Write-Host $_}
    }
   }else{Apply-Plan $root $plan}
   if($result.changed -gt 0){$latest=Latest-Journal $root;if(-not $latest){throw 'Installation journal not found after writing.'};$result.backup=$latest.folder}
   $after=Plan-For $root
   if(@($after.changes).Count -ne 0){throw 'Final verification failed.'}
   $result.message=if($result.changed -eq 0){'AlreadyInstalled'}else{'Installed'}
  }else{$result.message='ReadyToInstall'}
 }else{
  $chosen=Latest-Journal $root
  $rows=@(Rollback-State $root $chosen)
  $result.files=$rows;$result.changed=$rows.Count
  $result.backup=if($chosen){$chosen.folder}else{''}
  $result.token=Digest ([ordered]@{root=$root;backup=$result.backup;files=$rows})
  if($Mode -eq 'Rollback'){
   $request=Read-Json $RequestPath
   if($request.token -ne $result.token){throw 'Preview changed: check the new plan before confirming again.'}
   if($chosen){
    Undo-Latest $root
    if((Read-Json $chosen.journal).status -ne 'rolled-back'){throw 'Rollback journal verification failed.'}
    foreach($f in @($chosen.record.files)){
     $path=Safe-Child $root $f.relative
     if($f.existed){if(-not(Test-Path -LiteralPath $path) -or (Get-Sha $path) -ne $f.oldHash){throw 'Restored file verification failed.'}}
     elseif(Test-Path -LiteralPath $path){throw 'Newly added file remains after rollback.'}
    }
   }
   $result.message=if($chosen){'RolledBack'}else{'NothingToRollback'}
  }else{$result.message=if($chosen){'ReadyToRollback'}else{'NothingToRollback'}}
 }
 $result|ConvertTo-Json -Depth 15|Set-Content -LiteralPath $ResultPath -Encoding UTF8
}catch{
 [ordered]@{success=$false;mode=$Mode;message=$_.Exception.Message}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $ResultPath -Encoding UTF8
 Write-Error $_ -ErrorAction Continue
 exit 1
}finally{if($held){$mutex.ReleaseMutex()};if($mutex){$mutex.Dispose()}}
