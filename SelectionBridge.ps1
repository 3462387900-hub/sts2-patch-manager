# Loaded only by the embedded bridge for the exact approved archive.
function Get-SelectedPlan([string]$Root,[string[]]$Selected) {
 Check-Package
 $spec=Read-Json (Join-Path $PackageRoot 'plan.json')
 $map=Inventory $Root; $known=@{}; $metadata=@{}; $byPatch=@{}; $chosen=@{}
 foreach($c in $spec.components){
  $known[$c.id]=$c
  $m=Read-Json (Safe-Child $PackageRoot ('payload/'+$c.id+'/patch-info/patch.json'))
  $metadata[$c.id]=$m;$byPatch[$m.id]=$c.id
 }
 foreach($id in $Selected){if(-not $known.ContainsKey($id) -or $chosen.ContainsKey($id)){throw "Invalid patch selection: $id"};$chosen[$id]=$true}
 if($chosen.Count -eq 0){throw 'Empty patch selection.'}
 $all=@();$changes=@();$projected=@{};$context=@{};$mods=Safe-Child $Root 'mods'
 function Resolve-File([string]$Mod,[string]$Relative){
  if(-not $Mod){return Safe-Child $Root $Relative}
  if($map.ContainsKey($Mod)){
   if($Relative -ieq ($Mod+'.json')){return $map[$Mod].FullName}
   return Safe-Child $map[$Mod].DirectoryName $Relative
  }
  return Safe-Child (Safe-Child $mods $Mod) $Relative
 }
 function Current-Hash([string]$Path){
  [void](Safe-Child $Root $Path.Substring($Root.Length+1))
  $hash=if(Test-Path -LiteralPath $Path -PathType Leaf){Get-Sha $Path}else{''}
  $context[$Path.Substring($Root.Length+1)]=$hash
  return $hash
 }
 foreach($id in @($chosen.Keys|Sort-Object)){
  $c=$known[$id];$mf=if($map.ContainsKey($id)){$map[$id]}else{$null}
  if($c.required -and -not $mf){throw "Required original mod missing: $id"}
  $folder=if($mf){$mf.DirectoryName}elseif(-not $c.required){Safe-Child (Safe-Child $mods '_SpirePatch_Fixes') $id}else{Safe-Child $mods $id}
  if($id -eq 'StartingDeckTweaks' -and (Test-Path -LiteralPath (Safe-Child $folder 'StartingDeckTweaks.pck'))){throw 'Unexpected StartingDeckTweaks PCK.'}
  foreach($f in $c.files){
   $target=if($f.isManifest -and $mf){$mf.FullName}else{Safe-Child $folder $f.name}
   $old=Current-Hash $target;$exists=Test-Path -LiteralPath $target -PathType Leaf
   if($c.required -and -not $exists -and -not ($f.PSObject.Properties['allowMissing'] -and $f.allowMissing -eq $true -and $f.name.StartsWith('patch-info/'))){throw "Original mod file missing: $target"}
   if($exists -and $old -notin $f.allowedExisting){throw "Unsupported existing file: $target"}
   $item=[pscustomobject]@{relative=$target.Substring($Root.Length+1);target=$target;source=(Safe-Child $PackageRoot $f.source);existed=$exists;oldHash=$old;newHash=$f.sha256;operation='write';backup=''}
   $all+=,$item;$projected[$target]=$f.sha256;if($old -ne $f.sha256){$changes+=,$item}
  }
 }
 function Matches-Profile($m,[bool]$Future){
  foreach($profile in $m.compatibility.profiles){
   $ok=$true
   foreach($excluded in $profile.excluded_mod_ids){if($map.ContainsKey($excluded) -or ($Future -and $chosen.ContainsKey($excluded))){$ok=$false}}
   foreach($file in $profile.game_files){$path=Resolve-File '' $file.relative_path;if((Current-Hash $path) -ne $file.sha256){$ok=$false}}
   foreach($mod in $profile.required_mods){
    if(-not $map.ContainsKey($mod.mod_id) -and -not ($Future -and $chosen.ContainsKey($mod.mod_id))){$ok=$false}
    foreach($file in $mod.files){
     $path=Resolve-File $mod.mod_id $file.relative_path;$actual=Current-Hash $path
     $hash=if($Future -and $projected.ContainsKey($path)){$projected[$path]}else{$actual}
     if($hash -ne $file.sha256){$ok=$false}
    }
   }
   if($ok){return $true}
  }
  return $false
 }
 function Assert-Patch([string]$Id,$Visited){
  if($Visited.ContainsKey($Id)){return};$Visited[$Id]=$true
  $m=$metadata[$Id]
  if(-not $chosen.ContainsKey($Id)){
   if(-not $map.ContainsKey($Id)){throw "Required patch missing: $Id"}
   foreach($f in $m.detection.installed_fingerprints){if((Current-Hash (Resolve-File $f.mod_id $f.relative_path)) -ne $f.sha256){throw "Related patch needs update: $Id"}}
  }
  if(-not (Matches-Profile $m $true)){throw "Selected patch compatibility mismatch: $Id"}
  foreach($conflict in $m.relationships.conflicts_patch_ids){
   if(-not $byPatch.ContainsKey($conflict)){throw "Unknown patch conflict: $conflict"}
   $cid=$byPatch[$conflict];if($map.ContainsKey($cid) -or $chosen.ContainsKey($cid)){throw "Conflicting patch: $cid"}
  }
  $required=@($m.relationships.requires_patches)
  foreach($r in $m.relationships.requires_when_mod_present){if($map.ContainsKey($r.mod_id) -or $chosen.ContainsKey($r.mod_id)){$required+=,$r}}
  foreach($r in $required){
   if(-not $byPatch.ContainsKey($r.patch_id)){throw "Unknown patch dependency: $($r.patch_id)"}
   $cid=$byPatch[$r.patch_id];if([version]$metadata[$cid].version -lt [version]$r.minimum_version){throw "Related patch version too old: $cid"}
   Assert-Patch $cid $Visited
  }
 }
 $visited=@{}
 foreach($id in @($chosen.Keys|Sort-Object)){Assert-Patch $id $visited}
 foreach($id in @($metadata.Keys|Sort-Object)){
  if(-not $map.ContainsKey($id) -or $chosen.ContainsKey($id)){continue}
  $affected=$false
  foreach($profile in $metadata[$id].compatibility.profiles){foreach($mod in $profile.required_mods){if($chosen.ContainsKey($mod.mod_id)){$affected=$true}}}
  if($affected){Assert-Patch $id $visited}
 }
 # Retired Chorale code is superseded by SpireTogetherFix. Do not remove it
 # when installing an unrelated patch such as a display-only fix.
 if($chosen.ContainsKey('SpireTogetherFix')){
  foreach($retired in $spec.retired){
   if(-not $map.ContainsKey($retired.id)){continue};$mf=$map[$retired.id];$expected=@()
   foreach($f in $retired.files){
    $target=if($f.isManifest){$mf.FullName}else{Safe-Child $mf.DirectoryName $f.name}
    if((Current-Hash $target) -ne $f.sha256){throw "Unrecognized legacy fix: $target"}
    $expected+=,$target;$changes+=,[pscustomobject]@{relative=$target.Substring($Root.Length+1);target=$target;source='';existed=$true;oldHash=$f.sha256;newHash='';operation='remove';backup=''}
   }
   foreach($extra in Get-ChildItem -LiteralPath $mf.DirectoryName -Force){if($extra.FullName -notin $expected){throw "Legacy fix has unknown files: $($extra.FullName)"}}
  }
 }
 # Bind the preview to actual dependency hashes and the manifest inventory too.
 foreach($id in @($map.Keys|Sort-Object)){$null=Current-Hash $map[$id].FullName}
 $state=@($context.Keys|Sort-Object|ForEach-Object{[pscustomobject]@{path=$_;sha256=$context[$_]}})
 return [pscustomobject]@{all=$all;changes=$changes;version=$spec.version;context=$state;selected=@($chosen.Keys|Sort-Object)}
}
