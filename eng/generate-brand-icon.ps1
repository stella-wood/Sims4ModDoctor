param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$appXamlPath = Join-Path $ProjectRoot 'src\Sims4ModDoctor.Desktop\App.xaml'
$outputPath = Join-Path $ProjectRoot 'src\Sims4ModDoctor.Desktop\Assets\Sims4ModDoctor.ico'

[xml]$appXaml = Get-Content -LiteralPath $appXamlPath -Raw
$namespaces = [System.Xml.XmlNamespaceManager]::new($appXaml.NameTable)
$namespaces.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$sourceNode = $appXaml.SelectSingleNode('//p:DrawingImage[@x:Key="BrandMarkImage"]', $namespaces)
if ($null -eq $sourceNode) {
    throw 'BrandMarkImage was not found in App.xaml.'
}

$drawingGroupNode = $sourceNode.SelectSingleNode(
    'p:DrawingImage.Drawing/p:DrawingGroup',
    $namespaces)
if ($null -eq $drawingGroupNode) {
    throw 'BrandMarkImage does not contain a DrawingGroup.'
}

$drawingXaml = @"
<DrawingImage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <DrawingImage.Drawing>
    <DrawingGroup>
$($drawingGroupNode.InnerXml)
    </DrawingGroup>
  </DrawingImage.Drawing>
</DrawingImage>
"@
$drawingImage = [System.Windows.Markup.XamlReader]::Parse($drawingXaml)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[object]]::new()

foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $scale = $size / 32.0
        $backgroundBrush = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Color]::FromRgb(0xF0, 0xEC, 0xF4))
        $borderBrush = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Color]::FromRgb(0xDD, 0xD6, 0xE6))
        $borderPen = [System.Windows.Media.Pen]::new($borderBrush, [Math]::Max(0.5, 0.7 * $scale))
        $backgroundRect = [System.Windows.Rect]::new(
            1 * $scale,
            1 * $scale,
            $size - (2 * $scale),
            $size - (2 * $scale))
        $context.DrawRoundedRectangle(
            $backgroundBrush,
            $borderPen,
            $backgroundRect,
            6 * $scale,
            6 * $scale)

        $brush = [System.Windows.Media.DrawingBrush]::new($drawingImage.Drawing)
        $brush.Stretch = [System.Windows.Media.Stretch]::Uniform
        $brush.AlignmentX = [System.Windows.Media.AlignmentX]::Center
        $brush.AlignmentY = [System.Windows.Media.AlignmentY]::Center
        $iconInset = 2 * $scale
        $context.DrawRectangle(
            $brush,
            $null,
            [System.Windows.Rect]::new(
                $iconInset,
                $iconInset,
                $size - (2 * $iconInset),
                $size - (2 * $iconInset)))
    }
    finally {
        $context.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size,
        $size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        $images.Add([pscustomobject]@{
            Size = $size
            Data = $stream.ToArray()
        })
    }
    finally {
        $stream.Dispose()
    }
}

$outputDirectory = Split-Path -Parent $outputPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$fileStream = [System.IO.File]::Create($outputPath)
$writer = [System.IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Data.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Data)
    }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Write-Output $outputPath
