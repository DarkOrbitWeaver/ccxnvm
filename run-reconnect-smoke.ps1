$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverExe = Join-Path $repoRoot "server\bin\Debug\net9.0\CipherServer.exe"
$serverDll = Join-Path $repoRoot "server\bin\Debug\net9.0\CipherServer.dll"
$serverUrl = "http://127.0.0.1:$port"
$relayDbPath = Join-Path $repoRoot ".publish\relay-reconnect-test.db"
$probeExe = Join-Path $repoRoot "tools\Cipher.ReconnectProbe\bin\Debug\net9.0-windows\Cipher.ReconnectProbe.exe"
$probeDll = Join-Path $repoRoot "tools\Cipher.ReconnectProbe\bin\Debug\net9.0-windows\Cipher.ReconnectProbe.dll"

$env:MSBUILDDISABLENODEREUSE = "1"

function Start-RelayProcess {
    param([string]$DbPath, [int]$RelayPort)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment["PORT"] = "$RelayPort"
    $startInfo.Environment["RELAY_SQLITE_PATH"] = $DbPath

    if (Test-Path $serverExe) {
        $startInfo.FileName = $serverExe
    } else {
        $startInfo.FileName = "dotnet"
        $null = $startInfo.ArgumentList.Add($serverDll)
    }

    return [System.Diagnostics.Process]::Start($startInfo)
}

function Stop-RelayProcess {
    param($Process)
    if ($Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
    }
}

function Start-ProbeProcess {
    param([string]$Mode, [string]$OutputPrefix)

    if (Test-Path $probeExe) {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new($probeExe, "$Mode $serverUrl")
    } else {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new("dotnet", "`"$probeDll`" $Mode $serverUrl")
    }

    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $proc = [System.Diagnostics.Process]::Start($startInfo)
    return @{
        Label = $OutputPrefix
        Process = $proc
        StdOutTask = $proc.StandardOutput.ReadToEndAsync()
        StdErrTask = $proc.StandardError.ReadToEndAsync()
    }
}

function Wait-ProbeSuccess {
    param($Probe, [int]$TimeoutSeconds, [string]$Label, [string]$SuccessMarker)

    if (-not $Probe.Process.WaitForExit($TimeoutSeconds * 1000)) {
        try { Stop-Process -Id $Probe.Process.Id -Force } catch {}
        throw "$Label timed out"
    }

    $Probe.Process.WaitForExit()
    $Probe.Process.Refresh()

    $stdoutText = $Probe.StdOutTask.GetAwaiter().GetResult()
    $stderrText = $Probe.StdErrTask.GetAwaiter().GetResult()

    if (-not [string]::IsNullOrWhiteSpace($stdoutText)) {
        $stdoutText.TrimEnd() | Out-Host
    }
    if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
        $stderrText.TrimEnd() | Out-Host
    }

    if ($stdoutText -notmatch [regex]::Escape($SuccessMarker)) {
        $exitCode = if ($Probe.Process.HasExited) { $Probe.Process.ExitCode } else { "unknown" }
        throw "$Label failed with exit code $exitCode"
    }
}

dotnet build "$repoRoot\ccxnvm.sln" /nodeReuse:false | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path $relayDbPath) {
    Remove-Item $relayDbPath -Force
}

$relayProc = $null

try {
    $delayedProbe = Start-ProbeProcess -Mode 'wait-connect' -OutputPrefix 'reconnect-delayed-connect'
    Start-Sleep -Seconds 5
    $relayProc = Start-RelayProcess -DbPath $relayDbPath -RelayPort $port
    Wait-ProbeSuccess -Probe $delayedProbe -TimeoutSeconds 60 -Label 'Delayed-start reconnect probe' -SuccessMarker 'CONNECTED'
    Stop-RelayProcess $relayProc
    $relayProc = $null

    Start-Sleep -Seconds 2
    $relayProc = Start-RelayProcess -DbPath $relayDbPath -RelayPort $port
    Start-Sleep -Seconds 3
    $restartProbe = Start-ProbeProcess -Mode 'wait-reconnect' -OutputPrefix 'reconnect-restart'
    Start-Sleep -Seconds 8
    Stop-RelayProcess $relayProc
    $relayProc = $null
    Start-Sleep -Seconds 6
    $relayProc = Start-RelayProcess -DbPath $relayDbPath -RelayPort $port
    Wait-ProbeSuccess -Probe $restartProbe -TimeoutSeconds 90 -Label 'Relay-restart reconnect probe' -SuccessMarker 'RECONNECTED'

    Write-Host "Reconnect smoke scenarios passed."
}
finally {
    Stop-RelayProcess $relayProc
    dotnet build-server shutdown | Out-Null
}
