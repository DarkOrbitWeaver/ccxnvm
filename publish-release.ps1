param(
    [string]$Runtime = "win-x64",
    [string]$Channel = "stable",
    [string]$Version,
    [switch]$IncludePortable,
    [switch]$CleanReleaseHistory
)

$projectPath = ".\CipherClient.csproj"
$publishDir = Join-Path ".publish" "$Runtime-release"
$releaseDir = Join-Path ".releases" $Channel
$toolsDir = ".\.tools"

if (-not (Test-Path $projectPath)) {
    throw "Could not find $projectPath"
}

[xml]$project = Get-Content $projectPath
$packageVersion = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($packageVersion)) {
    throw "CipherClient.csproj is missing a <Version> value."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $packageVersion
}

$velopackVersion = ""
foreach ($ref in $project.Project.ItemGroup.PackageReference) {
    if ($ref.Include -eq "Velopack") {
        $velopackVersion = [string]$ref.Version
        break
    }
}

if ([string]::IsNullOrWhiteSpace($velopackVersion)) {
    throw "CipherClient.csproj is missing the Velopack package reference."
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

Get-ChildItem $publishDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

if ($CleanReleaseHistory) {
    Get-ChildItem $releaseDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
}

dotnet publish $projectPath `
  -c Release `
  -r $Runtime `
  --self-contained true `
  -p:Version=$Version `
  -p:PublishSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=false `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $publishDir

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$createdumpPath = Join-Path $publishDir "createdump.exe"
if (Test-Path $createdumpPath) {
    # Velopack 0.0.1298 fails while stubbing createdump.exe during packaging.
    Remove-Item $createdumpPath -Force
}

$vpkPath = Join-Path $toolsDir "vpk.exe"
if (Test-Path $vpkPath) {
    dotnet tool update --tool-path $toolsDir vpk --version $velopackVersion | Out-Host
} else {
    dotnet tool install --tool-path $toolsDir vpk --version $velopackVersion | Out-Host
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exe = Get-ChildItem $publishDir -Filter *.exe |
    Where-Object { $_.Name -ne "Update.exe" } |
    Select-Object -First 1

if (-not $exe) {
    throw "publish finished but no application .exe was found in $publishDir"
}

$packId = "DarkOrbitWeaver.Cipher"
$packArgs = @(
    "pack",
    "--packId", $packId,
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", $exe.Name,
    "--outputDir", $releaseDir,
    "--channel", $Channel,
    "--runtime", $Runtime,
    "--delta", "BestSize"
)

if (-not $IncludePortable) {
    $packArgs += "--noPortable"
}

if (Test-Path ".\Assets\256x256.ico") {
    $packArgs += @("--icon", ".\Assets\256x256.ico")
}

& $vpkPath @packArgs

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Release artifacts created in $releaseDir"
if ($IncludePortable) {
    Write-Host "Portable zip included because -IncludePortable was set."
} else {
    Write-Host "Portable zip skipped. Use -IncludePortable if you want a separate portable download."
}
if ($CleanReleaseHistory) {
    Write-Host "Release history was cleaned first, so this run may only produce a full package."
} else {
    Write-Host "Previous packages were kept so Velopack can generate smaller delta patches when possible."
}
Write-Host "Upload the setup exe, generated .nupkg files, and metadata files from $releaseDir to a GitHub Release."
