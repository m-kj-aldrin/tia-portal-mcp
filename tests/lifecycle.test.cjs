// Exercise the production PowerShell readiness function with HTTP responses only.
// No listener, process launch, lifecycle state access or network request is made.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const path = require('node:path');

test('lifecycle readiness authenticates the exact tracked instance and rejects unrelated HTTP success',
  { skip: process.platform !== 'win32' }, () => {
    const script = String.raw`
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$parseErrors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path (Get-Location) 'tools/tia-mcp-server.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$definition = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-ServerHealth'
}, $true)
if ($null -eq $definition) { throw 'Missing production readiness function.' }
Invoke-Expression $definition.Extent.Text

$executablePath = 'C:\Checkout\bin\TiaPortalDashboard.exe'
$controlHeader = 'X-Tia-Mcp-Control-Token'
$state = [pscustomobject]@{ pid = 1234; port = 5000; controlToken = 'isolated-test-token' }
$script:httpCalls = 0
$script:code = 200
$script:content = ''
$script:fail = $false
function Invoke-WebRequest {
    param([switch]$UseBasicParsing, [string]$Uri, [hashtable]$Headers, [int]$MaximumRedirection, [int]$TimeoutSec)
    $script:httpCalls++
    if ($Uri -cne 'http://127.0.0.1:5000/api/lifecycle/health') { throw 'Wrong endpoint.' }
    if ($Headers[$controlHeader] -cne $state.controlToken) { throw 'Missing instance authentication.' }
    if (-not $PSBoundParameters.ContainsKey('MaximumRedirection') -or $MaximumRedirection -ne 0) {
        throw 'Readiness must not follow redirects.'
    }
    if ($script:fail) { throw 'Simulated rejected token or unavailable endpoint.' }
    [pscustomobject]@{ StatusCode = $script:code; Content = $script:content }
}
function Check-Health([bool]$expected, [string]$label) {
    $actual = Test-ServerHealth $state
    if ($actual -ne $expected) { throw ('Readiness mismatch: ' + $label) }
}
$script:content = @{ status = 'ready'; processId = 1234; executablePath = $executablePath } | ConvertTo-Json
Check-Health $true 'authenticated matching instance'
$script:content = @{ status = 'ready'; processId = 5678; executablePath = $executablePath } | ConvertTo-Json
Check-Health $false 'same executable, wrong process'
$script:content = @{ status = 'ready'; processId = 1234; executablePath = 'C:\OtherCheckout\TiaPortalDashboard.exe' } | ConvertTo-Json
Check-Health $false 'same PID, wrong checkout'
$script:content = @{ status = 'starting'; processId = 1234; executablePath = $executablePath } | ConvertTo-Json
Check-Health $false 'shell not ready'
$script:content = '{"status":"ready"}'
Check-Health $false 'incomplete identity'
$script:content = '{"mode":"connection-prototype","connections":[]}'
Check-Health $false 'old dashboard HTTP 200'
$script:content = '<html>Another service is running</html>'
Check-Health $false 'unrelated HTTP 200'
$script:content = @{ status = 'ready'; processId = 1234; executablePath = $executablePath.ToUpperInvariant() } | ConvertTo-Json
Check-Health $true 'Windows executable path case'
$script:code = 503
Check-Health $false 'non-success status'
$script:code = 200
$script:fail = $true
Check-Health $false 'authentication rejection or unavailable endpoint'
$script:fail = $false
$before = $script:httpCalls
$state.controlToken = ''
Check-Health $false 'missing stored token'
if ($script:httpCalls -ne $before) { throw 'An unauthenticated readiness request was sent.' }
if (Test-ServerHealth $null) { throw 'Missing state passed readiness.' }
Write-Output '12 production readiness cases passed.'
`;
    const result = spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', script], {
      cwd: path.join(__dirname, '..'), encoding: 'utf8', timeout: 15000
    });
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stdout, /12 production readiness cases passed/);
  });
