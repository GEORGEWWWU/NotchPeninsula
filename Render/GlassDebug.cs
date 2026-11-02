using System;
using System.IO;
using System.Text;
using Microsoft.Win32;
using SkiaSharp;

namespace NotchPeninsula
{
    // 临时调试：把真实渲染链路里的玻璃位图 / 背板 / 参数 dump 到 %TEMP%\nps-glass-dump。
    // 由 HKCU\SOFTWARE\NotchPeninsula 的 GlassDump=1 开启（默认关），限 8 次、间隔 ≥1.5s。
    // 用途：验证「详情页边缘折射看不到」到底是合成没出形变，还是背板本身没内容可弯。
    internal static class GlassDebug
    {
        private const int MaxDumps = 60;
        private static bool _enabled;
        private static long _nextCheck;
        private static long _lastDump;
        private static int _count;

        private static bool Enabled()
        {
            long now = Environment.TickCount64;
            if (now < _nextCheck) return _enabled;
            _nextCheck = now + 2000;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\NotchPeninsula");
                _enabled = key?.GetValue("GlassDump") is int v && v == 1;
            }
            catch { _enabled = false; }
            return _enabled;
        }

        public static void Dump(SKBitmap glass, SKImage backdrop, SKRect src, SKRect dst,
            float logicalW, float logicalH, float rTop, float rBottom)
        {
            if (_count >= MaxDumps) return;
            long now = Environment.TickCount64;
            if (now - _lastDump < 700) return;
            if (!Enabled()) return;
            _lastDump = now;
            int n = ++_count;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "nps-glass-dump");
                Directory.CreateDirectory(dir);

                using (var img = SKImage.FromBitmap(glass))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 95))
                using (var fs = File.Create(Path.Combine(dir, $"glass-{n}.png")))
                    data.SaveTo(fs);

                // 背板按 src 裁出来（1:1）。SKImage 必须转成位图再裁 —— 直接 DrawImage
                // 在透明源上会得到全黑，看不出内容。
                using (var full = SKBitmap.FromImage(backdrop))
                {
                    var ri = new SKRectI((int)src.Left, (int)src.Top, (int)src.Right, (int)src.Bottom);
                    ri.Intersect(new SKRectI(0, 0, full.Width, full.Height));
                    using var sub = new SKBitmap();
                    if (full.ExtractSubset(sub, ri))
                    {
                        using var img2 = SKImage.FromBitmap(sub);
                        using var d2 = img2.Encode(SKEncodedImageFormat.Png, 95);
                        using var fs2 = File.Create(Path.Combine(dir, $"backdrop-{n}.png"));
                        d2.SaveTo(fs2);
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"glassPx={glass.Width}x{glass.Height}  logical={logicalW:F1}x{logicalH:F1}");
                sb.AppendLine($"src=({src.Left:F1},{src.Top:F1},{src.Right:F1},{src.Bottom:F1}) dst=({dst.Left:F1},{dst.Top:F1},{dst.Right:F1},{dst.Bottom:F1})");
                sb.AppendLine($"rTop={rTop:F1} rBottom={rBottom:F1}");
                sb.AppendLine($"dxgi={IslandBackdrop.UsingDxgi} downscale={IslandBackdrop.CurrentDownscale:F1}");
                sb.AppendLine(LiquidGlass2.DebugState());
                File.WriteAllText(Path.Combine(dir, $"info-{n}.txt"), sb.ToString());
            }
            catch { }
        }
    }
}
