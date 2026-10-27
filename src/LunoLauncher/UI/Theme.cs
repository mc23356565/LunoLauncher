using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LunoLauncher.Core;

namespace LunoLauncher.UI
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

        /// <summary>深色模式。存在设置里；没手动设过时跟随 Windows 的"应用主题"。</summary>
        public static bool Dark
        {
            get => Settings.GetBool("ui.dark", SystemPrefersDark);
            set => Settings.Set("ui.dark", value);
        }

        /// <summary>Windows 设置 → 个性化 → 颜色 → 选择应用模式。0 = 深色。</summary>
        static bool SystemPrefersDark
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        var v = k?.GetValue("AppsUseLightTheme");
                        if (v is int light) return light == 0;
                    }
                }
                catch { }
                return false;
            }
        }

        public static void Apply(string hex) => Apply(hex, Dark);

        /// <summary>按主题色 + 明暗重新生成整套界面颜色。全部走 DynamicResource，换完立刻生效。</summary>
        public static void Apply(string hex, bool dark)
        {
            var c = Parse(hex, Color.FromRgb(0x2F, 0x7B, 0xF5));
            var r = Application.Current.Resources;
            r["AccentColor"] = c;

            if (dark)
            {
                // 深色：底色带一点主题色，卡片比底色亮一档，文字反过来
                var card = Color.FromRgb(0x1B, 0x20, 0x29);
                // 太暗的主题色（比如"黑曜石"）在深色底上会像禁用状态，先提亮到能看清
                var acc = Visible(c);
                r["AccentColor"] = acc;
                r["Accent"] = Frozen(acc);
                r["AccentHover"] = Frozen(Mix(acc, Colors.White, 0.16));
                r["AccentPressed"] = Frozen(Mix(acc, Colors.Black, 0.22));
                r["AccentSoft"] = Frozen(Mix(acc, card, 0.84));
                r["AccentSoft2"] = Frozen(Mix(acc, card, 0.66));
                r["Bg"] = Frozen(Mix(acc, Color.FromRgb(0x0D, 0x10, 0x15), 0.94));
                r["Card"] = Frozen(card);
                r["CardHover"] = Frozen(Color.FromRgb(0x23, 0x29, 0x34));
                r["Line"] = Frozen(Color.FromRgb(0x2D, 0x35, 0x43));
                r["Text"] = Frozen(Color.FromRgb(0xE9, 0xED, 0xF5));
                r["Text2"] = Frozen(Color.FromRgb(0xA7, 0xB1, 0xC2));
                r["Text3"] = Frozen(Color.FromRgb(0x6C, 0x77, 0x8A));
                r["Danger"] = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
                r["DangerSoft"] = Frozen(Mix(Color.FromRgb(0xFF, 0x6B, 0x6B), card, 0.82));
                r["Ok"] = Frozen(Color.FromRgb(0x3E, 0xD2, 0x8F));
                r["OkSoft"] = Frozen(Mix(Color.FromRgb(0x3E, 0xD2, 0x8F), card, 0.82));
                r["Warn"] = Frozen(Color.FromRgb(0xF5, 0xB5, 0x4C));
                r["WarnSoft"] = Frozen(Mix(Color.FromRgb(0xF5, 0xB5, 0x4C), card, 0.82));
                r["Surface"] = Frozen(Color.FromRgb(0x15, 0x19, 0x21));
                r["SwitchOff"] = Frozen(Color.FromRgb(0x3A, 0x43, 0x54));
                r["ScrollThumb"] = Frozen(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
                r["ScrollThumbHover"] = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
                r["TooltipBg"] = Frozen(Color.FromRgb(0x2B, 0x33, 0x40));
                var titleDark = new LinearGradientBrush(Mix(acc, Color.FromRgb(0x1C, 0x22, 0x2C), 0.90),
                                                        Mix(acc, Color.FromRgb(0x14, 0x18, 0x20), 0.80), 90);
                titleDark.Freeze();
                r["TitleBrush"] = titleDark;
                return;
            }

            r["Accent"] = Frozen(c);
            r["AccentHover"] = Frozen(Mix(c, Colors.White, 0.14));
            r["AccentPressed"] = Frozen(Mix(c, Colors.Black, 0.12));
            r["AccentSoft"] = Frozen(Mix(c, Colors.White, 0.90));
            r["AccentSoft2"] = Frozen(Mix(c, Colors.White, 0.78));
            r["Card"] = Frozen(Colors.White);
            r["CardHover"] = Frozen(Mix(c, Colors.White, 0.975));
            r["Line"] = Frozen(Color.FromRgb(0xE8, 0xEC, 0xF4));
            r["Text"] = Frozen(Color.FromRgb(0x10, 0x18, 0x28));
            r["Text2"] = Frozen(Color.FromRgb(0x47, 0x54, 0x67));
            r["Text3"] = Frozen(Color.FromRgb(0x98, 0xA2, 0xB3));
            r["Danger"] = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
            r["DangerSoft"] = Frozen(Color.FromRgb(0xFD, 0xEC, 0xEC));
            r["Ok"] = Frozen(Color.FromRgb(0x22, 0xA0, 0x6B));
            r["OkSoft"] = Frozen(Color.FromRgb(0xE3, 0xF6, 0xEE));
            r["Warn"] = Frozen(Color.FromRgb(0xE0, 0x8A, 0x00));
            r["WarnSoft"] = Frozen(Color.FromRgb(0xFF, 0xF4, 0xE0));
            r["Surface"] = Frozen(Mix(c, Colors.White, 0.985));
            r["SwitchOff"] = Frozen(Color.FromRgb(0xC9, 0xD0, 0xDB));
            r["ScrollThumb"] = Frozen(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
            r["ScrollThumbHover"] = Frozen(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            r["TooltipBg"] = Frozen(Color.FromArgb(0xF0, 0x1F, 0x26, 0x33));
            // 标题栏跟着主题色走：上浅下深的同色渐变，换主题时能明显看出来
            var title = new LinearGradientBrush(Mix(c, Colors.White, 0.91), Mix(c, Colors.White, 0.74), 90);
            title.Freeze();
            r["TitleBrush"] = title;
            // 整个界面的底色也带一点主题色，但保持足够浅，不影响可读性
            r["Bg"] = Frozen(Mix(c, Colors.White, 0.965));
        }

        /// <summary>相对亮度（0~1），用来判断颜色在深色底上够不够亮。</summary>
        public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        /// <summary>深色模式下保证主题色够亮；亮度不足就往白色方向提。</summary>
        public static Color Visible(Color c)
        {
            var r = c;
            for (int i = 0; i < 8 && Luma(r) < 0.40; i++) r = Mix(r, Colors.White, 0.18);
            return r;
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
