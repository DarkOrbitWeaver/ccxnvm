param(
    [Parameter(Mandatory = $true)]
    [string]$ImagePath,
    [int]$Top = 12
)

if (-not (Test-Path $ImagePath)) {
    throw "Could not find image: $ImagePath"
}

Add-Type -AssemblyName System.Drawing

$fullPath = (Resolve-Path $ImagePath).Path
$bitmap = [System.Drawing.Bitmap]::FromFile($fullPath)
$thumb = [System.Drawing.Bitmap]::new($bitmap, 240, 135)
$counts = @{}

for ($x = 0; $x -lt $thumb.Width; $x++) {
    for ($y = 0; $y -lt $thumb.Height; $y++) {
        $pixel = $thumb.GetPixel($x, $y)
        $r = [int]([math]::Floor($pixel.R / 16) * 16)
        $g = [int]([math]::Floor($pixel.G / 16) * 16)
        $b = [int]([math]::Floor($pixel.B / 16) * 16)
        $key = "#{0:X2}{1:X2}{2:X2}" -f $r, $g, $b

        if ($counts.ContainsKey($key)) {
            $counts[$key]++
        } else {
            $counts[$key] = 1
        }
    }
}

Write-Host $fullPath
$counts.GetEnumerator() |
    Sort-Object Value -Descending |
    Select-Object -First $Top |
    ForEach-Object {
        Write-Host ("  {0} {1}" -f $_.Key, $_.Value)
    }

$thumb.Dispose()
$bitmap.Dispose()
