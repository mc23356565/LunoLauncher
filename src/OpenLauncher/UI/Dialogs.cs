using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using OpenLauncher.Core;
using OpenLauncher.Update;

namespace OpenLauncher.UI
{
    /// <summary>窗内对话框（不弹系统窗口），全部 async。</summary>
    public static class Dialogs
    {
        static Style S(string key) => (Style)Application.Current.FindResource(key);
        static Brush B(string key) => (Brush)Application.Current.FindResource(key);

        /// <summary>通用对话框：标题 + 任意内容 + 一排按钮。返回点击的按钮下标（Esc = -1）。</summary>
        public static Task<int> ShowAsync(string title, UIElement body, string[] buttons, int primary = 0, int danger = -1, double width = 440)
        {
            var tcs = new TaskCompletionSource<int>();
            var card = new Border
            {
                Background = B("Card"),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(24, 20, 24, 18),
                Width = width,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.25 },
            };
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = title, Style = S("H1"), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
            if (body != null) root.Children.Add(body);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            void close(int idx)
            {
                MainWindow.Current.CloseDialogContent(card);
                tcs.TrySetResult(idx);
            }
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    Content = buttons[i],
                    Margin = new Thickness(8, 0, 0, 0),
                    MinWidth = 76,
                    Style = i == danger ? S("DangerButton") : i == primary ? S("Primary") : S("BaseButton"),
                    IsDefault = i == primary,
                };
                btn.Click += (s, e) => close(idx);
                row.Children.Add(btn);
            }
            root.Children.Add(row);
            card.Child = root;
            card.KeyDown += (s, e) => { if (e.Key == Key.Escape) close(-1); };
            MainWindow.Current.ShowDialogContent(card);
            card.Focusable = true;
            card.Loaded += (s, e) => card.Focus();
            return tcs.Task;
        }

        public static TextBlock Text(string text) => new TextBlock { Text = text, Style = S("Body"), TextWrapping = TextWrapping.Wrap };

        public static Task Alert(string title, string text) => ShowAsync(title, Text(text), new[] { "知道了" });

        public static async Task<bool> Confirm(string title, string text, string ok = "确定", string cancel = "取消", bool danger = false)
            => await ShowAsync(title, Text(text), new[] { cancel, ok }, primary: 1, danger: danger ? 1 : -1) == 1;

        public static async Task<string> Prompt(string title, string label, string value = "", string placeholder = "")
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(label)) panel.Children.Add(new TextBlock { Text = label, Style = S("Body"), Margin = new Thickness(0, 0, 0, 8) });
            var box = new TextBox { Text = value, Tag = placeholder };
            panel.Children.Add(box);
            box.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            var r = await ShowAsync(title, panel, new[] { "取消", "确定" }, primary: 1);
            return r == 1 ? box.Text.Trim() : null;
        }

        public static async Task<int> Choose(string title, string text, IList<string> options)
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(text)) panel.Children.Add(new TextBlock { Text = text, Style = S("Body"), Margin = new Thickness(0, 0, 0, 10) });
            var list = new ListBox { Style = S("CardList"), MaxHeight = 320 };
            foreach (var o in options) list.Items.Add(new TextBlock { Text = o, TextWrapping = TextWrapping.Wrap });
            if (options.Count > 0) list.SelectedIndex = 0;
            panel.Children.Add(list);
            var r = await ShowAsync(title, panel, new[] { "取消", "确定" }, primary: 1);
            return r == 1 ? list.SelectedIndex : -1;
        }

        /// <summary>大段文字（日志、说明），可复制</summary>
        public static Task<int> Long(string title, string text, string[] buttons = null, double width = 620)
        {
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 320,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            return ShowAsync(title, box, buttons ?? new[] { "关闭" }, width: width);
        }

        public static void Toast(string text, MainWindow.ToastKind kind = MainWindow.ToastKind.Info) => MainWindow.Current?.Toast(text, kind);

        // ---------- 更新 ----------
        public static async Task ShowUpdateAsync(UpdateInfo info)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"当前 {AppInfo.VersionString}  →  新版本 {info.Version}", Style = S("Body"), FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"签名者 {info.Signer} · 来源 {new Uri(info.FoundAt).Host} · {Util.FormatSize(info.Size)}", Style = S("Sub"), Margin = new Thickness(0, 4, 0, 10) });
            if (!string.IsNullOrWhiteSpace(info.Notes))
                panel.Children.Add(new TextBox { Text = info.Notes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top });
            panel.Children.Add(new TextBlock { Text = "更新文件经过发布者签名校验，可以从任意镜像下载。", Style = S("Sub"), Margin = new Thickness(0, 10, 0, 0) });
            var r = await ShowAsync("发现新版本", panel, new[] { "以后再说", "立即更新" }, primary: 1, width: 520);
            if (r == 1)
            {
                TaskManager.Start($"更新到 {info.Version}", t => Updater.DownloadAndApplyAsync(info, t));
                MainWindow.Current.ToggleTasks(true);
            }
        }
    }
}
