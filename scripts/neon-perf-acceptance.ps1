param(
    [string]$RepoRoot = (Get-Location).Path,
    [int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$project = Join-Path $RepoRoot 'SpirePatch.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw 'Run from the repository root, or pass -RepoRoot.' }
if ($env:OS -ne 'Windows_NT') { throw 'Windows with WPF and .NET 9 SDK is required.' }
$head = (& git -C $RepoRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Git HEAD.' }
& git -C $RepoRoot merge-base --is-ancestor 1564f9bc1302c308a022c1c23580bed38b6dd1bc HEAD
if ($LASTEXITCODE -ne 0) { throw 'Checkout the P2-02 branch including the candidate and follow-up commits first.' }
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('SpirePatch-P2-02-' + [guid]::NewGuid().ToString('N'))
$appDir = Join-Path $runRoot 'app'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $runRoot 'commit.txt') -Value $head -Encoding UTF8
Write-Host "HEAD: $head"
Write-Host "Reports: $runRoot"
& dotnet build $project -c Release -o $appDir
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$exe = Join-Path $appDir 'SpirePatch.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'SpirePatch.exe missing.' }
$modes = @(
    'neon-perf-self-test',
    'neon-p1-self-test',
    'neon-p2-self-test',
    'workshop-mod-install-self-test',
    'workshop-mod-update-self-test',
    'github-mod-install-self-test',
    'supply-station-self-test',
    'supply-station-manifest-self-test',
    'supply-station-content-self-test'
)
foreach ($mode in $modes) {
    $report = Join-Path $runRoot ($mode + '.txt')
    $arg = @("--$mode", ('"{0}"' -f $report))
    $p = Start-Process -FilePath $exe -WorkingDirectory $runRoot -ArgumentList $arg -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        throw "$mode timed out. Reports: $runRoot"
    }
    $p.Refresh()
    if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
    if ($p.ExitCode -ne 0) {
        $startupError = Join-Path $appDir 'startup-error.txt'
        if (Test-Path -LiteralPath $startupError) { Get-Content -LiteralPath $startupError }
        throw "$mode failed with exit code $($p.ExitCode). Reports: $runRoot"
    }
    if (-not (Test-Path -LiteralPath $report)) { throw "$mode did not produce a report." }
    if (Select-String -LiteralPath $report -Pattern '^(FAIL|ERROR)(\s|:|$)' -Quiet) {
        throw "$mode reported a failure."
    }
    if ($mode -eq 'neon-perf-self-test') {
        foreach ($n in @(50,200,500)) {
            foreach ($phase in @('first_screen','search_filter','scroll','single_disable_refresh','single_enable_refresh')) {
                if (-not (Select-String -LiteralPath $report -Pattern "^METRIC count=$n phase=$phase " -Quiet)) {
                    throw "Missing performance metric: $n $phase"
                }
            }
        }
        if (-not (Select-String -LiteralPath $report -Pattern '^PASS neon-perf-self-test completed$' -Quiet)) {
            throw 'Performance suite did not reach its completion marker.'
        }
    }
    if ($mode -in @('neon-p1-self-test','neon-p2-self-test') -and
        -not (Select-String -LiteralPath $report -Pattern '^PASS ' -Quiet)) {
        throw "$mode has no recorded passing assertion."
    }
    Write-Host "PASS $mode"
}
Write-Host "All selected isolated tests completed. Reports retained at $runRoot"
