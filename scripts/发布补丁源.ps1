#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$CandidateFile,
    [Parameter(Mandatory=$true)][string]$PayloadZip,
    [Parameter(Mandatory=$true)][string]$ManagerTag,
    [string]$GitHubCli='gh',
    [string]$OutputDirectory='',
    [switch]$Publish
)
$ErrorActionPreference='Stop'
$patchRepo='3462387900-hub/sts2-patches'
$managerRepo='3462387900-hub/sts2-patch-manager'
if($ManagerTag -notmatch '^v\d+\.\d+\.\d+$'){throw 'ManagerTag 必须为正式版本，例如 v0.10.0。'}
if(!$OutputDirectory){$OutputDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/补丁源发布-'+[guid]::NewGuid().ToString('N'))}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
function Api([string]$Route){
    $result=& $GitHubCli api $Route
    if($LASTEXITCODE -ne 0){throw ('GitHub 读取失败：'+$Route)}
    return ($result | ConvertFrom-Json -Depth 100)
}
function ReadRepo([string]$Repo,[string]$Path,[string]$Ref){
    $file=Api ('repos/'+$Repo+'/contents/'+$Path+'?ref='+$Ref)
    if($file.encoding -ne 'base64'){throw ('文件编码异常：'+$Path)}
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($file.content))
}
function Require($Condition,[string]$Message){if(!$Condition){throw $Message}}
function Canonical($Value){
    if($null -eq $Value){return 'null'}
    if($Value -is [System.Management.Automation.PSCustomObject]){
        $parts=foreach($key in @($Value.PSObject.Properties.Name | Sort-Object -CaseSensitive)){
            ($key | ConvertTo-Json -Compress)+':'+(Canonical $Value.$key)
        }
        return '{'+($parts -join ',')+'}'
    }
    if($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]){
        $parts=foreach($entry in $Value){Canonical $entry};return '['+($parts -join ',')+']'
    }
    return (ConvertTo-Json -InputObject $Value -Compress)
}
$candidateText=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $CandidateFile))
$feed=$candidateText | ConvertFrom-Json -Depth 100
Require ($feed.schema_version -eq 1 -and $feed.repository -ceq $patchRepo) '候选源的协议或仓库不匹配。'
Require ($feed.catalog_commit -cmatch '^[a-f0-9]{40}$') 'catalog_commit 必须为固定的 40 位提交。'
$bundle=$feed.bundle
Require ($bundle.install_mode -ceq 'manager_payload') '此脚本用于 manager_payload 发布。'
Require ($bundle.file_name -cmatch '^STS2-[A-Za-z0-9._-]+\.zip$') '载荷文件名无效。'
Require ($bundle.sha256 -match '^[a-f0-9]{64}$' -and $bundle.size -gt 0 -and $bundle.size -le 52428800) '载荷大小或 SHA256 无效。'
$prefix='https://github.com/'+$patchRepo+'/releases/tag/'
Require ($bundle.release_url.StartsWith($prefix,[StringComparison]::Ordinal)) '发布页不属于补丁仓库。'
$patchTag=$bundle.release_url.Substring($prefix.Length)
Require ($patchTag -cmatch '^[A-Za-z0-9._-]+$') '补丁发布标签无效。'
Require ($bundle.download_url -ceq ('https://github.com/'+$patchRepo+'/releases/download/'+$patchTag+'/'+$bundle.file_name)) '下载地址与发布标签不一致。'
$current=Api ('repos/'+$patchRepo+'/contents/manager-feed.json?ref=main')
$release=Api ('repos/'+$patchRepo+'/releases/tags/'+$patchTag)
Require (!$release.draft -and !$release.prerelease) '请先正式发布补丁载荷。'
$assets=@($release.assets | Where-Object name -CEQ $bundle.file_name)
Require ($assets.Count -eq 1) '补丁发布附件必须唯一。'
$asset=$assets[0]
Require ($asset.state -ceq 'uploaded' -and $asset.size -eq $bundle.size -and $asset.digest -ieq ('sha256:'+$bundle.sha256) -and $asset.url -ceq $bundle.asset_api_url) 'GitHub 附件大小、摘要或 API 地址与候选源不一致。'
Require ((Get-Item -LiteralPath $PayloadZip).Length -eq $bundle.size -and (Get-FileHash -LiteralPath $PayloadZip -Algorithm SHA256).Hash -ieq $bundle.sha256) '本地载荷与发布附件的摘要不一致。'
$managerRelease=Api ('repos/'+$managerRepo+'/releases/tags/'+$ManagerTag)
Require (!$managerRelease.draft -and !$managerRelease.prerelease -and $managerRelease.body) '必须先正式发布支持该载荷的管理器，并填写更新说明。'
$managerAssets=@($managerRelease.assets | Where-Object name -CEQ ('SpirePatch-'+$ManagerTag+'-win-x64.zip'))
Require ($managerAssets.Count -eq 1 -and $managerAssets[0].state -ceq 'uploaded' -and $managerAssets[0].digest -match '^sha256:[a-f0-9]{64}$') '管理器正式附件尚未就绪。'
$source=ReadRepo $managerRepo 'BundleInstaller.cs' $ManagerTag
Require ($source -cmatch ('SupportedHash\s*=\s*"'+$bundle.sha256.ToUpperInvariant()+'"') -and $source -match ('SupportedSize\s*=\s*'+$bundle.size+'\s*;')) '已发布管理器没有审核该载荷的哈希和大小；请先接入并发布管理器。'
$snapshot=(ReadRepo $managerRepo 'patch-catalog.json' $ManagerTag) | ConvertFrom-Json -Depth 100
$plan=(ReadRepo $managerRepo 'catalog-plan.json' $ManagerTag) | ConvertFrom-Json -Depth 100
$index=(ReadRepo $patchRepo 'index.json' $feed.catalog_commit) | ConvertFrom-Json -Depth 100
Require ($index.schema_version -ceq '1.0' -and $snapshot.commit -ceq $feed.catalog_commit) '管理器快照与候选目录提交不一致。'
Require (@($index.entries).Count -gt 0 -and @($index.entries).Count -le 100) '补丁目录条目数量异常。'
$ids=@($index.entries.component_id | Sort-Object -Unique)
Require ($ids.Count -eq @($index.entries).Count -and @($index.entries.id | Sort-Object -Unique).Count -eq $ids.Count) '索引包含重复标识。'
Require ((Canonical @($ids)) -ceq (Canonical @($snapshot.patches.component_id | Sort-Object -Unique))) '在线目录与管理器快照的补丁集合不同。'
Require ((Canonical @($ids)) -ceq (Canonical @($plan.components.id | Sort-Object -Unique))) '在线目录与安装配方的补丁集合不同。'
foreach($entry in $index.entries){
    Require ($entry.component_id -cmatch '^[A-Za-z0-9_-][A-Za-z0-9._-]*$' -and $entry.metadata -ceq ('patches/'+$entry.component_id+'/patch-info/patch.json')) '索引包含非法元数据路径。'
    $metadata=(ReadRepo $patchRepo $entry.metadata $feed.catalog_commit) | ConvertFrom-Json -Depth 100
    $embedded=@($snapshot.patches | Where-Object component_id -CEQ $entry.component_id)
    Require ($metadata.id -ceq $entry.id -and $metadata.publication_status -cin @('draft','published')) ('补丁已撤回、状态无效或 ID 不符：'+$entry.component_id)
    Require ($embedded.Count -eq 1 -and (Canonical $metadata) -ceq (Canonical $embedded[0].metadata)) ('管理器快照中的完整说明未同步：'+$entry.component_id)
    # The original eleven records retain historical draft/pending labels. Like
    # Catalog.ReviewedBundle, trust only the entire exact released snapshot;
    # never promote their evidence status or admit an unfamiliar draft.
    if($metadata.publication_status -ceq 'draft'){Write-Output ('保留历史草稿说明，已核对完整发布快照：'+$entry.component_id)}
}
$archive=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PayloadZip))
try{
    $plans=@($archive.Entries | Where-Object FullName -Match '^[^/]+/plan\.json$')
    Require ($plans.Count -eq 1) 'ZIP 内应只有一个顶层目录下的 plan.json。'
    $reader=[IO.StreamReader]::new($plans[0].Open())
    try{$zipPlan=$reader.ReadToEnd() | ConvertFrom-Json -Depth 100}finally{$reader.Dispose()}
    Require ((Canonical $zipPlan) -ceq (Canonical $plan)) 'ZIP 配方与已发布管理器的配方不同。'
}finally{$archive.Dispose()}
$report=[ordered]@{validated_at=[DateTimeOffset]::Now.ToString('o');catalog_commit=$feed.catalog_commit;patch_count=$ids.Count;manager_tag=$ManagerTag;payload_sha256=$bundle.sha256;previous_feed_sha=$current.sha;published=$false}
Write-Output ('校验通过：'+$ids.Count+' 项补丁；管理器 '+$ManagerTag+'；载荷 '+$bundle.file_name)
if($Publish){
    $latest=Api ('repos/'+$patchRepo+'/contents/manager-feed.json?ref=main')
    Require ($latest.sha -ceq $current.sha) '检查期间正式源发生变化，请重新校验，未覆盖他人更新。'
    $existing=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($latest.content)) | ConvertFrom-Json -Depth 100
    if((Canonical $existing) -ceq (Canonical $feed)){Write-Output '正式源已是该版本，无需重复提交。';$report.published=$true}
    else{
        $request=[ordered]@{message=('Publish '+$ids.Count+' reviewed patches for manager '+$ManagerTag);branch='main';sha=$current.sha;content=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($candidateText))}
        $requestPath=Join-Path $OutputDirectory '发布请求.json'
        $request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $requestPath -Encoding utf8
        $result=& $GitHubCli api ('repos/'+$patchRepo+'/contents/manager-feed.json') --method PUT --input $requestPath
        if($LASTEXITCODE -ne 0){throw '正式源发布失败，请保留校验记录后重试。'}
        $report.published=$true;$report['commit']=($result | ConvertFrom-Json).commit.sha
        Write-Output ('已更新正式源：'+$report.commit)
    }
}else{Write-Output '仅校验，尚未修改 GitHub。确认隔离安装与回滚验证通过后，增加 -Publish 才会切换正式源。'}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory '发布校验结果.json') -Encoding utf8
