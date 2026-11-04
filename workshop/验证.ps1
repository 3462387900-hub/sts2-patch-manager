param([switch]$IncludeManagerChecks)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$run = Join-Path $PSScriptRoot ('test-output/check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$cli = Join-Path $PSScriptRoot 'SpirePatch.Workshop.Cli/SpirePatch.Workshop.Cli.csproj'
$tests = Join-Path $PSScriptRoot 'SpirePatch.Workshop.Tests/SpirePatch.Workshop.Tests.csproj'
dotnet restore $cli --locked-mode
if ($LASTEXITCODE) { throw 'CLI 依赖锁定还原失败。' }
dotnet restore $tests --locked-mode
if ($LASTEXITCODE) { throw '测试依赖锁定还原失败。' }
dotnet build $cli -c Release --no-restore
if ($LASTEXITCODE) { throw 'CLI 构建失败。' }
dotnet run --project $tests -c Release --no-restore -- (Join-Path $run 'offline')
if ($LASTEXITCODE) { throw '隔离单元测试失败。' }
if ($IncludeManagerChecks) {
    $app = Join-Path $run 'manager'
    dotnet build (Join-Path $root 'SpirePatch.csproj') -c Release -p:SelfContained=false -p:RestorePackagesPath="$(Join-Path $root '.packages')" -o $app
    if ($LASTEXITCODE) { throw '现有管理器构建失败。' }
    foreach ($mode in @('safety-self-test','self-test','recommendation-self-test','online-self-test','update-self-test','auto-update-self-test','coop-self-test','coop-actions-self-test','catalog-v3-self-test','patch-organizer-self-test','patch-lifecycle-self-test','workspace-integration-self-test')) {
        $proof = Join-Path $run ($mode + '.txt')
        $process = Start-Process -FilePath (Join-Path $app 'SpirePatch.exe') -ArgumentList @('--' + $mode, ('"' + $proof + '"')) -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $proof)) { throw ('现有管理器回归检查失败：' + $mode) }
    }
}
Write-Output ('验证结果：' + $run)
Write-Output '以上是离线测试，不代表真实 Steam 下载成功。未创建发布 ZIP。'
