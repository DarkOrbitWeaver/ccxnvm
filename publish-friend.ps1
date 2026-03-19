param(
    [string]$Runtime = "win-x64"
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
