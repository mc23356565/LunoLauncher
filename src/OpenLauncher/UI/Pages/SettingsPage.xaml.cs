using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenLauncher.Core;
using OpenLauncher.Game;
using OpenLauncher.Update;

namespace OpenLauncher.UI.Pages
{
    public class MirrorRow
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public bool Enabled { get; set; }
    }

    public class KeyRow
    {
        public string Xml { get; set; }
        public string Fingerprint { get; set; }
        public string Origin { get; set; }
        public bool Removable { get; set; }
        public Visibility RemovableVis => Removable ? Visibility.Visible : Visibility.Collapsed;
    }

    public partial class SettingsPage : UserControl, IPage
    {
        bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
            foreach (var (name, hex) in Theme.Presets)
            {
                var b = new Button { Style = (Style)FindResource("Ghost"), ToolTip = name, Padding = new Thickness(3), Margin = new Thickness(0, 0, 6, 6), Tag = hex };
                b.Content = new Ellipse { Width = 26, Height = 26, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) };
                b.Click += (s, e) => SetTheme((string)((Button)s).Tag);
                ThemePresets.Children.Add(b);
            }
        }

        public void OnShown() => Load();

        public void ScrollTo(string section)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                FrameworkElement target = section switch
                {
                    "java" => SecJava,
                    "download" => SecDownload,
                    "account" => SecAccount,
                    "ui" => SecUi,
                    "update" => SecUpdate,
                    _ => SecLaunch,
                };
                var p = target.TransformToAncestor(Body).Transform(new Point(0, 0));
                Scroller.ScrollToVerticalOffset(p.Y);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void Jump_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ScrollTo((string)((RadioButton)sender).CommandParameter);
        }

        static void Select(ComboBox box, string tag) =>
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];
        static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        void Load()
        {
            _loading = true;
            Select(Isolation, Settings.Isolation);
            Select(AfterLaunch, Settings.AfterLaunch);
            MemAuto.IsChecked = Settings.MemoryAuto;
            MemSlider.Maximum = Math.Max(4096, Util.Memory().totalMb / 1024 * 1024);
            MemSlider.Value = Settings.MemoryMb;
            UpdateMemText();
            WinW.Text = Settings.WindowWidth.ToString();
            WinH.Text = Settings.WindowHeight.ToString();
            Fullscreen.IsChecked = Settings.Fullscreen;
            WinTitle.Text = Settings.WindowTitle;
            JvmArgs.Text = Settings.JvmArgs;
            GameArgs.Text = Settings.GameArgs;

            Select(GameSource, ((int)Settings.GameSource).ToString());
            Select(ModSource, ((int)Settings.ModSource).ToString());
            Threads.Value = Settings.DownloadThreads;
            ThreadsText.Text = Settings.DownloadThreads.ToString();
            SpeedLimit.Text = Settings.SpeedLimitKb.ToString();
            LoadMirrors();

            MsClientId.Text = Settings.MicrosoftClientId;
            CfKey.Text = Settings.CurseForgeApiKey;
            ThemeHex.Text = Settings.ThemeColor;

            AutoUpdate.IsChecked = Settings.AutoCheckUpdate;
            UpdateSources.Text = string.Join(Environment.NewLine, Settings.ExtraList("update.sources"));
            UpdateStatus.Text = $"当前版本 {AppInfo.VersionString}" + (Updater.LastCheckMessage != null ? "，" + Updater.LastCheckMessage : "");
            LoadKeys();
            LoadJava();
            _loading = false;
        }

        void UpdateMemText()
        {
            var (total, avail) = Util.Memory();
            MemRow.Visibility = Visibility.Visible;
            MemSlider.Visibility = MemAuto.IsChecked == true ? Visibility.Hidden : Visibility.Visible;
            MemText.Text = MemAuto.IsChecked == true
                ? $"自动 · 空闲 {avail / 1024.0:0.#} / {total / 1024.0:0.#} GB"
                : $"{MemSlider.Value / 1024:0.##} GB · 空闲 {avail / 1024.0:0.#} GB";
        }

        void MemSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MemText == null) return;
            UpdateMemText();
            if (!_loading) Settings.MemoryMb = (int)MemSlider.Value;
        }

        void Threads_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ThreadsText == null) return;
            ThreadsText.Text = ((int)Threads.Value).ToString();
            if (!_loading) Settings.DownloadThreads = (int)Threads.Value;
        }

        void Any_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Settings.Isolation = TagOf(Isolation);
            Settings.AfterLaunch = TagOf(AfterLaunch);
            Settings.MemoryAuto = MemAuto.IsChecked == true;
            UpdateMemText();
            if (int.TryParse(WinW.Text, out var w) && w >= 320) Settings.WindowWidth = w;
            if (int.TryParse(WinH.Text, out var h) && h >= 240) Settings.WindowHeight = h;
            Settings.Fullscreen = Fullscreen.IsChecked == true;
            Settings.WindowTitle = WinTitle.Text.Trim();
            Settings.JvmArgs = JvmArgs.Text.Trim();
            Settings.GameArgs = GameArgs.Text.Trim();
            Settings.GameSource = (SourceMode)int.Parse(TagOf(GameSource));
            Settings.ModSource = (SourceMode)int.Parse(TagOf(ModSource));
            if (int.TryParse(SpeedLimit.Text, out var lim) && lim >= 0) Settings.SpeedLimitKb = lim;
            Settings.MicrosoftClientId = MsClientId.Text.Trim();
            Settings.CurseForgeApiKey = CfKey.Text.Trim();
            Settings.AutoCheckUpdate = AutoUpdate.IsChecked == true;
            Settings.SetList("update.sources", UpdateSources.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.StartsWith("http")));
            Settings.JavaPath = TagOf(JavaGlobal);
        }

        // ---------- Java ----------
        void LoadJava()
        {
            var known = JavaManager.Known;
            JavaList.ItemsSource = known;
            JavaGlobal.Items.Clear();
            JavaGlobal.Items.Add(new ComboBoxItem { Content = "按版本自动选择（推荐）", Tag = "" });
            foreach (var j in known) JavaGlobal.Items.Add(new ComboBoxItem { Content = j.Display, Tag = j.JavawPath, ToolTip = j.JavawPath });
            Select(JavaGlobal, Settings.JavaPath);
            if (known.Count == 0) _ = ScanJavaAsync(false);
        }

        async Task ScanJavaAsync(bool force)
        {
            await Task.Run(() => JavaManager.ScanAsync(force));
            _loading = true;
            LoadJavaListOnly();
            _loading = false;
        }

        void LoadJavaListOnly()
        {
            var known = JavaManager.Known;
            JavaList.ItemsSource = null;
            JavaList.ItemsSource = known;
            JavaGlobal.Items.Clear();
            JavaGlobal.Items.Add(new ComboBoxItem { Content = "按版本自动选择（推荐）", Tag = "" });
            foreach (var j in known) JavaGlobal.Items.Add(new ComboBoxItem { Content = j.Display, Tag = j.JavawPath, ToolTip = j.JavawPath });
            Select(JavaGlobal, Settings.JavaPath);
        }

        async void ScanJava_Click(object sender, RoutedEventArgs e)
        {
            await ScanJavaAsync(true);
            Dialogs.Toast($"找到 {JavaManager.Known.Count} 个 Java", MainWindow.ToastKind.Ok);
        }

        void AddJava_Click(object sender, RoutedEventArgs e)
        {
            var f = Pickers.PickFile("选择 javaw.exe", "javaw.exe|javaw.exe;java.exe");
            if (f == null) return;
            if (JavaManager.Inspect(f) == null) { Dialogs.Toast("这不是一个可用的 Java", MainWindow.ToastKind.Error); return; }
            JavaManager.AddManual(f);
            _ = ScanJavaAsync(false);
        }

        async void DownloadJava_Click(object sender, RoutedEventArgs e)
        {
            var options = new List<string> { "Java 25（Minecraft 26.x）", "Java 21（1.20.5 ~ 1.21.x）", "Java 17（1.18 ~ 1.20.4）", "Java 8（1.16.5 及更早）" };
            var majors = new[] { 25, 21, 17, 8 };
            var comps = new[] { "java-runtime-epsilon", "java-runtime-delta", "java-runtime-gamma", "jre-legacy" };
            var idx = await Dialogs.Choose("下载 Java", "从 Mojang 官方下载对应的 Java 运行时：", options);
            if (idx < 0) return;
            TaskManager.Start($"下载 Java {majors[idx]}", async t =>
            {
                await JavaManager.DownloadAsync(majors[idx], comps[idx], t, 0, 1);
                await JavaManager.ScanAsync(true);
                TaskManager.OnUi(() => { _loading = true; LoadJavaListOnly(); _loading = false; });
            });
            MainWindow.Current.ToggleTasks(true);
        }

        // ---------- 镜像 ----------
        void LoadMirrors()
        {
            var disabled = new HashSet<string>(Settings.ExtraList("mirror.disabled"), StringComparer.OrdinalIgnoreCase);
            var all = Mirrors.Builtin.Concat(Settings.GetNode("mirror.custom").Select(MirrorRule.FromJson)).Concat(Settings.GetNode("mirror.remote").Select(MirrorRule.FromJson));
            MirrorList.ItemsSource = all.GroupBy(r => r.Name).Select(g => new MirrorRow
            {
                Name = g.Key,
                Title = g.Key + (g.First().Group == "mod" ? "（Mod 资源）" : "（游戏文件）"),
                Detail = $"{g.Count()} 条映射，例如 {Mirrors.Host(g.First().From)} → {Mirrors.Host(g.First().To)}",
                Enabled = !disabled.Contains(g.Key),
            }).ToList();
        }

        void MirrorToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is MirrorRow row)) return;
            var disabled = Settings.ExtraList("mirror.disabled");
            if (((CheckBox)sender).IsChecked == true) disabled.RemoveAll(n => n == row.Name);
            else if (!disabled.Contains(row.Name)) disabled.Add(row.Name);
            Settings.SetList("mirror.disabled", disabled);
            Mirrors.Reload();
            LoadMirrors();
        }

        async void AddMirror_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel();
            TextBox field(string label, string ph)
            {
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Sub") });
                var tb = new TextBox { Tag = ph, Margin = new Thickness(0, 4, 0, 10) };
                panel.Children.Add(tb);
                return tb;
            }
            var name = field("镜像名称", "我的镜像");
            var from = field("官方地址前缀", "https://piston-data.mojang.com/");
            var to = field("镜像地址前缀", "https://mirror.example.com/");
            var isMod = new CheckBox { Style = (Style)FindResource("Switch"), Content = "这是 Mod 资源（Modrinth / CurseForge）的镜像" };
            panel.Children.Add(isMod);
            if (await Dialogs.ShowAsync("添加自定义镜像", panel, new[] { "取消", "添加" }, primary: 1, width: 480) != 1) return;
            if (!from.Text.StartsWith("http") || !to.Text.StartsWith("http")) { Dialogs.Toast("地址要以 http 开头", MainWindow.ToastKind.Error); return; }
            var arr = Settings.GetNode("mirror.custom").IsArr ? Settings.GetNode("mirror.custom").Clone() : JNode.Arr();
            arr.Add(new MirrorRule(isMod.IsChecked == true ? "mod" : "game", string.IsNullOrWhiteSpace(name.Text) ? "自定义" : name.Text.Trim(), from.Text.Trim(), to.Text.Trim()).ToJson());
            Settings.Set("mirror.custom", arr);
            Mirrors.Reload();
            LoadMirrors();
        }

        // ---------- 个性化 ----------
        void SetTheme(string hex)
        {
            Settings.ThemeColor = hex;
            ThemeHex.Text = hex;
            Theme.Apply(hex);
        }

        void ThemeHex_LostFocus(object sender, RoutedEventArgs e)
        {
            var hex = ThemeHex.Text.Trim();
            if (!hex.StartsWith("#")) hex = "#" + hex;
            try { ColorConverter.ConvertFromString(hex); SetTheme(hex); }
            catch { ThemeHex.Text = Settings.ThemeColor; }
        }

        void PickBg_Click(object sender, RoutedEventArgs e)
        {
            var f = Pickers.PickFile("选择背景图片", "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif");
            if (f == null) return;
            Settings.BackgroundImage = f;
            MainWindow.Current.ApplyBackground();
        }

        void ClearBg_Click(object sender, RoutedEventArgs e)
        {
            Settings.BackgroundImage = "";
            MainWindow.Current.ApplyBackground();
        }

        // ---------- 更新 ----------
        async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateBtn.IsEnabled = false;
            UpdateStatus.Text = "正在从各个更新源拉取签名清单…";
            try
            {
                var info = await Task.Run(() => Updater.CheckAsync());
                UpdateStatus.Text = Updater.LastCheckMessage;
                if (info != null && info.IsNewer) await Dialogs.ShowUpdateAsync(info);
                LoadKeys();
            }
            catch (Exception ex) { UpdateStatus.Text = "检查失败：" + ex.Message; }
            finally { CheckUpdateBtn.IsEnabled = true; }
        }

        void LoadKeys()
        {
            var rows = new List<KeyRow>();
            void add(IEnumerable<string> keys, string origin, bool removable)
            {
                foreach (var k in keys.Where(k => !string.IsNullOrWhiteSpace(k) && !k.Contains("@@")))
                {
                    string fp;
                    try { fp = UpdateCrypto.Fingerprint(k); } catch { fp = "（无效公钥）"; }
                    if (rows.Any(r => r.Fingerprint == fp)) continue;
                    rows.Add(new KeyRow { Xml = k, Fingerprint = fp, Origin = origin, Removable = removable });
                }
            }
            add(Branding.TrustedKeys, "内置", false);
            add(Settings.ExtraList("update.userKeys"), "你手动添加的", true);
            add(Settings.GetNode("update.rotated").Select(r => r["key"].Str).Where(k => k != null), "由已信任的发布者签名轮换", true);
            var revoked = new HashSet<string>(Settings.ExtraList("update.revoked"));
            foreach (var r in rows.Where(r => revoked.Contains(r.Fingerprint))) r.Origin += "（已吊销）";
            KeyList.ItemsSource = rows;
        }

        async void AddKey_Click(object sender, RoutedEventArgs e)
        {
            var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, VerticalContentAlignment = VerticalAlignment.Top, Tag = "<RSAKeyValue>…</RSAKeyValue>" };
            var panel = new StackPanel();
            panel.Children.Add(Dialogs.Text("粘贴发布者的公钥（public-key.xml 的内容）。只添加你确实信任的人：被信任的公钥可以给你推送任何程序更新。"));
            panel.Children.Add(new Border { Height = 10 });
            panel.Children.Add(box);
            if (await Dialogs.ShowAsync("添加信任的发布者", panel, new[] { "取消", "添加" }, primary: 1, width: 520) != 1) return;
            var xml = box.Text.Trim();
            string fp;
            try { fp = UpdateCrypto.Fingerprint(xml); }
            catch { await Dialogs.Alert("公钥无效", "格式不对，应该是 <RSAKeyValue> 开头的 XML。"); return; }
            if (!await Dialogs.Confirm("确认指纹", $"公钥指纹：{fp}\n\n请和发布者公布的指纹核对一致后再添加。", "信任")) return;
            var list = Settings.ExtraList("update.userKeys");
            if (!list.Contains(xml)) list.Add(xml);
            Settings.SetList("update.userKeys", list);
            LoadKeys();
        }

        void RemoveKey_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is KeyRow row)) return;
            Settings.SetList("update.userKeys", Settings.ExtraList("update.userKeys").Where(k => k != row.Xml));
            var rotated = JNode.Arr();
            foreach (var r in Settings.GetNode("update.rotated")) if (r["key"].Str != row.Xml) rotated.Add(r.Clone());
            Settings.Set("update.rotated", rotated);
            LoadKeys();
        }
    }
}
