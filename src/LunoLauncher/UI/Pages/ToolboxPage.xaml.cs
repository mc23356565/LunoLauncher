using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LunoLauncher.Core;

namespace LunoLauncher.UI.Pages
{
    /// <summary>工具箱 → 实用在线网页里的一条</summary>
    public class WebTool
    {
        public string Name { get; set; }
        public string Desc { get; set; }
        public string Url { get; set; }
        /// <summary>图标的主色（也是抓不到 favicon 时自绘图标的填充色）</summary>
        public Brush BadgeBrush { get; set; }
        /// <summary>本地自绘的图标，抓不到 favicon 时用它</summary>
        public Geometry IconGeometry { get; set; }

        /// <summary>卡片右下角显示的域名，不显示路径</summary>
        public string Host
        {
            get
            {
                try { return new Uri(Url).Host.Replace("www.", ""); }
                catch { return Url; }
            }
        }

        /// <summary>站点图标：按惯例取根目录的 favicon.ico</summary>
        public string IconUrl => "https://" + Host + "/favicon.ico";
    }

    /// <summary>
    /// 工具箱。现在只有「实用网页」是真的，另外两个先占个位置，
    /// 做成独立窗口以后再从这里点开。
    /// </summary>
    public partial class ToolboxPage : UserControl, IPage
    {
        static Brush Hex(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        WebTool[] _tools;

        public ToolboxPage()
        {
            InitializeComponent();
            _tools = new[]
            {
                new WebTool
                {
                    Name = "官方中文 Wiki", BadgeBrush = Hex("#3C8527"), IconGeometry = Geo("geoWiki"),
                    Desc = "方块、合成、红石、结构与教程，最全最准的中文资料",
                    Url = "https://zh.minecraft.wiki/",
                },
                new WebTool
                {
                    Name = "错误日志分析", BadgeBrush = Hex("#E08A00"), IconGeometry = Geo("geoLog"),
                    Desc = "把 latest.log 或崩溃报告贴上去，生成可分享的链接，还能看出常见报错",
                    Url = "https://mclo.gs/",
                },
                new WebTool
                {
                    Name = "壁纸生成器", BadgeBrush = Hex("#4F6BFF"), IconGeometry = Geo("geoWall"),
                    Desc = "用你的皮肤生成 Minecraft 风格的壁纸，尺寸和场景都能挑",
                    Url = "https://minecraft.novaskin.me/wallpapers",
                },
                new WebTool
                {
                    Name = "服务器状态查询", BadgeBrush = Hex("#22A06B"), IconGeometry = Geo("geoServer"),
                    Desc = "查某个服务器在不在线、多少人、延迟多少，不用先进游戏",
                    Url = "https://mcstatus.io/",
                },
                new WebTool
                {
                    Name = "皮肤动图生成", BadgeBrush = Hex("#7C5CFF"), IconGeometry = Geo("geoGif"),
                    Desc = "把皮肤做成 GIF 动图，方便发群里或者当头像",
                    Url = "https://mcgif.cn/",
                },
                new WebTool
                {
                    Name = "物品贴图查找", BadgeBrush = Hex("#0E9AA7"), IconGeometry = Geo("geoItem"),
                    Desc = "按名字找原版物品、方块、状态效果的图标，直接下 PNG",
                    Url = "https://mcicons.ccleaf.com/",
                },
                new WebTool
                {
                    Name = "像素画生成", BadgeBrush = Hex("#C2410C"), IconGeometry = Geo("geoPixel"),
                    Desc = "把任意图片转成方块像素画，给出每种方块要摆多少个",
                    Url = "https://mcpixelart.com/",
                },
                new WebTool
                {
                    Name = "头颅装饰站", BadgeBrush = Hex("#E5484D"), IconGeometry = Geo("geoHead"),
                    Desc = "上万种玩家头颅和装饰方块，直接给 /give 指令",
                    Url = "https://minecraft-heads.com",
                },
            };
            WebList.ItemsSource = _tools;
            NavWeb.IsChecked = true;
        }

        Geometry Geo(string key) => (Geometry)FindResource(key);

        public void OnShown() { }

        /// <summary>直接切到「种子查询」面板</summary>
        public void ShowSeed() => ShowSeed(null, 0);

        /// <summary>
        /// 切到「种子查询」并按命令行给初始值：<c>--page seed seed=4 dim=-1 layer=2</c>。
        /// 只给截图 / 快捷方式用，缺参数就用界面上现有的值。
        /// </summary>
        public void ShowSeed(string[] args, int start)
        {
            NavSeed.IsChecked = true;
            if (SeedPanel != null) SeedPanel.Visibility = Visibility.Visible;
            if (WebPanel != null) WebPanel.Visibility = Visibility.Collapsed;
            InitSeedIfNeeded();
            if (args == null) return;
            bool changed = false;
            int openVersionDrop = 0;
            string forceTypes = null;
            for (int i = start; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim();
                int eq = a.IndexOf('=');
                if (eq <= 0) continue;
                string key = a.Substring(0, eq).ToLowerInvariant();
                string val = a.Substring(eq + 1);
                if (key == "seed") { SeedBox.Text = val; changed = true; }
                else if (key == "x" && int.TryParse(val, out int px)) { PlayerXBox.Text = px.ToString(); changed = true; }
                else if (key == "z" && int.TryParse(val, out int pz)) { PlayerZBox.Text = pz.ToString(); changed = true; }
                else if (key == "dim" && int.TryParse(val, out int d))
                {
                    int idx = Array.IndexOf(DimIds, d);
                    if (idx >= 0) { DimBox.SelectedIndex = idx; changed = true; }
                }
                else if (key == "ver")
                {
                    var item = FindVersionItem(val);
                    if (item != null) { VersionBox.SelectedItem = item; changed = true; }
                }
                else if (key == "layer" && int.TryParse(val, out int l) && l >= 0 && l < LayerYs.Length)
                {
                    LayerBox.SelectedIndex = l;
                    changed = true;
                }
                else if (key == "zoom" && double.TryParse(val, out double z))
                {
                    _zoom = Math.Max(1.0, Math.Min(8.0, z));
                    if (ZoomText != null) ZoomText.Text = $"{_zoom * 100:0}%";
                    changed = true;
                }
                else if (key == "finder" && int.TryParse(val, out int ft))
                {
                    _finderAutoType = ft;
                    changed = true;
                }
                else if (key == "popup")
                {
                    _popupFirstMark = true;
                    changed = true;
                }
                else if (key == "types")
                {
                    forceTypes = val;
                    changed = true;
                }
                else if (key == "bq" && BiomeSearch != null) { BiomeSearch.Text = val; changed = true; }
                else if (key == "sq" && StructSearch != null) { StructSearch.Text = val; changed = true; }
                else if (key == "openver")
                {
                    openVersionDrop = int.TryParse(val, out int ovd) && ovd > 0 ? ovd : 5600;
                    changed = true;
                }
            }
            if (changed)
            {
                UpdateControlAvailability();
                if (!string.IsNullOrEmpty(forceTypes)) ForceStructTypes(forceTypes);
                SeedGo_Click(null, null);   // 和「计算」按钮一样：玩家坐标同时是视角中心
                AutoRunFinder();
                if (openVersionDrop > 0)
                {
                    // ComboBox 的下拉是独立窗口，得等主窗口显示/最大化之后再打开（截图要用 -FullScreen 抓）
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(openVersionDrop) };
                    timer.Tick += (s, a) => { timer.Stop(); VersionBox.IsDropDownOpen = true; };
                    timer.Start();
                }
            }
        }

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // 构造期间 IsChecked 会先触发一次，那时 Host 还没建好
            if (WebPanel == null) return;

            bool web = sender == NavWeb;
            WebPanel.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
            SeedPanel.Visibility = web ? Visibility.Collapsed : Visibility.Visible;
            if (web) return;
            InitSeedIfNeeded();
        }

        /// <summary>一行的图标：抓到 favicon 就换成图片，抓不到就留着自绘的那个</summary>
        async void Icon_Loaded(object sender, RoutedEventArgs e)
        {
            var grid = sender as Grid;
            var tool = grid?.DataContext as WebTool;
            if (tool == null) return;
            var bmp = await RemoteImage.LoadAsync(tool.IconUrl, 64);
            // 容器可能已经被回收给了别的行，结果就不要了
            if (bmp == null || !ReferenceEquals(grid.DataContext, tool)) return;
            Image img = null;
            Path drawn = null;
            foreach (var c in grid.Children)
            {
                if (c is Image i) img = i;
                else if (c is Path p) drawn = p;
            }
            if (img == null) return;
            img.Source = bmp;
            if (drawn != null) drawn.Visibility = Visibility.Collapsed;
        }

        /// <summary>复制链接到剪贴板。不碰浏览器，用户自己粘过去最稳</summary>
        void CopyLink_Click(object sender, RoutedEventArgs e)
        {
            var tool = (sender as FrameworkElement)?.DataContext as WebTool;
            if (tool == null || string.IsNullOrEmpty(tool.Url)) return;
            try
            {
                Clipboard.SetText(tool.Url);
                Dialogs.Toast("已复制 " + tool.Host, MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { Log.Warn("复制链接失败：" + ex.Message); }
        }
    }
}
