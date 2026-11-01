param(
    [string]$IconPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\DeepSeekTranslator\Assets\app.ico'),
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging\msix\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $IconPath)) { throw "Icon not found: $IconPath" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$source = [System.Drawing.Image]::FromFile($IconPath)

$assets = @(
    @{ Name = 'StoreLogo.png'; Width = 50; Height = 50 },
    @{ Name = 'Square44x44Logo.png'; Width = 44; Height = 44 },
    @{ Name = 'Square71x71Logo.png'; Width = 71; Height = 71 },
    @{ Name = 'Square150x150Logo.png'; Width = 150; Height = 150 },
    @{ Name = 'Square310x310Logo.png'; Width = 310; Height = 310 },
    @{ Name = 'Wide310x150Logo.png'; Width = 310; Height = 150 }
)

foreach ($asset in $assets) {
    $bitmap = New-Object System.Drawing.Bitmap(
        $asset.Width, $asset.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        $scale = [Math]::Min($asset.Width / $source.Width, $asset.Height / $source.Height)
        $width = [int][Math]::Round($source.Width * $scale)
        $height = [int][Math]::Round($source.Height * $scale)
        $x = [int](($asset.Width - $width) / 2)
        $y = [int](($asset.Height - $height) / 2)

        $graphics.DrawImage($source, $x, $y, $width, $height)
        $bitmap.Save((Join-Path $OutDir $asset.Name), [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "  $($asset.Name)  $($asset.Width)x$($asset.Height)"
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$source.Dispose()
Write-Host "MSIX assets -> $OutDir"
