param(
    [string]$Runtime = "win-x64",
    [switch]$SkipZip
)

$output = Join-Path ".publish" $Runtime

dotnet publish .\CipherClient.csproj `
  -c Release `
  -r $Runtime `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $output

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$productName = "Cipher"
if (Test-Path ".\Branding.props") {
    try {
        $branding = [xml](Get-Content ".\Branding.props")
        if ($branding.Project.PropertyGroup.Product) {
            $productName = [string]$branding.Project.PropertyGroup.Product
        }
    } catch {
    }
}

$exe = Get-ChildItem $output -Filter *.exe | Select-Object -First 1
if (-not $exe) {
    throw "publish finished but no .exe was found in $output"
}

$readmePath = Join-Path $output "README.txt"
$readme = @(
    "$($exe.Name)",
    "",
    "Run this app from any normal folder you like, for example:",
    "- Desktop\$productName",
    "- Documents\$productName",
    "- Downloads\$productName",
    "",
    "Encrypted local data is stored here:",
    "%APPDATA%\$productName\",
    "",
    "Important files in that folder:",
    "- vault.db",
    "- vault.salt",
    "- session.bin",
    "",
    "Tips:",
    "- Back up %APPDATA%\$productName\ if you want to keep the account and chat history.",
    "- Deleting the exe does not delete the vault.",
    "- The built-in NUKE action destroys the local vault data."
) -join [Environment]::NewLine

Set-Content -Path $readmePath -Value $readme -Encoding UTF8

if (-not $SkipZip) {
    $zipPath = Join-Path ".publish" "$($exe.BaseName)-$Runtime.zip"
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Created archive: $zipPath"
}
