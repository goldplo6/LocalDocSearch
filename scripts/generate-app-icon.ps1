#requires -Version 7.0
<#
.SYNOPSIS
Regenerate the original LocalDocSearch icon assets on Windows.
.DESCRIPTION
Uses one scene for SVG and System.Drawing raster output. No downloads, packages,
project build, or application startup. Only the six named Assets files are written.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This generator requires Windows System.Drawing.' }
Add-Type -AssemblyName System.Drawing.Common

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetsPath = Join-Path $projectRoot 'src/LocalDocSearch.Desktop/Assets'
$assetNames = @('app-icon.svg', 'app-icon.ico', 'app-icon.png', 'app-icon-512.png', 'app-icon-preview.png', 'app-icon-manifest.json')
$null = New-Item -ItemType Directory -Path $assetsPath -Force
$blue = '#315CE7'
$white = '#FFFFFF'
$fold = '#C8D5FF'
$frameSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# All coordinates are in the same 256 x 256 design space. This ordered scene is
# the only drawing source: both exporters consume every shape below.
$scene = @(
    @{ Kind = 'RoundRect'; X = 8; Y = 8; Width = 240; Height = 240; Radius = 52; Fill = $blue },
    @{ Kind = 'Path'; Fill = $white; Commands = @(
        @('M', 73, 45), @('L', 136, 45), @('L', 177, 86), @('L', 177, 183),
        @('C', 177, 190, 172, 195, 165, 195), @('L', 73, 195),
        @('C', 66, 195, 61, 190, 61, 183), @('L', 61, 57),
        @('C', 61, 50, 66, 45, 73, 45), @('Z')
    ) },
    @{ Kind = 'Path'; Fill = $fold; Commands = @(
        @('M', 136, 45), @('L', 136, 76),
        @('C', 136, 82, 140, 86, 146, 86), @('L', 177, 86), @('Z')
    ) },
    @{ Kind = 'Line'; X1 = 84; Y1 = 110; X2 = 133; Y2 = 110; Stroke = $blue; StrokeWidth = 12 },
    @{ Kind = 'Line'; X1 = 84; Y1 = 137; X2 = 108; Y2 = 137; Stroke = $blue; StrokeWidth = 12 },
    # Blue separators keep the white lens and handle distinct from the document.
    @{ Kind = 'Line'; X1 = 188; Y1 = 188; X2 = 211; Y2 = 211; Stroke = $blue; StrokeWidth = 28 },
    @{ Kind = 'Circle'; CX = 166; CY = 166; Radius = 42; Fill = $blue },
    @{ Kind = 'Line'; X1 = 188; Y1 = 188; X2 = 211; Y2 = 211; Stroke = $white; StrokeWidth = 13 },
    @{ Kind = 'Circle'; CX = 166; CY = 166; Radius = 30; Fill = $blue; Stroke = $white; StrokeWidth = 12 }
)

function Format-Number([double] $Value) {
    return $Value.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
}

function New-SceneSvg {
    $elements = [Collections.Generic.List[string]]::new()
    foreach ($shape in $scene) {
        $fillValue = if ($shape.ContainsKey('Fill')) { $shape.Fill } else { 'none' }
        $style = 'fill="{0}"' -f $fillValue
        if ($shape.ContainsKey('Stroke')) {
            $style += ' stroke="{0}" stroke-width="{1}" stroke-linecap="round" stroke-linejoin="round"' -f $shape.Stroke, (Format-Number $shape.StrokeWidth)
        }
        switch ($shape.Kind) {
            'RoundRect' {
                $elements.Add(('  <rect x="{0}" y="{1}" width="{2}" height="{3}" rx="{4}" {5}/>' -f $shape.X, $shape.Y, $shape.Width, $shape.Height, $shape.Radius, $style))
            }
            'Path' {
                $commands = foreach ($command in $shape.Commands) {
                    $numbers = for ($i = 1; $i -lt $command.Count; $i++) { Format-Number $command[$i] }
                    $command[0] + ($numbers -join ' ')
                }
                $elements.Add(('  <path d="{0}" {1}/>' -f ($commands -join ' '), $style))
            }
            'Line' {
                $elements.Add(('  <path d="M{0} {1} L{2} {3}" {4}/>' -f $shape.X1, $shape.Y1, $shape.X2, $shape.Y2, $style))
            }
            'Circle' {
                $elements.Add(('  <circle cx="{0}" cy="{1}" r="{2}" {3}/>' -f $shape.CX, $shape.CY, $shape.Radius, $style))
            }
            default { throw "Unknown SVG shape: $($shape.Kind)" }
        }
    }
    return '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">' + "`n" + ($elements -join "`n") + "`n</svg>`n"
}

function New-ShapePath($Shape) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    switch ($Shape.Kind) {
        'RoundRect' {
            [single]$diameter = 2 * $Shape.Radius
            [single]$right = $Shape.X + $Shape.Width - $diameter
            [single]$bottom = $Shape.Y + $Shape.Height - $diameter
            $path.AddArc([single]$Shape.X, [single]$Shape.Y, $diameter, $diameter, 180, 90)
            $path.AddArc($right, [single]$Shape.Y, $diameter, $diameter, 270, 90)
            $path.AddArc($right, $bottom, $diameter, $diameter, 0, 90)
            $path.AddArc([single]$Shape.X, $bottom, $diameter, $diameter, 90, 90)
            $path.CloseFigure()
        }
        'Path' {
            [single]$lastX = 0
            [single]$lastY = 0
            foreach ($command in $Shape.Commands) {
                switch ($command[0]) {
                    'M' {
                        $path.StartFigure()
                        $lastX = $command[1]
                        $lastY = $command[2]
                    }
                    'L' {
                        $path.AddLine($lastX, $lastY, [single]$command[1], [single]$command[2])
                        $lastX = $command[1]
                        $lastY = $command[2]
                    }
                    'C' {
                        $path.AddBezier($lastX, $lastY, [single]$command[1], [single]$command[2], [single]$command[3], [single]$command[4], [single]$command[5], [single]$command[6])
                        $lastX = $command[5]
                        $lastY = $command[6]
                    }
                    'Z' { $path.CloseFigure() }
                    default { throw "Unknown path command: $($command[0])" }
                }
            }
        }
        'Line' { $path.AddLine([single]$Shape.X1, [single]$Shape.Y1, [single]$Shape.X2, [single]$Shape.Y2) }
        'Circle' {
            $path.AddEllipse([single]($Shape.CX - $Shape.Radius), [single]($Shape.CY - $Shape.Radius), [single](2 * $Shape.Radius), [single](2 * $Shape.Radius))
        }
        default { throw "Unknown raster shape: $($Shape.Kind)" }
    }
    return ,$path
}

function New-IconPng([int] $Size) {
    # At least 4x supersampling; 64x for 16 px and 16x for 64 px.
    [int]$renderSize = [Math]::Max(1024, $Size * 4)
    $large = [Drawing.Bitmap]::new($renderSize, $renderSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $small = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($large)
    $resizer = [Drawing.Graphics]::FromImage($small)
    $stream = [IO.MemoryStream]::new()
    $attributes = [Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.ScaleTransform([single]($renderSize / 256.0), [single]($renderSize / 256.0))
        foreach ($shape in $scene) {
            $path = New-ShapePath $shape
            try {
                if ($shape.ContainsKey('Fill')) {
                    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($shape.Fill))
                    try { $graphics.FillPath($brush, $path) } finally { $brush.Dispose() }
                }
                if ($shape.ContainsKey('Stroke')) {
                    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($shape.Stroke), [single]$shape.StrokeWidth)
                    try {
                        $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
                        $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
                        $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                        $graphics.DrawPath($pen, $path)
                    } finally { $pen.Dispose() }
                }
            } finally { $path.Dispose() }
        }
        $resizer.Clear([Drawing.Color]::Transparent)
        $resizer.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $resizer.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $resizer.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $resizer.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $rectangle = [Drawing.Rectangle]::new(0, 0, $Size, $Size)
        $resizer.DrawImage($large, $rectangle, 0, 0, $renderSize, $renderSize, [Drawing.GraphicsUnit]::Pixel, $attributes)
        $small.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $attributes.Dispose()
        $stream.Dispose()
        $resizer.Dispose()
        $graphics.Dispose()
        $small.Dispose()
        $large.Dispose()
    }
}

# Standard PNG CRC32, independently checked on every chunk in each saved frame.
$crcTable = [uint32[]]::new(256)
for ($n = 0; $n -lt 256; $n++) {
    [uint32]$crcValue = $n
    for ($bit = 0; $bit -lt 8; $bit++) {
        if ($crcValue -band 1) { $crcValue = ($crcValue -shr 1) -bxor [uint32]3988292384 }
        else { $crcValue = $crcValue -shr 1 }
    }
    $crcTable[$n] = $crcValue
}

function Get-Crc32([byte[]] $Data, [int] $Offset, [int] $Count) {
    [uint32]$crcValue = [uint32]::MaxValue
    for ($i = $Offset; $i -lt $Offset + $Count; $i++) {
        $index = [int](($crcValue -bxor [uint32]$Data[$i]) -band 255)
        $crcValue = ($crcValue -shr 8) -bxor $crcTable[$index]
    }
    return [uint32]($crcValue -bxor [uint32]::MaxValue)
}

function Get-UInt32BE([byte[]] $Data, [int] $Offset) {
    return [uint32]([long]$Data[$Offset] * 16777216 + [long]$Data[$Offset + 1] * 65536 + [long]$Data[$Offset + 2] * 256 + [long]$Data[$Offset + 3])
}

function Get-Sha256([byte[]] $Data) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Data)).ToLowerInvariant()
}

function Assert-Png([byte[]] $Data, [int] $ExpectedWidth, [int] $ExpectedHeight, [bool] $RequireTransparency = $true) {
    $signature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
    if ($Data.Length -lt 45) { throw 'PNG is too short.' }
    for ($i = 0; $i -lt 8; $i++) {
        if ($Data[$i] -ne $signature[$i]) { throw 'Invalid PNG signature.' }
    }
    $position = 8
    $sawHeader = $false
    $sawData = $false
    $sawEnd = $false
    while ($position -lt $Data.Length) {
        if ($position + 12 -gt $Data.Length) { throw 'Truncated PNG chunk.' }
        $length = Get-UInt32BE $Data $position
        if ([long]$position + 12 + $length -gt $Data.Length) { throw 'PNG chunk exceeds payload.' }
        $type = [Text.Encoding]::ASCII.GetString($Data, $position + 4, 4)
        $storedCrc = Get-UInt32BE $Data ($position + 8 + $length)
        $computedCrc = Get-Crc32 $Data ($position + 4) ($length + 4)
        if ($storedCrc -ne $computedCrc) { throw "PNG $type CRC mismatch." }
        if (-not $sawHeader -and $type -ne 'IHDR') { throw 'PNG IHDR must be first.' }
        switch ($type) {
            'IHDR' {
                if ($sawHeader -or $length -ne 13) { throw 'Invalid PNG IHDR.' }
                if ((Get-UInt32BE $Data ($position + 8)) -ne $ExpectedWidth -or (Get-UInt32BE $Data ($position + 12)) -ne $ExpectedHeight) { throw 'PNG dimensions mismatch.' }
                if ($Data[$position + 16] -ne 8 -or $Data[$position + 17] -ne 6) { throw 'PNG must be 8-bit RGBA.' }
                $sawHeader = $true
            }
            'IDAT' { $sawData = $true }
            'IEND' {
                if ($length -ne 0) { throw 'PNG IEND must be empty.' }
                $sawEnd = $true
            }
        }
        $position += 12 + $length
        if ($sawEnd) { break }
    }
    if (-not $sawHeader -or -not $sawData -or -not $sawEnd -or $position -ne $Data.Length) { throw 'Incomplete PNG or trailing bytes.' }
    $stream = [IO.MemoryStream]::new($Data, $false)
    $bitmap = [Drawing.Bitmap]::new($stream)
    try {
        if ($bitmap.Width -ne $ExpectedWidth -or $bitmap.Height -ne $ExpectedHeight) { throw 'Decoded PNG dimensions mismatch.' }
        $rectangle = [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
        $locked = $bitmap.LockBits($rectangle, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $pixels = [byte[]]::new([Math]::Abs($locked.Stride) * $bitmap.Height)
            [Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
            $transparent = 0
            $partial = 0
            $opaque = 0
            for ($i = 3; $i -lt $pixels.Length; $i += 4) {
                if ($pixels[$i] -eq 0) { $transparent++ }
                elseif ($pixels[$i] -eq 255) { $opaque++ }
                else { $partial++ }
            }
        } finally { $bitmap.UnlockBits($locked) }
        if ($RequireTransparency) {
            if ($transparent -eq 0 -or $partial -eq 0 -or $opaque -eq 0) { throw 'PNG alpha must include transparent, antialiased, and opaque pixels.' }
            foreach ($point in @(@(0, 0), @(($bitmap.Width - 1), 0), @(0, ($bitmap.Height - 1)), @(($bitmap.Width - 1), ($bitmap.Height - 1)))) {
                if ($bitmap.GetPixel($point[0], $point[1]).A -ne 0) { throw 'Icon corners must be fully transparent.' }
            }
        }
        return [ordered]@{ Width = $bitmap.Width; Height = $bitmap.Height; TransparentPixels = $transparent; EdgePixels = $partial; OpaquePixels = $opaque }
    } finally {
        $bitmap.Dispose()
        $stream.Dispose()
    }
}

function New-Ico($Frames) {
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Frames.Count)
        [uint32]$offset = 6 + 16 * $Frames.Count
        foreach ($frame in $Frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $Frames) { $writer.Write([byte[]]$frame.Bytes) }
        $writer.Flush()
        return ,$stream.ToArray()
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Assert-Ico([byte[]] $Data, $Frames) {
    $stream = [IO.MemoryStream]::new($Data, $false)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1 -or $reader.ReadUInt16() -ne $Frames.Count) { throw 'ICO header mismatch.' }
        [long]$expectedOffset = 6 + 16 * $Frames.Count
        foreach ($frame in $Frames) {
            $widthByte = $reader.ReadByte()
            $heightByte = $reader.ReadByte()
            $colors = $reader.ReadByte()
            $reserved = $reader.ReadByte()
            $planes = $reader.ReadUInt16()
            $bits = $reader.ReadUInt16()
            $length = $reader.ReadUInt32()
            $offset = $reader.ReadUInt32()
            $width = if ($widthByte -eq 0) { 256 } else { [int]$widthByte }
            $height = if ($heightByte -eq 0) { 256 } else { [int]$heightByte }
            if ($width -ne $frame.Size -or $height -ne $frame.Size -or $colors -ne 0 -or $reserved -ne 0 -or $planes -ne 1 -or $bits -ne 32) { throw 'ICO directory metadata mismatch.' }
            if ($offset -ne $expectedOffset -or $length -ne $frame.Bytes.Length -or [long]$offset + $length -gt $Data.Length) { throw 'ICO offsets or lengths mismatch.' }
            $payload = [byte[]]::new($length)
            [Array]::Copy($Data, [long]$offset, $payload, 0L, [long]$length)
            if ((Get-Sha256 $payload) -ne $frame.Sha256) { throw 'ICO payload hash mismatch.' }
            $null = Assert-Png $payload $frame.Size $frame.Size
            $expectedOffset += $length
        }
        if ($expectedOffset -ne $Data.Length) { throw 'ICO cumulative length mismatch.' }
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
    # Native Windows decoder read-back, independent of the directory parser.
    $nativeStream = [IO.MemoryStream]::new($Data, $false)
    $nativeIcon = [Drawing.Icon]::new($nativeStream)
    try {
        if ($nativeIcon.Width -lt 16 -or $nativeIcon.Height -lt 16) { throw 'Windows icon decoder returned invalid dimensions.' }
    } finally {
        $nativeIcon.Dispose()
        $nativeStream.Dispose()
    }
}

function New-PreviewPng($Frames) {
    $sheet = [Drawing.Bitmap]::new(1040, 720, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($sheet)
    $stream = [IO.MemoryStream]::new()
    $headingFont = [Drawing.Font]::new('Segoe UI', 14, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $labelFont = [Drawing.Font]::new('Segoe UI', 12, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    try {
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        foreach ($panel in @(@{ Y = 0; Background = '#F4F6FA'; Foreground = '#1E293B'; Name = 'LocalDocSearch / Light' }, @{ Y = 360; Background = '#182131'; Foreground = '#E8EEF8'; Name = 'LocalDocSearch / Dark' })) {
            $backgroundBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($panel.Background))
            $foregroundBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($panel.Foreground))
            try {
                $graphics.FillRectangle($backgroundBrush, 0, $panel.Y, 1040, 360)
                $graphics.DrawString($panel.Name, $headingFont, $foregroundBrush, 28, $panel.Y + 20)
                $x = 28
                foreach ($frame in $Frames) {
                    $cellWidth = [Math]::Max(68, $frame.Size + 32)
                    $imageX = $x + [int](($cellWidth - $frame.Size) / 2)
                    $imageY = $panel.Y + 188 - [int]($frame.Size / 2)
                    $imageStream = [IO.MemoryStream]::new([byte[]]$frame.Bytes, $false)
                    $image = [Drawing.Bitmap]::new($imageStream)
                    try { $graphics.DrawImageUnscaled($image, $imageX, $imageY) }
                    finally { $image.Dispose(); $imageStream.Dispose() }
                    $label = '{0} px' -f $frame.Size
                    $labelWidth = $graphics.MeasureString($label, $labelFont).Width
                    $graphics.DrawString($label, $labelFont, $foregroundBrush, [single]($x + ($cellWidth - $labelWidth) / 2), [single]($panel.Y + 333))
                    $x += $cellWidth
                }
            } finally { $foregroundBrush.Dispose(); $backgroundBrush.Dispose() }
        }
        $sheet.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $headingFont.Dispose()
        $labelFont.Dispose()
        $stream.Dispose()
        $graphics.Dispose()
        $sheet.Dispose()
    }
}

function Write-Asset([string] $Name, [byte[]] $Bytes) {
    if ($Name -notin $assetNames) { throw "Unexpected asset output: $Name" }
    [IO.File]::WriteAllBytes((Join-Path $assetsPath $Name), $Bytes)
}

# Check expanded stroke bounds before exporting: the handle and every foreground
# element must remain inside the tile's inset bounding square, not just the canvas.
foreach ($shape in $scene) {
    $path = New-ShapePath $shape
    try {
        $bounds = $path.GetBounds()
        if ($shape.ContainsKey('Stroke')) {
            # All strokes use round joins/caps. Inflate by half their width;
            # GetBounds(matrix, pen) overestimates with the pen's miter limit.
            [single]$halfStroke = $shape.StrokeWidth / 2.0
            $bounds.Inflate($halfStroke, $halfStroke)
        }
        # GDI+ arc bounds have sub-0.0001 design-unit float error.
        $epsilon = 0.001
        if ($bounds.Left -lt (8 - $epsilon) -or $bounds.Top -lt (8 - $epsilon) -or $bounds.Right -gt (248 + $epsilon) -or $bounds.Bottom -gt (248 + $epsilon)) {
            throw "Scene geometry extends outside the tile: $($shape.Kind); bounds=$bounds"
        }
    } finally {
        $path.Dispose()
    }
}

$svg = New-SceneSvg
$xmlSettings = [Xml.XmlReaderSettings]::new()
$xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$xmlSettings.XmlResolver = $null
$stringReader = [IO.StringReader]::new($svg)
$xmlReader = [Xml.XmlReader]::Create($stringReader, $xmlSettings)
$svgDocument = [Xml.XmlDocument]::new()
try { $svgDocument.Load($xmlReader) } finally { $xmlReader.Dispose(); $stringReader.Dispose() }
if ($svgDocument.DocumentElement.LocalName -ne 'svg' -or $svgDocument.DocumentElement.NamespaceURI -ne 'http://www.w3.org/2000/svg') { throw 'Invalid SVG root.' }
$svgBytes = [Text.UTF8Encoding]::new($false).GetBytes($svg)

$frames = @(foreach ($size in $frameSizes) {
    $bytes = New-IconPng $size
    $alpha = Assert-Png $bytes $size $size
    [pscustomobject]@{ Size = $size; Bytes = $bytes; Sha256 = Get-Sha256 $bytes; Alpha = $alpha }
})
$png512 = New-IconPng 512
$alpha512 = Assert-Png $png512 512 512
$ico = New-Ico $frames
Assert-Ico $ico $frames
$preview = New-PreviewPng $frames
$null = Assert-Png $preview 1040 720 $false

Write-Asset 'app-icon.svg' $svgBytes
Write-Asset 'app-icon.ico' $ico
Write-Asset 'app-icon.png' $frames[-1].Bytes
Write-Asset 'app-icon-512.png' $png512
Write-Asset 'app-icon-preview.png' $preview

# Read back actual saved files rather than trusting the in-memory encoder alone.
$null = Assert-Png ([IO.File]::ReadAllBytes((Join-Path $assetsPath 'app-icon.png'))) 256 256
$null = Assert-Png ([IO.File]::ReadAllBytes((Join-Path $assetsPath 'app-icon-512.png'))) 512 512
Assert-Ico ([IO.File]::ReadAllBytes((Join-Path $assetsPath 'app-icon.ico'))) $frames
$savedSvg = [Xml.XmlDocument]::new()
$savedSvg.XmlResolver = $null
$savedSvg.Load((Join-Path $assetsPath 'app-icon.svg'))
if ($savedSvg.OuterXml -ne $svgDocument.OuterXml) { throw 'SVG read-back mismatch.' }

$manifest = [ordered]@{
    SchemaVersion = 1
    Generator = 'scripts/generate-app-icon.ps1'
    DesignSpace = '256x256'
    Palette = [ordered]@{ Tile = $blue; PaperAndLens = $white; Fold = $fold }
    SvgSha256 = Get-Sha256 $svgBytes
    IcoSha256 = Get-Sha256 $ico
    Png256Sha256 = $frames[-1].Sha256
    Png512Sha256 = Get-Sha256 $png512
    Png512Alpha = $alpha512
    Frames = @($frames | ForEach-Object { [ordered]@{ Width = $_.Size; Height = $_.Size; Bytes = $_.Bytes.Length; Sha256 = $_.Sha256; Alpha = $_.Alpha } })
}
$manifestJson = ($manifest | ConvertTo-Json -Depth 6) + "`n"
Write-Asset 'app-icon-manifest.json' ([Text.UTF8Encoding]::new($false).GetBytes($manifestJson))

Write-Host ('Generated and verified: {0}' -f $assetsPath)
foreach ($frame in $frames) {
    Write-Host ('{0,3} x {0,-3} | PNG {1,6} bytes | SHA256 {2} | alpha 0:{3} partial:{4} 255:{5}' -f $frame.Size, $frame.Bytes.Length, $frame.Sha256, $frame.Alpha.TransparentPixels, $frame.Alpha.EdgePixels, $frame.Alpha.OpaquePixels)
}
Write-Host ('512 x 512 | PNG {0,6} bytes | SHA256 {1}' -f $png512.Length, (Get-Sha256 $png512))
Write-Host ('ICO {0} bytes | SHA256 {1} | 9 PNG frames; directory and Windows decoder verified' -f $ico.Length, (Get-Sha256 $ico))
Write-Host 'SVG XML, PNG chunk CRC32, RGBA dimensions/alpha, saved-file read-back: passed.'
