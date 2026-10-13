Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$outputPath = Join-Path $PSScriptRoot '..\Main\Assets\LogCompression.ico'
$outputPath = [System.IO.Path]::GetFullPath($outputPath)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()

foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $scale = $size / 256.0
    $context.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))

    $brand = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#245BCC'))
    $navy = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#14253D'))
    $paper = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#FFFFFF'))
    $fold = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#DCE8FF'))
    $zip = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#8FB5FF'))
    $softLine = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#9EB8E6'))

    # 圆角底色呼应主界面的蓝色操作按钮，日志纸张和 ZIP 拉链组合为工具识别符。
    $context.DrawRoundedRectangle($brand, $null, [System.Windows.Rect]::new(0, 0, 256, 256), 48, 48)
    $context.DrawRoundedRectangle($paper, $null, [System.Windows.Rect]::new(43, 32, 170, 192), 16, 16)

    $foldGeometry = [System.Windows.Media.StreamGeometry]::new()
    $geometryContext = $foldGeometry.Open()
    $geometryContext.BeginFigure([System.Windows.Point]::new(163, 33), $true, $true)
    $geometryContext.LineTo([System.Windows.Point]::new(211, 81), $true, $false)
    $geometryContext.LineTo([System.Windows.Point]::new(177, 81), $true, $false)
    $geometryContext.LineTo([System.Windows.Point]::new(163, 67), $true, $false)
    $geometryContext.Close()
    $context.DrawGeometry($fold, $null, $foldGeometry)

    foreach ($line in @(@(66, 93, 114), @(66, 116, 93), @(66, 139, 108))) {
        $context.DrawRoundedRectangle($softLine, $null,
            [System.Windows.Rect]::new($line[0], $line[1], $line[2], 9), 4.5, 4.5)
    }

    $context.DrawRoundedRectangle($navy, $null, [System.Windows.Rect]::new(43, 163, 170, 61), 0, 0)
    $context.DrawRoundedRectangle($zip, $null, [System.Windows.Rect]::new(122, 163, 13, 61), 2, 2)
    foreach ($toothY in @(171, 184, 197, 210)) {
        $context.DrawRoundedRectangle($paper, $null,
            [System.Windows.Rect]::new(116, $toothY, 25, 5), 2.5, 2.5)
    }
    $context.DrawRoundedRectangle($zip, $null, [System.Windows.Rect]::new(127, 151, 30, 22), 5, 5)
    $context.DrawRoundedRectangle($navy, $null, [System.Windows.Rect]::new(134, 156, 16, 12), 3, 3)

    $context.Pop()
    $context.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames.Add($stream.ToArray())
    $stream.Dispose()
}

# ICO 目录保留多种像素尺寸；PNG 帧让桌面外壳在不同 DPI 下选择合适清晰度。
$directoryLength = 6 + 16 * $sizes.Count
$offset = $directoryLength
$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)

for ($index = 0; $index -lt $sizes.Count; $index++) {
    $size = $sizes[$index]
    $frame = $frames[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Length
}

foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Flush()
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outputPath)) | Out-Null
[System.IO.File]::WriteAllBytes($outputPath, $output.ToArray())
$writer.Dispose()
$output.Dispose()
Write-Output "Created $outputPath"
