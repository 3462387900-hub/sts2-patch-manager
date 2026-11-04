param([Parameter(Mandatory=$true)][string]$Tag, [string]$GitHubCli='gh')
$ErrorActionPreference='Stop'
if ($Tag -notmatch '^v\d+\.\d+\.\d+$') { throw '请使用 v三段版本 标签。' }
$repository='3462387900-hub/sts2-patch-manager'
$json=& $GitHubCli api ('repos/'+$repository+'/releases/tags/'+$Tag)
if ($LASTEXITCODE -ne 0) { throw '读取发布失败。' }
$release=$json | ConvertFrom-Json
if ($release.draft -or $release.prerelease -or !$release.body -or $release.tag_name -ne $Tag) { throw '只能将已有说明的正式发布写入索引。' }
$expected='SpirePatch-'+$Tag+'-win-x64.zip'
$assets=@($release.assets | Where-Object name -eq $expected)
if ($assets.Count -ne 1 -or $assets[0].state -ne 'uploaded' -or $assets[0].digest -notmatch '^sha256:[a-fA-F0-9]{64}$') { throw 'Windows 更新附件尚未就绪。' }
$index=[ordered]@{ tag_name=$release.tag_name; draft=$false; prerelease=$false; body=$release.body; published_at=$release.published_at; html_url=$release.html_url; assets=@([ordered]@{ name=$assets[0].name; state=$assets[0].state; size=$assets[0].size; digest=$assets[0].digest; browser_download_url=$assets[0].browser_download_url; url=$assets[0].url }) }
$destination=Join-Path (Split-Path -Parent $PSScriptRoot) 'manager-update.json'
$index | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output '版本索引已生成。核对 SHA256 与发布内容后，提交并推送 manager-update.json。'
