using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JustWatch.Core.Models;

namespace JustWatch.TrayApp;

public static class TrayIconGenerator
{
    private static readonly Color DimGrey = Color.FromRgb(0x66, 0x66, 0x66);
    private static readonly Color BgColor = Color.FromArgb(180, 0x1e, 0x1e, 0x1e);

    public static BitmapSource Generate(IEnumerable<ProcessConfig> services, Func<string, ProcessStatus> getStatus)
    {
        try
        {
            var dots = services.Where(p => p.ShowTrayDot && !p.HideFromTray).ToArray();
            if (dots.Length == 0) return new BitmapImage(new Uri("pack://application:,,,/icon.ico"));
            const int size = 64;
            var columns = (int)Math.Ceiling(Math.Sqrt(dots.Length));
            var rows = (int)Math.Ceiling((double)dots.Length / columns);
            var sq = dots.Length == 3 ? 24 : Math.Min(40, (52 - (columns - 1) * 4) / columns);
            const int gap = 4;
            const double corner = 5;
            const double dpi = 96;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Semi-transparent dark background for visibility on any taskbar
                dc.DrawRoundedRectangle(new SolidColorBrush(BgColor), null,
                    new Rect(0, 0, size, size), 8, 8);

                for (var i = 0; i < dots.Length; i++)
                {
                    var row = i / columns;
                    var rowCount = Math.Min(columns, dots.Length - row * columns);
                    var left = (size - rowCount * sq - (rowCount - 1) * gap) / 2.0;
                    var top = dots.Length == 3 ? 6.0 : (size - rows * sq - (rows - 1) * gap) / 2.0;
                    var color = IsActive(getStatus(dots[i].Id))
                        ? (Color)ColorConverter.ConvertFromString(dots[i].TrayColor) : DimGrey;
                    dc.DrawRoundedRectangle(new SolidColorBrush(color), null,
                        new Rect(left + (i % columns) * (sq + gap), top + row * (sq + gap), sq, sq), corner, corner);
                }
            }

            var rtb = new RenderTargetBitmap(size, size, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch
        {
            return new BitmapImage(new Uri("pack://application:,,,/icon.ico"));
        }
    }

    private static bool IsActive(ProcessStatus status) =>
        status is ProcessStatus.Running or ProcessStatus.Starting or ProcessStatus.RecoveringWithAgent;
}
