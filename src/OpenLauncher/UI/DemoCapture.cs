#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenLauncher.Core;

namespace OpenLauncher.UI
{
    /// <summary>
    /// 宣传片素材采集（仅 Debug 版）：OpenLauncher.exe --demo-capture &lt;输出目录&gt; &lt;脚本名&gt;
    /// 用 RenderTargetBitmap 把真实界面按高倍率离屏渲染成 PNG，并导出关键控件的位置（DIP 坐标），
    /// 后期据此精确地加鼠标轨迹、点击波纹和高亮框。
    /// </summary>
    public static partial class DemoCapture
    {
        public static string OutDir { get; private set; }
        public static double Scale = 2.5;
        static JNode Meta = JNode.Obj();

        public static bool Active => OutDir != null;

        public static void Init(string[] args)
        {
            int i = Array.IndexOf(args, "--demo-capture");
            if (i < 0 || i + 1 >= args.Length) return;
            OutDir = args[i + 1];
            Directory.CreateDirectory(OutDir);
            // 分几轮采集时合并坐标表，不覆盖上一轮的
            var metaFile = Path.Combine(OutDir, "meta.json");
            if (File.Exists(metaFile) && JNode.TryParse(File.ReadAllText(metaFile), out var old) && old.IsObj) Meta = old;
            Script = i + 2 < args.Length ? args[i + 2] : "all";
        }

        public static string Script { get; private set; }

        static MainWindow W => MainWindow.Current;
        static FrameworkElement Root => (FrameworkElement)W.Content;

        public static Task Wait(int ms) => Task.Delay(ms);

        /// <summary>等界面布局、动画、图片加载稳定</summary>
        public static async Task Settle(int ms = 450)
        {
            await Task.Delay(ms);
            Root.UpdateLayout();
            await W.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>截一帧；marks 为需要记录位置的控件</summary>
        public static void Snap(string name, params (string key, FrameworkElement el)[] marks)
        {
            Root.UpdateLayout();
            var w = Root.ActualWidth; var h = Root.ActualHeight;
            var rtb = new RenderTargetBitmap((int)Math.Round(w * Scale), (int)Math.Round(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            // 先画一层窗口背景，避免透明
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)Application.Current.FindResource("Bg"), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, w, h));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.Combine(OutDir, name + ".png"))) enc.Save(fs);

            var m = JNode.Obj().Set("w", w).Set("h", h);
            var els = JNode.Obj();
            foreach (var (key, el) in marks)
            {
                if (el == null || !el.IsVisible) continue;
                try
                {
                    var p = el.TransformToAncestor(Root).Transform(new Point(0, 0));
                    els.Set(key, JNode.Arr().Add(Math.Round(p.X, 1)).Add(Math.Round(p.Y, 1)).Add(Math.Round(el.ActualWidth, 1)).Add(Math.Round(el.ActualHeight, 1)));
                }
                catch { }
            }
            m.Set("els", els);
            Meta.Set(name, m);
            Meta.SaveFile(Path.Combine(OutDir, "meta.json"));
            Log.Info("采集 " + name);
        }

        /// <summary>连续截帧：每帧之前执行 step(i)</summary>
        public static async Task Seq(string name, int frames, Func<int, Task> step, Func<(string, FrameworkElement)[]> marks = null)
        {
            for (int i = 0; i < frames; i++)
            {
                if (step != null) await step(i);
                Snap($"{name}_{i:000}", marks?.Invoke() ?? new (string, FrameworkElement)[0]);
            }
        }

        // ---------- 查找控件 ----------
        public static T Find<T>(DependencyObject root, Func<T, bool> pred = null) where T : DependencyObject
        {
            if (root == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t && (pred == null || pred(t))) return t;
                var r = Find(c, pred);
                if (r != null) return r;
            }
            return null;
        }

        public static List<T> FindAll<T>(DependencyObject root, Func<T, bool> pred = null) where T : DependencyObject
        {
            var list = new List<T>();
            void walk(DependencyObject d)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is T t && (pred == null || pred(t))) list.Add(t);
                    walk(c);
                }
            }
            if (root != null) walk(root);
            return list;
        }

        /// <summary>按按钮上显示的文字找按钮</summary>
        public static Button ButtonByText(DependencyObject root, string text)
            => Find<Button>(root, b => (b.Content as string) == text || FindAll<TextBlock>(b).Any(t => t.Text == text));

        public static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));

        public static void Check(ToggleButton t) { t.IsChecked = true; }

        public static ScrollViewer ScrollerOf(DependencyObject d) => Find<ScrollViewer>(d);
    }
}
#endif
