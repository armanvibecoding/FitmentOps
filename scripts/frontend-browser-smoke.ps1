[CmdletBinding()]
param(
    [string]$FrontendBaseUrl = $(if ($env:FRONTEND_BASE_URL) { $env:FRONTEND_BASE_URL } else { 'http://127.0.0.1:4173' })
)

$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'frontend-browser-smoke.sh'
$source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8
$match = [regex]::Match(
    $source,
    "read -r -d '' smoke_code <<'JS' \|\| true\r?\n(?<code>[\s\S]*?)\r?\nJS")
if (-not $match.Success) {
    throw 'Could not extract the shared Playwright scenario from frontend-browser-smoke.sh.'
}

$scenario = $match.Groups['code'].Value.Replace("`r", '').Replace("`n", ' ')
$env:SMOKE_CODE = $scenario
& node -e 'new Function(`return (${process.env.SMOKE_CODE})`);'
if ($LASTEXITCODE -ne 0) {
    throw 'The shared Playwright scenario is not valid JavaScript.'
}

$session = "parca-muhendisi-local-$PID"
$cliScript = Resolve-Path (Join-Path $PSScriptRoot '..\AutoPartsStore\Frontend\client\node_modules\@playwright\cli\playwright-cli.js')
$cli = @($cliScript.Path, '--session', $session)
try {
    & node @cli open $FrontendBaseUrl
    if ($LASTEXITCODE -ne 0) { throw 'Playwright could not open the frontend.' }
    $result = & node @cli run-code $scenario
    if ($LASTEXITCODE -ne 0) { throw 'Playwright scenario failed.' }
    $result | Write-Output
    if (($result -join "`n") -notmatch '"checkout":"passed"') {
        throw 'Playwright scenario did not return the required checkout result.'
    }
}
finally {
    & node @cli close *> $null
    Remove-Item Env:SMOKE_CODE -ErrorAction SilentlyContinue
}
