[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet("start", "stop", "restart", "status")]
    [string]$Action = "status",

    [ValidateRange(1, 65535)]
    [Nullable[int]]$Port,

    [ValidateSet("read-only", "full")]
    [string]$AccessProfile,

    [switch]$ConnectionPrototype,

    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 20,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$executablePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe"))
$statePath = Join-Path $repoRoot ".tia-mcp-server-state.json"
$processName = "TiaPortalDashboard"
$controlHeader = "X-Tia-Mcp-Control-Token"

function New-LifecycleResult {
    param(
        [int]$ExitCode,
        [string]$ActionName,
        [string]$Status,
        [string]$Message,
        [Nullable[int]]$ProcessId = $null,
        [Nullable[int]]$HttpPort = $null,
        [string]$Profile = $null,
        [string]$Endpoint = $null
    )

    [pscustomobject]@{
        ExitCode = $ExitCode
        action = $ActionName
        status = $Status
        pid = $ProcessId
        port = $HttpPort
        accessProfile = $Profile
        endpoint = $Endpoint
        message = $Message
    }
}

function Complete-LifecycleAction {
    param([pscustomobject]$Result)

    $output = $Result | Select-Object action, status, pid, port, accessProfile, endpoint, message
    if ($Json) {
        $output | ConvertTo-Json -Compress
    }
    else {
        Write-Host "[$($Result.status)] $($Result.message)"
        if ($Result.endpoint) { Write-Host "Endpoint: $($Result.endpoint)" }
        if ($Result.pid) { Write-Host "PID: $($Result.pid)" }
    }
    exit $Result.ExitCode
}

function Read-LifecycleState {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return $null }
    try {
        Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Lifecycle state is invalid: $statePath. Remove it only after verifying that this checkout's dashboard is not running."
    }
}

function Write-LifecycleState {
    param([pscustomobject]$State)
    $State | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Remove-LifecycleState {
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        Remove-Item -LiteralPath $statePath -Force
    }
}

function Get-ProcessExecutablePath {
    param([System.Diagnostics.Process]$Process)
    try { return [System.IO.Path]::GetFullPath($Process.Path) }
    catch { return $null }
}

function Get-TrackedProcess {
    param([pscustomobject]$State)

    if ($null -eq $State -or $null -eq $State.pid) { return $null }
    $process = Get-Process -Id ([int]$State.pid) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    $actualPath = Get-ProcessExecutablePath $process
    if (-not [string]::Equals($actualPath, $executablePath, [StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    return $process
}

function Get-UnmanagedCheckoutProcesses {
    @(
        Get-Process -Name $processName -ErrorAction SilentlyContinue |
            Where-Object {
                $path = Get-ProcessExecutablePath $_
                [string]::Equals($path, $executablePath, [StringComparison]::OrdinalIgnoreCase)
            }
    )
}

function Test-ServerHealth {
    param([pscustomobject]$State)
    try {
        if ($null -eq $State -or [string]::IsNullOrWhiteSpace([string]$State.controlToken)) { return $false }
        $headers = @{ $controlHeader = [string]$State.controlToken }
        $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$($State.port)/api/lifecycle/health" -Headers $headers -MaximumRedirection 0 -TimeoutSec 2
        if ($response.StatusCode -ne 200) { return $false }
        $identity = $response.Content | ConvertFrom-Json
        return $identity.status -ceq 'ready' -and $identity.processId -eq [int]$State.pid -and
            [string]::Equals([string]$identity.executablePath, $executablePath, [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Get-ServerEndpoint {
    param([int]$HttpPort)
    return "http://127.0.0.1:$HttpPort/"
}

function Invoke-StartServer {
    param([int]$HttpPort, [string]$Profile)

    # One guarded server. Preserve the explicit access profile.
    $endpoint = Get-ServerEndpoint $HttpPort

    $state = Read-LifecycleState
    $tracked = Get-TrackedProcess $state
    if ($null -ne $tracked) {
        $healthy = Test-ServerHealth $state
        $status = if ($healthy) { "running" } else { "unhealthy" }
        $code = if ($healthy) { 0 } else { 1 }
        return New-LifecycleResult $code "start" $status "This checkout's tracked dashboard is already running." $tracked.Id ([int]$state.port) $state.accessProfile (Get-ServerEndpoint ([int]$state.port))
    }

    if ($null -ne $state) { Remove-LifecycleState }
    $unmanaged = @(Get-UnmanagedCheckoutProcesses)
    if ($unmanaged.Count -gt 0) {
        return New-LifecycleResult 1 "start" "unmanaged" "A dashboard from this checkout is running without lifecycle state. Exit it through the tray before using this tool." $unmanaged[0].Id
    }
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        return New-LifecycleResult 1 "start" "missing-build" "Release executable not found. Build the project before starting the server."
    }

    $token = [Guid]::NewGuid().ToString("N")
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executablePath
    $startInfo.WorkingDirectory = Split-Path -Parent $executablePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $startInfo.EnvironmentVariables["TIA_MCP_PORT"] = $HttpPort.ToString([Globalization.CultureInfo]::InvariantCulture)
    $startInfo.EnvironmentVariables["TIA_MCP_ACCESS"] = $Profile
    $startInfo.EnvironmentVariables["TIA_MCP_CONTROL_TOKEN"] = $token

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        return New-LifecycleResult 1 "start" "error" "The dashboard process could not be started."
    }

    $state = [pscustomobject]@{
        version = 1
        pid = $process.Id
        executablePath = $executablePath
        port = $HttpPort
        accessProfile = $Profile
        controlToken = $token
        startedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    Write-LifecycleState $state

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $process.Refresh()
        if ($process.HasExited) {
            Remove-LifecycleState
            return New-LifecycleResult 1 "start" "error" "The dashboard exited before its HTTP endpoint became ready."
        }
        if (Test-ServerHealth $state) {
            $process.Refresh()
            if ($process.HasExited) { continue }
            return New-LifecycleResult 0 "start" "running" "The dashboard is ready and its authenticated identity matches the launched process." $process.Id $HttpPort $Profile $endpoint
        }
        Start-Sleep -Milliseconds 200
    }

    return New-LifecycleResult 1 "start" "unhealthy" "The process started, but its status endpoint did not become healthy before the timeout." $process.Id $HttpPort $Profile $endpoint
}

function Invoke-StopServer {
    param([string]$ActionName = "stop")

    $state = Read-LifecycleState
    $tracked = Get-TrackedProcess $state
    if ($null -eq $tracked) {
        if ($null -ne $state) { Remove-LifecycleState }
        $unmanaged = @(Get-UnmanagedCheckoutProcesses)
        if ($unmanaged.Count -gt 0) {
            return New-LifecycleResult 1 $ActionName "unmanaged" "A dashboard from this checkout is running without a control token. Exit it through the tray; no force-stop was attempted." $unmanaged[0].Id
        }
        return New-LifecycleResult 0 $ActionName "stopped" "This checkout's dashboard is already stopped."
    }

    if (-not [string]::Equals([string]$state.executablePath, $executablePath, [StringComparison]::OrdinalIgnoreCase)) {
        return New-LifecycleResult 1 $ActionName "error" "Lifecycle state does not belong to this checkout; no process was stopped." $tracked.Id
    }

    $stopUri = "http://127.0.0.1:$($state.port)/api/lifecycle/stop"
    try {
        $headers = @{ $controlHeader = [string]$state.controlToken }
        Invoke-WebRequest -UseBasicParsing -Method Post -Uri $stopUri -Headers $headers -MaximumRedirection 0 -TimeoutSec ([Math]::Min($TimeoutSeconds, 10)) | Out-Null
    }
    catch {
        return New-LifecycleResult 1 $ActionName "error" "Graceful shutdown was rejected or unavailable; no force-stop was attempted." $tracked.Id ([int]$state.port) $state.accessProfile (Get-ServerEndpoint ([int]$state.port))
    }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($null -eq (Get-Process -Id $tracked.Id -ErrorAction SilentlyContinue)) {
            Remove-LifecycleState
            return New-LifecycleResult 0 $ActionName "stopped" "The dashboard exited gracefully." $tracked.Id ([int]$state.port) $state.accessProfile
        }
        Start-Sleep -Milliseconds 200
    }

    return New-LifecycleResult 1 $ActionName "timeout" "Graceful shutdown timed out; no force-stop was attempted." $tracked.Id ([int]$state.port) $state.accessProfile
}

function Invoke-StatusServer {
    $state = Read-LifecycleState
    $tracked = Get-TrackedProcess $state
    if ($null -eq $tracked) {
        if ($null -ne $state) { Remove-LifecycleState }
        $unmanaged = @(Get-UnmanagedCheckoutProcesses)
        if ($unmanaged.Count -gt 0) {
            return New-LifecycleResult 1 "status" "unmanaged" "A dashboard from this checkout is running, but it was not started by this tool." $unmanaged[0].Id
        }
        return New-LifecycleResult 3 "status" "stopped" "This checkout's dashboard is stopped."
    }

    $healthy = Test-ServerHealth $state
    $status = if ($healthy) { "running" } else { "unhealthy" }
    $code = if ($healthy) { 0 } else { 1 }
    $message = if ($healthy) { "The authenticated endpoint matches this checkout's tracked process." } else { "The tracked process exists, but its authenticated identity could not be verified. Older builds require a managed reload." }
    return New-LifecycleResult $code "status" $status $message $tracked.Id ([int]$state.port) $state.accessProfile (Get-ServerEndpoint ([int]$state.port))
}

try {
    if ($Action -in @("start", "restart")) {
        if ($PSBoundParameters.ContainsKey("ConnectionPrototype") -and -not $ConnectionPrototype) {
            throw "V1 runtime was retired. Omitting -ConnectionPrototype now starts the rehaul transition; no server was stopped."
        }
    }
    $result = switch ($Action) {
        "status" { Invoke-StatusServer }
        "start" {
            $startPort = if ($PSBoundParameters.ContainsKey("Port")) { [int]$Port } else { 5000 }
            $startProfile = if ($PSBoundParameters.ContainsKey("AccessProfile")) { $AccessProfile } else { "full" }
            Invoke-StartServer $startPort $startProfile
        }
        "stop" { Invoke-StopServer }
        "restart" {
            $previousState = Read-LifecycleState
            $restartPort = if ($PSBoundParameters.ContainsKey("Port")) { [int]$Port } elseif ($null -ne $previousState) { [int]$previousState.port } else { 5000 }
            $restartProfile = if ($PSBoundParameters.ContainsKey("AccessProfile")) { $AccessProfile } elseif ($null -ne $previousState) { [string]$previousState.accessProfile } else { "full" }
            $stopResult = Invoke-StopServer "restart"
            if ($stopResult.ExitCode -ne 0) { $stopResult }
            else { Invoke-StartServer $restartPort $restartProfile }
        }
    }
}
catch {
    $result = New-LifecycleResult 1 $Action "error" $_.Exception.Message
}

Complete-LifecycleAction $result
