$IncludeUi = $false
if ($args -contains '-IncludeUi') {
    $IncludeUi = $true
}

$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspace = Join-Path $env:TEMP ("ccxnvm-gate-" + [guid]::NewGuid().ToString("N"))

$env:MSBUILDDISABLENODEREUSE = "1"

function Invoke-Step {
    param(
        [string]$Label,
        [scriptblock]$Action
    )

    Write-Host "==> $Label"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
}

New-Item -ItemType Directory -Path $workspace | Out-Null
robocopy $sourceRoot $workspace /E `
    /XD .git bin obj .publish .releases `
        tests\Cipher.UI.Tests\bin tests\Cipher.UI.Tests\obj tests\Cipher.UI.Tests\.artifacts `
        tests\Cipher.Tests\bin tests\Cipher.Tests\obj `
        server\bin server\obj `
        tools\Cipher.SmokeRunner\bin tools\Cipher.SmokeRunner\obj `
        tools\Cipher.ReconnectProbe\bin tools\Cipher.ReconnectProbe\obj | Out-Null

if ($LASTEXITCODE -ge 8) {
    throw "failed to create isolated quality-gate workspace"
}

try {
    Push-Location $workspace

    Invoke-Step "Build solution" { dotnet build .\ccxnvm.sln /nodeReuse:false }
    Invoke-Step "Run unit tests" { dotnet test .\tests\Cipher.Tests\Cipher.Tests.csproj --no-build }
    Invoke-Step "Run local integration smoke" { powershell -ExecutionPolicy Bypass -File .\run-local-integration.ps1 }
    Invoke-Step "Run reconnect smoke" { powershell -ExecutionPolicy Bypass -File .\run-reconnect-smoke.ps1 }

    if ($IncludeUi) {
        Invoke-Step "Run UI regression tests" {
            dotnet test .\tests\Cipher.UI.Tests\Cipher.UI.Tests.csproj --no-build --logger "console;verbosity=minimal"
        }
    }

    Write-Host ""
    Write-Host "Quality gate passed."
} finally {
    Pop-Location
    dotnet build-server shutdown | Out-Null
    Write-Host "Quality-gate workspace: $workspace"
}
