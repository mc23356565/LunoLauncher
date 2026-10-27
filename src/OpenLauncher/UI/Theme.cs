using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenLauncher.UI
{
    public static class Theme
    {
        public static readonly (string name, string hex)[] Presets =
        {
            ("天空蓝", "#2F7BF5"), ("草方块绿", "#22A06B"), ("末影紫", "#7C5CFA"), ("岩浆橙", "#F08A24"),
            ("樱花粉", "#E5568B"), ("海晶青", "#0EA5B7"), ("红石红", "#D9483B"), ("黑曜石", "#3C4454"),
        };

        public static Color Parse(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
        }

        public static void Apply(string hex)
        {
            var c = Parse(hex, Color.FromRgb(0x2F, 0x7B, 0xF5));
            var r = Application.Current.Resources;
            r["AccentColor"] = c;
            r["Accent"] = Frozen(c);
            r["AccentHover"] = Frozen(Mix(c, Colors.White, 0.14));
            r["AccentPressed"] = Frozen(Mix(c, Colors.Black, 0.12));
            r["AccentSoft"] = Frozen(Mix(c, Colors.White, 0.9));
            r["AccentSoft2"] = Frozen(Mix(c, Colors.White, 0.78));
            var title = new LinearGradientBrush(c, Mix(c, Color.FromRgb(0x7C, 0x5C, 0xFA), 0.22), 0);
            title.Freeze();
            r["TitleBrush"] = title;
        }

        static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static Color Mix(Color a, Color b, double t)
        {
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
        }

        /// <summary>读本地图片为冻结的 BitmapImage（不锁文件）</summary>
        public static BitmapImage LoadImage(string path, int decodeWidth = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                using (var fs = File.OpenRead(path)) { bmp.StreamSource = fs; bmp.EndInit(); }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public static BitmapImage LoadImage(byte[] data, int decodeWidth = 0)
        {
            try
            {
                if (data == null || data.Length == 0) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
