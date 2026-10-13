# Render the vector master with WPF. No external tools or fonts required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
[xml]$svg = Get-Content (Join-Path $root 'src/JustWatch.Core/Assets/justwatch.svg') -Raw
function Brush($color) {
    if (!$color -or $color -eq 'none') { return $null }
    return [Windows.Media.BrushConverter]::new().ConvertFromString($color)
}
function Render([int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
    foreach ($node in $svg.svg.ChildNodes) {
        $fill = Brush $node.GetAttribute('fill')
        $pen = $null
        if ($node.GetAttribute('stroke')) {
            $pen = [Windows.Media.Pen]::new((Brush $node.GetAttribute('stroke')), [double]$node.GetAttribute('stroke-width'))
            $pen.LineJoin = 'Round'; $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
        }
        switch ($node.LocalName) {
            'rect' { $dc.DrawRoundedRectangle($fill, $pen, [Windows.Rect]::new([double]$node.x,[double]$node.y,[double]$node.width,[double]$node.height),[double]$node.rx,[double]$node.rx) }
            'circle' { $dc.DrawEllipse($fill,$pen,[Windows.Point]::new([double]$node.cx,[double]$node.cy),[double]$node.r,[double]$node.r) }
            'path' { $dc.DrawGeometry($fill,$pen,[Windows.Media.Geometry]::Parse($node.d)) }
        }
    }
    $dc.Pop(); $dc.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new(); $encoder.Save($stream)
    return ,$stream.ToArray()
}
$sizes = @(16,20,24,32,40,48,64,128,256)
$images = @($sizes | ForEach-Object { ,(Render $_) })
$iconPath = Join-Path $root 'src/JustWatch.TrayApp/icon.ico'
$stream = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $root 'src/JustWatch.Core/Assets/justwatch.png'),(Render 1024))
Write-Host 'JustWatch SVG rendered to a 1024px PNG and nine-size Windows icon.'
