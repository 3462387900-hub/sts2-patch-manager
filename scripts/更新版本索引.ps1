param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [ValidateSet('Stable','Beta')][string]$Channel='Stable',
    [string]$GitHubCli='gh'
)
$ErrorActionPreference='Stop'

$stablePattern='^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$'
$betaPattern='^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-beta\.[1-9][0-9]{0,8}$'
if($Channel -ceq 'Stable'){
    if($Tag -cnotmatch $stablePattern){throw '稳定版必须使用 v三段版本 标签，例如 v0.14.4。'}
    $expectedPrerelease=$false
    $destinationName='manager-update.json'
}
else{
    if($Tag -cnotmatch $betaPattern){throw 'Beta 版必须使用 v三段版本-beta.N 标签，例如 v0.14.4-beta.1。'}
    $expectedPrerelease=$true
    $destinationName='manager-beta-update.json'
}

$repository='3462387900-hub/sts2-patch-manager'
$json=& $GitHubCli api ('repos/'+$repository+'/releases/tags/'+$Tag)
if($LASTEXITCODE -ne 0){throw '读取发布失败。'}
$release=$json | ConvertFrom-Json
if($release.draft -or $release.prerelease -ne $expectedPrerelease -or
   [string]::IsNullOrWhiteSpace([string]$release.body) -or $release.tag_name -cne $Tag){
    throw 'Release 的标签、草稿状态、预发布状态或更新说明与所选通道不匹配。'
}
if([string]::IsNullOrWhiteSpace([string]$release.published_at) -or
   [string]::IsNullOrWhiteSpace([string]$release.html_url)){
    throw 'Release 缺少发布时间或发布页地址。'
}

$expectedAsset='SpirePatch-'+$Tag+'-win-x64.zip'
$assets=@($release.assets | Where-Object { $_.name -ceq $expectedAsset })
if($assets.Count -ne 1 -or $assets[0].state -cne 'uploaded' -or
   $assets[0].size -le 0 -or $assets[0].digest -cnotmatch '^sha256:[a-fA-F0-9]{64}$' -or
   [string]::IsNullOrWhiteSpace([string]$assets[0].browser_download_url) -or
   [string]::IsNullOrWhiteSpace([string]$assets[0].url)){
    throw '当前通道的 Windows 更新附件尚未完整上传，或缺少有效 SHA256。'
}

# Keep this schema aligned with ManagerUpdates.ParseReleaseElement and UpdateChecks.Json.
$index=[ordered]@{
    draft=$false
    prerelease=$expectedPrerelease
    tag_name=$release.tag_name
    body=$release.body
    published_at=$release.published_at
    html_url=$release.html_url
    assets=@([ordered]@{
        name=$assets[0].name
        state=$assets[0].state
        size=[long]$assets[0].size
        digest=$assets[0].digest
        browser_download_url=$assets[0].browser_download_url
        url=$assets[0].url
    })
}
$repositoryRoot=Split-Path -Parent $PSScriptRoot
$destination=Join-Path $repositoryRoot $destinationName
$index | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output ("已生成 "+$destinationName+"；本次只更新 "+$Channel+" 通道索引。")
