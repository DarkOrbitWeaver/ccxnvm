$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverExe = Join-Path $repoRoot "server\bin\Debug\net9.0\CipherServer.exe"
$serverDll = Join-Path $repoRoot "server\bin\Debug\net9.0\CipherServer.dll"
$serverUrl = "http://127.0.0.1:$port"
$relayDbPath = Join-Path $repoRoot ".publish\relay-test.db"

if (Test-Path $relayDbPath) {
    Remove-Item $relayDbPath -Force
}


dotnet build "$repoRoot\ccxnvm.sln" | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.WorkingDirectory = $repoRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.Environment["PORT"] = "$port"
$startInfo.Environment["RELAY_SQLITE_PATH"] = $relayDbPath

if (Test-Path $serverExe) {
    $startInfo.FileName = $serverExe
} else {
    $startInfo.FileName = "dotnet"
    $null = $startInfo.ArgumentList.Add($serverDll)
}

$serverProc = [System.Diagnostics.Process]::Start($startInfo)

try {
    $deadline = (Get-Date).AddSeconds(20)
    $healthy = $false
    do {
        Start-Sleep -Milliseconds 500
        try {
            $health = Invoke-WebRequest -UseBasicParsing "$serverUrl/health"
            if ($health.StatusCode -eq 200) {
                $healthy = $true
                break
            }
        } catch {
        }
    } while ((Get-Date) -lt $deadline)

    if (-not $healthy) {
        throw "local relay did not become healthy at $serverUrl"
    }

    dotnet run --project "$repoRoot\tools\Cipher.SmokeRunner" --no-launch-profile -- $serverUrl
    exit $LASTEXITCODE
}
finally {
    if ($serverProc -and -not $serverProc.HasExited) {
        Stop-Process -Id $serverProc.Id -Force
    }
}
