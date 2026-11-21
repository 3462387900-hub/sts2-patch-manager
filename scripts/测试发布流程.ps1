#requires -Version 7.0
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path -Parent $PSScriptRoot
$tempRoot=Join-Path ([IO.Path]::GetTempPath()) ('spirepatch-release-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

function global:gh{
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    if($Arguments.Count -ne 2 -or $Arguments[0] -cne 'api'){
        throw '模拟 GitHub CLI 只接受 gh api <release-endpoint>。'
    }
    $global:LASTEXITCODE=0
    return [string]$global:MockGitHubReleaseJson
}
function New-MockReleaseJson([string]$Tag,[bool]$Beta){
    $assetName='SpirePatch-'+$Tag+'-win-x64.zip'
    $digest='sha256:'+('a'*64)
    $release=[ordered]@{
        draft=$false
        prerelease=$Beta
        tag_name=$Tag
        body='发布索引隔离测试：新增、修复与验证说明。'
        published_at='2026-11-21T00:00:00Z'
        html_url='https://github.com/3462387900-hub/sts2-patch-manager/releases/tag/'+$Tag
        assets=@([ordered]@{
            name=$assetName
            state='uploaded'
            size=123456
            digest=$digest
            browser_download_url='https://github.com/3462387900-hub/sts2-patch-manager/releases/download/'+$Tag+'/'+$assetName
            url='https://api.github.com/repos/3462387900-hub/sts2-patch-manager/releases/assets/123456'
        })
    }
    return ($release | ConvertTo-Json -Depth 8 -Compress)
}

try{
    [xml]$project=Get-Content -LiteralPath (Join-Path $repositoryRoot 'SpirePatch.csproj')
    $projectVersion=[string]$project.Project.PropertyGroup.Version
    $projectInfo=[string]$project.Project.PropertyGroup.InformationalVersion
    if($projectVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
       $projectInfo -cne $projectVersion){
        throw 'SpirePatch.csproj 的 Version 与 InformationalVersion 必须是相同的基础三段版本。'
    }
    $infoPattern='^(?<base>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?<suffix>-beta\.[1-9][0-9]{0,8})?$'
    foreach($candidate in @($projectVersion,$projectVersion+'-beta.1')){
        $match=[regex]::Match($candidate,$infoPattern)
        if(!$match.Success -or $match.Groups['base'].Value -cne $projectVersion){
            throw ('有效的程序集 InformationalVersion 被拒绝：'+$candidate)
        }
    }
    foreach($candidate in @($projectVersion+'-rc.1',$projectVersion+'-beta.0',$projectVersion+'-beta.01')){
        if([regex]::IsMatch($candidate,$infoPattern)){
            throw ('无效的程序集 InformationalVersion 被接受：'+$candidate)
        }
    }
    Write-Output 'PASS 稳定版和 Beta 的 InformationalVersion 规则与当前 csproj 基础版本兼容。'

    $sandbox=Join-Path $tempRoot 'sandbox'
    $sandboxScripts=Join-Path $sandbox 'scripts'
    New-Item -ItemType Directory -Path $sandboxScripts -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '更新版本索引.ps1') -Destination $sandboxScripts
    $indexScript=Join-Path $sandboxScripts '更新版本索引.ps1'

    $global:MockGitHubReleaseJson=New-MockReleaseJson -Tag 'v1.2.3' -Beta $false
    & $indexScript -Tag 'v1.2.3' -Channel Stable -GitHubCli 'gh'
    $stablePath=Join-Path $sandbox 'manager-update.json'
    $betaPath=Join-Path $sandbox 'manager-beta-update.json'
    if(!(Test-Path -LiteralPath $stablePath) -or (Test-Path -LiteralPath $betaPath)){
        throw 'Stable 模式没有严格只生成 manager-update.json。'
    }
    $stable=Get-Content -LiteralPath $stablePath -Raw | ConvertFrom-Json
    if($stable.tag_name -cne 'v1.2.3' -or $stable.draft -ne $false -or
       $stable.prerelease -ne $false -or @($stable.assets).Count -ne 1){
        throw 'Stable 索引的标签、通道标志或附件结构错误。'
    }
    $requiredRoot=@('draft','prerelease','tag_name','body','published_at','html_url','assets')
    if(@($requiredRoot | Where-Object { $_ -notin @($stable.PSObject.Properties.Name) }).Count -gt 0){
        throw 'Stable 索引缺少 ManagerUpdates 所需字段。'
    }
    $requiredAsset=@('name','state','size','digest','browser_download_url','url')
    if(@($requiredAsset | Where-Object { $_ -notin @($stable.assets[0].PSObject.Properties.Name) }).Count -gt 0){
        throw 'Stable 索引缺少 UpdateChecks 所需附件字段。'
    }
    $stableHash=(Get-FileHash -LiteralPath $stablePath -Algorithm SHA256).Hash
    Write-Output 'PASS Stable 模式只写 manager-update.json，JSON 字段与客户端解析结构一致。'

    $global:MockGitHubReleaseJson=New-MockReleaseJson -Tag 'v1.2.4-beta.1' -Beta $true
    & $indexScript -Tag 'v1.2.4-beta.1' -Channel Beta -GitHubCli 'gh'
    if(!(Test-Path -LiteralPath $betaPath)){
        throw 'Beta 模式没有生成 manager-beta-update.json。'
    }
    $beta=Get-Content -LiteralPath $betaPath -Raw | ConvertFrom-Json
    $stableHashAfterBeta=(Get-FileHash -LiteralPath $stablePath -Algorithm SHA256).Hash
    if($stableHashAfterBeta -cne $stableHash){
        throw 'Beta 模式改动了稳定版 manager-update.json。'
    }
    if($beta.tag_name -cne 'v1.2.4-beta.1' -or $beta.draft -ne $false -or
       $beta.prerelease -ne $true -or @($beta.assets).Count -ne 1){
        throw 'Beta 索引的标签、通道标志或附件结构错误。'
    }
    if(@($requiredRoot | Where-Object { $_ -notin @($beta.PSObject.Properties.Name) }).Count -gt 0 -or
       @($requiredAsset | Where-Object { $_ -notin @($beta.assets[0].PSObject.Properties.Name) }).Count -gt 0){
        throw 'Beta 索引缺少 ManagerUpdates/UpdateChecks 所需字段。'
    }
    $betaHash=(Get-FileHash -LiteralPath $betaPath -Algorithm SHA256).Hash
    Write-Output 'PASS Beta 模式只写 manager-beta-update.json，稳定版索引逐字节保持不变。'

    $global:MockGitHubReleaseJson=New-MockReleaseJson -Tag 'v1.2.5' -Beta $true
    $rejected=$false
    try{& $indexScript -Tag 'v1.2.5' -Channel Stable -GitHubCli 'gh'}catch{$rejected=$true}
    if(!$rejected){throw 'Stable 模式接受了 prerelease Release。'}
    if((Get-FileHash -LiteralPath $stablePath -Algorithm SHA256).Hash -cne $stableHash){
        throw '拒绝错误 Stable Release 时意外改写了稳定索引。'
    }

    $global:MockGitHubReleaseJson=New-MockReleaseJson -Tag 'v1.2.6-beta.1' -Beta $false
    $rejected=$false
    try{& $indexScript -Tag 'v1.2.6-beta.1' -Channel Beta -GitHubCli 'gh'}catch{$rejected=$true}
    if(!$rejected){throw 'Beta 模式接受了非 prerelease Release。'}
    if((Get-FileHash -LiteralPath $betaPath -Algorithm SHA256).Hash -cne $betaHash){
        throw '拒绝错误 Beta Release 时意外改写了 Beta 索引。'
    }
    Write-Output 'PASS 两个通道均拒绝错误 Release 类型，失败时不会覆盖已有索引。'

    $package=Join-Path $tempRoot 'package'
    foreach($relativePath in @(
        '鸣谢与来源.md',
        'THIRD-PARTY-NOTICES.md',
        'third_party/WorkshopAndroidDownloader-LICENSE.txt',
        'third_party/SharpCompress-LICENSE.txt'
    )){
        $destination=Join-Path $package $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $relativePath) -Destination $destination
    }
    $validator=Join-Path $PSScriptRoot '验证发布声明.ps1'
    & $validator -Directory $package -SourceRoot $repositoryRoot

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath=Join-Path $tempRoot 'release-fixture.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $package,$archivePath,[IO.Compression.CompressionLevel]::Optimal,$true
    )
    & $validator -Directory $package -SourceRoot $repositoryRoot -ArchivePath $archivePath

    $missingLicense=Join-Path $package 'third_party/WorkshopAndroidDownloader-LICENSE.txt'
    $missingBackup=Join-Path $tempRoot 'WorkshopAndroidDownloader-LICENSE.backup'
    Move-Item -LiteralPath $missingLicense -Destination $missingBackup
    $rejected=$false
    try{& $validator -Directory $package -SourceRoot $repositoryRoot}catch{$rejected=$true}
    finally{Move-Item -LiteralPath $missingBackup -Destination $missingLicense}
    if(!$rejected){throw '打包完整性检查没有识别缺失的第三方许可证。'}

    $licensePath=Join-Path $package 'third_party/SharpCompress-LICENSE.txt'
    $licenseBackup=Join-Path $tempRoot 'SharpCompress-LICENSE.backup'
    Copy-Item -LiteralPath $licensePath -Destination $licenseBackup
    Set-Content -LiteralPath $licensePath -Value 'intentionally tampered' -NoNewline
    $rejected=$false
    try{& $validator -Directory $package -SourceRoot $repositoryRoot}catch{$rejected=$true}
    finally{Copy-Item -LiteralPath $licenseBackup -Destination $licensePath -Force}
    if(!$rejected){throw '打包完整性检查没有识别内容被篡改的许可证。'}
    Write-Output 'PASS 声明/许可证校验能识别缺失或内容被篡改的文件。'
    Write-Output 'PASS 发布索引和打包声明本地隔离测试全部完成。'
}
finally{
    Remove-Item Function:\global:gh -ErrorAction SilentlyContinue
    Remove-Variable MockGitHubReleaseJson -Scope Global -ErrorAction SilentlyContinue
    if(Test-Path -LiteralPath $tempRoot){Remove-Item -LiteralPath $tempRoot -Recurse -Force}
}
