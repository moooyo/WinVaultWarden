[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RunDirectory,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]*$')]
    [string]$OwnerPrefix = 'native-harness',
    [ValidateRange(1, 6)]
    [int]$Columns = 3,
    [ValidateRange(1, 6)]
    [int]$Rows = 3,
    [ValidateRange(320, 960)]
    [int]$CellWidth = 480,
    [ValidateRange(280, 800)]
    [int]$CellHeight = 400,
    [bool]$IncludePopupLayers = $true
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$resolvedRunDirectory = (Resolve-Path -LiteralPath $RunDirectory).Path
$manifestPath = Join-Path $resolvedRunDirectory 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    $manifestPath = Join-Path $resolvedRunDirectory 'manifest.in-progress.json'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$outputDirectoryName = "contact-sheets-$OwnerPrefix"
$outputDirectory = Join-Path $resolvedRunDirectory $outputDirectoryName
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

# These are labeled review thumbnails of original native PNGs, not replacement screenshots.
# No application UI, image content, or layout is recreated by this script.
function Resolve-NativeImagePath {
    param([string]$RelativePath)

    $resolved = [IO.Path]::GetFullPath((Join-Path $resolvedRunDirectory $RelativePath))
    $requiredPrefix = $resolvedRunDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A manifest image path escaped its evidence directory.'
    }
    return $resolved
}

$cards = foreach ($capture in $manifest.Captures) {
    [pscustomobject]@{
        Theme = $capture.Theme
        Viewport = $capture.Viewport
        Name = $capture.Name
        Kind = 'Native XAML client'
        Path = $capture.Image.Path
    }
    if (-not $IncludePopupLayers) { continue }
    foreach ($layer in $capture.PopupLayers) {
        if ($layer.HasVariation -eq $false) { continue }
        [pscustomobject]@{
            Theme = $capture.Theme
            Viewport = $capture.Viewport
            Name = $capture.Name
            Kind = 'Native popup XAML'
            Path = $layer.Path
        }
    }
    foreach ($layer in $capture.OwnWindowLayers) {
        if (-not $layer.HasVariation -or $layer.WindowClass -notlike '*PopupWindowSiteBridge*') { continue }
        [pscustomobject]@{
            Theme = $capture.Theme
            Viewport = $capture.Viewport
            Name = $capture.Name
            Kind = 'Own-process native popup'
            Path = $layer.Path
        }
    }
}

$headerHeight = 64
$pageCapacity = $Columns * $Rows
$font = [Drawing.Font]::new('Segoe UI', 13, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$headingFont = [Drawing.Font]::new('Segoe UI', 18, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
$subheadingFont = [Drawing.Font]::new('Segoe UI', 12, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$textBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(28, 28, 28))
$secondaryBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(85, 85, 85))
$linePen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(208, 208, 208), 1)
$sheetEntries = [Collections.Generic.List[object]]::new()

try {
    foreach ($group in @($cards | Group-Object -Property Theme, Viewport)) {
        $theme = $group.Group[0].Theme
        $viewport = $group.Group[0].Viewport
        $pageCount = [int][Math]::Ceiling($group.Count / [double]$pageCapacity)
        for ($page = 0; $page -lt $pageCount; $page++) {
            $pageCards = @($group.Group | Select-Object -Skip ($page * $pageCapacity) -First $pageCapacity)
            $usedRows = [int][Math]::Ceiling($pageCards.Count / [double]$Columns)
            $bitmap = [Drawing.Bitmap]::new($Columns * $CellWidth, $headerHeight + $usedRows * $CellHeight)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::FromArgb(245, 245, 245))
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::ClearTypeGridFit
                $title = "Native UI review | $($manifest.Round) | $theme | $viewport | $($page + 1)/$pageCount"
                $graphics.DrawString($title, $headingFont, $textBrush, [single]12, [single]8)
                $graphics.DrawString('Thumbnails of original native PNGs. Open the source PNG for pixel-level review.', $subheadingFont, $secondaryBrush, [single]12, [single]34)

                for ($index = 0; $index -lt $pageCards.Count; $index++) {
                    $card = $pageCards[$index]
                    $x = ($index % $Columns) * $CellWidth
                    $y = $headerHeight + [int][Math]::Floor($index / [double]$Columns) * $CellHeight
                    $graphics.DrawRectangle($linePen, [Drawing.Rectangle]::new($x + 4, $y + 4, $CellWidth - 8, $CellHeight - 8))
                    $graphics.DrawString($card.Name, $font, $textBrush, [single]($x + 12), [single]($y + 10))
                    $graphics.DrawString($card.Kind, $subheadingFont, $secondaryBrush, [single]($x + 12), [single]($y + 29))
                    $sourceImage = [Drawing.Image]::FromFile((Resolve-NativeImagePath -RelativePath $card.Path))
                    try {
                        $availableWidth = $CellWidth - 24
                        $availableHeight = $CellHeight - 64
                        $scale = [Math]::Min($availableWidth / [double]$sourceImage.Width, $availableHeight / [double]$sourceImage.Height)
                        $drawWidth = [int][Math]::Round($sourceImage.Width * $scale)
                        $drawHeight = [int][Math]::Round($sourceImage.Height * $scale)
                        $drawX = $x + [int][Math]::Round(($CellWidth - $drawWidth) / 2.0)
                        $drawY = $y + 50 + [int][Math]::Round(($availableHeight - $drawHeight) / 2.0)
                        $graphics.DrawImage($sourceImage, [Drawing.Rectangle]::new($drawX, $drawY, $drawWidth, $drawHeight))
                    }
                    finally { $sourceImage.Dispose() }
                }
                $name = '{0}-{1}-{2:00}.png' -f $theme, $viewport, ($page + 1)
                $outputPath = Join-Path $outputDirectory $name
                $bitmap.Save($outputPath, [Drawing.Imaging.ImageFormat]::Png)
                $sheetEntries.Add([pscustomobject]@{
                    sheet = "$outputDirectoryName/$name"
                    theme = $theme
                    viewport = $viewport
                    originalNativeImages = @($pageCards | ForEach-Object { $_.Path })
                })
            }
            finally {
                $graphics.Dispose()
                $bitmap.Dispose()
            }
        }
    }
}
finally {
    $font.Dispose()
    $headingFont.Dispose()
    $subheadingFont.Dispose()
    $textBrush.Dispose()
    $secondaryBrush.Dispose()
    $linePen.Dispose()
}

$indexPath = Join-Path $outputDirectory 'index.json'
ConvertTo-Json -InputObject ([pscustomobject]@{
    description = 'Contact sheets are labeled thumbnails. Original native PNGs and their hashes remain authoritative.'
    ownerPrefix = $OwnerPrefix
    columns = $Columns
    rows = $Rows
    manifest = [IO.Path]::GetRelativePath($resolvedRunDirectory, $manifestPath).Replace('\', '/')
    sheets = $sheetEntries.ToArray()
}) -Depth 6 | Set-Content -LiteralPath $indexPath -Encoding utf8
Write-Host "Created $($sheetEntries.Count) contact sheets. Index: $indexPath"
