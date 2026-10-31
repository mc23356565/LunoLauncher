using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LunoLauncher.Content;
using LunoLauncher.Core;
using LunoLauncher.UI;

namespace LunoLauncher.UI.Pages
{
    /// <summary>
    /// 工具箱 → 种子查询。
    ///
    /// 界面结构照 cubiomes-viewer：顶部工具条（种子 / 版本 / 维度 / 层）、
    /// 右侧可折叠的群系与结构勾选面板、底部状态栏（坐标 · 群系 · 缩放比例）。
    ///
    /// 位图底（生物群系 + 史莱姆区块 + 区块网格）+ 矢量覆盖层（结构标记、玩家点、结构小框）。
    /// 交互照 cubiomes-viewer：滚轮直接缩放、左键拖动平移、没拖动就是单击选点。
    ///
    /// 三个坐标：_viewX/_viewZ 视角中心；_dotX/_dotZ 玩家位置（精确到方块，不吸附区块）。
    ///
    /// 一致性：渲染时把每个采样点的群系 id 一起留下来（_grid），悬停和状态栏都只查这张表，
    /// 不再单独调原生库 —— 这样"显示的"和"查到的"永远是同一组 dim 和 y。
    /// </summary>
    public partial class ToolboxPage
    {
        /// <summary>下拉里的一项：版本条目，或者一个分组标题（标题项不可选）</summary>
        sealed class VersionItem
        {
            public Cubiomes.McVersion V { get; set; }
            // 注意：WPF 绑定只认属性，不认字段（写成字段会导致标题空白、DataTrigger 失效）
            public bool IsHeader { get; set; }
            public string HeaderText { get; set; }

            public string Name => IsHeader || V == null ? "" : V.Name;
            public string Range => IsHeader || V == null ? "" : V.Range;
            public int Id => IsHeader || V == null ? -1 : V.Id;
            /// <summary>区间和名字一样时（比如 26.3）就不重复显示</summary>
            public Visibility RangeVisibility =>
                IsHeader || V == null || string.IsNullOrEmpty(V.Range) || V.Range == V.Name ? Visibility.Collapsed : Visibility.Visible;
            public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ItemVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
            public override string ToString() => IsHeader ? HeaderText : (V == null ? "" : V.Display);
        }

        // 维度：下拉下标 → cubiomes 的 dim（主世界 0 / 下界 -1 / 末地 1）
        static readonly int[] DimIds = { 0, -1, 1 };
        static readonly string[] DimNames = { "主世界", "下界", "末地" };

        // 层：只有主世界 + 1.18 以上才有意义（3D 群系）
        static readonly int[] LayerYs = { 64, -32, -56 };
        static readonly string[] LayerNames = { "地表 y=64", "地下 y=-32", "深层 y=-56" };

        bool _seedReady;
        bool _renderPending;
        int _viewX, _viewZ;          // 可视区中心对应的方块坐标
        int _dotX, _dotZ;            // 玩家点
        double _zoom = 1.0;          // 1 方块 = zoom 个 DIP

        StructMark _infoMark;

        /// <summary>小框现在显示的是"玩家点"还是"结构"（决定 /tp 指令和标题）</summary>
        bool _infoIsDot;

        /// <summary>小框是"点开"的还是"悬停出来"的：点开的常驻，悬停的鼠标移开就收</summary>
        bool _infoPinned;

        /// <summary>调试 / 截图用：draw 完把第一个结构的小框打开（命令行 popup=1）</summary>
        bool _popupFirstMark;

        readonly Ellipse _dot = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x35, 0x35)),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            IsHitTestVisible = true,          // 点红点要能看坐标 / 复制 TP
            Cursor = Cursors.Hand,
            ToolTip = "玩家位置（点一下看坐标 / 复制 TP）",
            RenderTransformOrigin = new Point(0.5, 0.5),
            Visibility = Visibility.Collapsed,
        };

        // ---------- 当前选择 ----------
        // 版本名单来自 DLL（Cubiomes.Versions），这里只做"当前选中项"的读取
        VersionItem CurVersionItem => VersionBox == null ? null : VersionBox.SelectedItem as VersionItem;
        int CurMc => CurVersionItem != null ? CurVersionItem.Id : Cubiomes.NewestVersion;
        int CurDim => DimBox == null || DimBox.SelectedIndex < 0 ? 0 : DimIds[DimBox.SelectedIndex];
        int CurLayerY => LayerBox == null || LayerBox.SelectedIndex < 0 ? LayerYs[0] : LayerYs[LayerBox.SelectedIndex];
        /// <summary>1.18 以前是 2D 群系（y 无关），1.18 起才是 3D</summary>
        bool Cur3D => Cubiomes.Has3D(CurMc);
        /// <summary>查群系用的 y：旧版本和别的维度都按固定值查，免得"选了层却看不到变化"</summary>
        int CurQueryY => CurDim != 0 ? 64 : (Cur3D ? CurLayerY : 0);

        void InitSeedIfNeeded()
        {
            if (_seedReady) return;
            // 注意：这期间填下拉框会触发 SelectionChanged，那些处理函数会因为 _seedReady 还是 false 直接返回，
            // 不会重复建列表 / 重复渲染。最后再把开关打开，跑第一次渲染。
            // 版本下拉：名单来自 DLL（35 个，Beta 1.7 → 26.3），按分组显示覆盖区间
            BuildVersionList();
            DimBox.ItemsSource = DimNames;
            DimBox.SelectedIndex = 0;
            LayerBox.ItemsSource = LayerNames;
            LayerBox.SelectedIndex = 0;
            if (string.IsNullOrWhiteSpace(SeedBox.Text)) SeedBox.Text = "0";
            if (string.IsNullOrWhiteSpace(PlayerXBox.Text)) PlayerXBox.Text = "0";
            if (string.IsNullOrWhiteSpace(PlayerZBox.Text)) PlayerZBox.Text = "0";
            MapOverlay.Children.Add(_dot);
            _dot.MouseLeftButtonDown += Dot_Click;
            WirePanelDragging();
            BuildBiomeList();
            BuildStructList();
            UpdateControlAvailability();
            _dotX = ParseInt(PlayerXBox.Text, 0);
            _dotZ = ParseInt(PlayerZBox.Text, 0);
            _viewX = _dotX;
            _viewZ = _dotZ;
            _seedReady = true;
            ZoomText.Text = $"{_zoom * 100:0}%";
            UpdateCoordText();
            RenderSeed();
        }

        /// <summary>按 DLL 给的名单填版本下拉（分组标题 + 覆盖区间），默认选最新</summary>
        void BuildVersionList()
        {
            var items = new List<VersionItem>();
            int lastGroup = int.MinValue;
            foreach (var v in Cubiomes.Versions)
            {
                if (v.Group != lastGroup)
                {
                    items.Add(new VersionItem { IsHeader = true, HeaderText = v.GroupName });
                    lastGroup = v.Group;
                }
                items.Add(new VersionItem { V = v });
            }

            VersionBox.ItemsSource = items;
            if (items.Count > 0) VersionBox.SelectedItem = items[items.Count - 1];   // 默认最新
            else if (SeedHint != null) SeedHint.Text = "读取不到 cubiomes 版本列表（cubiomes.dll 里没有 luno_mc_* 导出？）";
            int hdr = 0;
            foreach (var it in items) if (it.IsHeader) hdr++;
            Log.Info($"[ver] 版本列表 {items.Count - hdr} 个 + 分组标题 {hdr} 个");
        }

        /// <summary>版本编号 → 显示名（"1.16.5"），找不到就显示数字</summary>
        static string VersionName(int mcVer)
        {
            foreach (var v in Cubiomes.Versions) if (v.Id == mcVer) return v.Name;
            return mcVer.ToString();
        }

        /// <summary>版本编号 → 覆盖区间（"1.16.2-1.16.5"）</summary>
        static string VersionRange(int mcVer)
        {
            foreach (var v in Cubiomes.Versions) if (v.Id == mcVer) return v.Range;
            return "";
        }

        /// <summary>按名字 / 区间 / 编号找一个版本项（命令行 ver=1.16.5 用）</summary>
        VersionItem FindVersionItem(string text)
        {
            if (VersionBox == null || string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            VersionItem byName = null, byRange = null, byId = null;
            foreach (var o in VersionBox.Items)
            {
                var it = o as VersionItem;
                if (it == null || it.IsHeader) continue;
                if (byName == null && string.Equals(it.Name, text, StringComparison.OrdinalIgnoreCase)) byName = it;
                else if (byRange == null && string.Equals(it.Range, text, StringComparison.OrdinalIgnoreCase)) byRange = it;
                else if (byId == null && it.Id.ToString() == text) byId = it;
            }
            return byName ?? byRange ?? byId;
        }

        static string DimName(int dim) => dim == 0 ? "主世界" : dim == -1 ? "下界" : "末地";

        static string LayerName(int y)
        {
            for (int i = 0; i < LayerYs.Length; i++) if (LayerYs[i] == y) return LayerNames[i];
            return "y=" + y;
        }

        // ---------- 下拉框 / 开关 ----------
        void Version_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_seedReady) return;
            // 群系名和"哪些结构在这个版本有"都跟版本有关，换版本要重建列表
            BuildBiomeList(true);
            BuildStructList(true);
            UpdateControlAvailability();
            if (FinderPanel != null && FinderPanel.Visibility == Visibility.Visible) RebuildFinderTypes();
            RenderSeed();
        }

        void Dim_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_seedReady) return;
            UpdateControlAvailability();
            CloseStructInfo();
            if (FinderPanel != null && FinderPanel.Visibility == Visibility.Visible) RebuildFinderTypes();
            RenderSeed();
        }

        void Layer_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_seedReady) return;
            RenderSeed();
        }

        void RenderToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_seedReady) return;
            RenderSeed();
        }

        /// <summary>层的可见性、史莱姆开关的可用性（史莱姆只存在于主世界）</summary>
        void UpdateControlAvailability()
        {
            int dim = CurDim;
            bool over = dim == 0;
            bool layered = over && Cur3D;

            // 1.18 以前没有 3D 群系（没有洞穴三层），整组隐藏而不是留在那里点不动
            if (LayerGroup != null) LayerGroup.Visibility = layered ? Visibility.Visible : Visibility.Collapsed;
            LayerBox.IsEnabled = layered;
            LayerBox.ToolTip = layered
                ? "1.18 起群系是 3D 的：繁茂洞穴 / 滴水石洞 / 深暗之域只在这些层出现"
                : over ? "1.18 以前群系是 2D 的，没有分层" : "只有主世界才分层";

            SlimeToggle.IsEnabled = over;
            SlimeToggle.Content = over ? "史莱姆区块" : "史莱姆区块（仅主世界）";
            SlimeToggle.ToolTip = over
                ? "史莱姆区块只由种子和区块坐标决定，与群系无关"
                : "史莱姆只存在于主世界";
        }

        /// <summary>「计算」：用输入框坐标当玩家位置，并把视角跳过去</summary>
        void SeedGo_Click(object sender, RoutedEventArgs e)
        {
            _dotX = ParseInt(PlayerXBox.Text, _dotX);
            _dotZ = ParseInt(PlayerZBox.Text, _dotZ);
            _viewX = _dotX;
            _viewZ = _dotZ;
            UpdateCoordText();
            RenderSeed();
        }

        /// <summary>「移动到中心」：视角跳到玩家位置</summary>
        void CenterOnDot_Click(object sender, RoutedEventArgs e)
        {
            _viewX = _dotX;
            _viewZ = _dotZ;
            RenderSeed();
        }

        // ---------- 可折叠面板 ----------
        void ToggleBiome_Click(object sender, RoutedEventArgs e)
        {
            bool show = BiomeBody.Visibility != Visibility.Visible;
            BiomeBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            BiomeArrow.Text = show ? "\uE70D" : "\uE76C";
        }

        void ToggleStruct_Click(object sender, RoutedEventArgs e)
        {
            bool show = StructBody.Visibility != Visibility.Visible;
            StructBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            StructArrow.Text = show ? "\uE70D" : "\uE76C";
        }

        // ---------- 群系勾选列表（不勾选的不渲染） ----------
        /// <summary>
        /// 1.21 全套群系 id（含 1.18 之前的旧 id：id 没变，只是名字变了）。
        /// 每一行还要过 <see cref="BiomeInVersion"/>，老版本里没有的群系不显示在列表上。
        /// </summary>
        static readonly int[] BiomeIds =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
            21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39,
            40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 127,
            129, 130, 131, 132, 133, 134, 140, 149, 151, 155, 156, 157, 158,
            160, 161, 162, 163, 164, 165, 166, 167, 168, 169,
            170, 171, 172, 173, 174, 175, 177, 178, 179, 180, 181, 182,
            183, 184, 185, 186, 187, 188,
        };

        /// <summary>这个 id 在这个版本里存不存在（cubiomes 没导出 biomeExists，按版本门槛判断）</summary>
        static bool BiomeInVersion(int id, int mc)
        {
            if (id >= 188) return mc >= 35;             // 26.3  斑驳森林
            if (id == 187) return mc >= 34;             // 26.2  硫磺洞穴
            if (id == 186) return mc >= 28;             // 1.21.4 苍白花园
            if (id == 185) return mc >= 25;             // 1.20  樱花树林
            if (id == 183 || id == 184) return mc >= 23; // 1.19  深暗之域 / 红树林沼泽
            if (id == 174 || id == 175) return mc >= 21; // 1.17  繁茂洞穴 / 滴水石洞
            if (id >= 170 && id <= 173) return mc >= 20; // 1.16  下界四个群系
            if (id >= 177 && id <= 182) return mc >= 22; // 1.18  山地群系
            return true;
        }

        readonly HashSet<int> _visible = new HashSet<int>();

        /// <summary>清单行的标签：编号 + 可搜索文本（搜索框按这个过滤，不重建控件）</summary>
        sealed class RowTag
        {
            public int Id;
            public string Text;
        }

        /// <param name="keepChecks">true 时保留当前的勾选（换版本重建列表用）</param>
        void BuildBiomeList(bool keepChecks = false)
        {
            if (BiomeList == null) return;
            HashSet<int> keep = keepChecks && _visible.Count > 0 ? new HashSet<int>(_visible) : null;
            int mc = CurMc;
            BiomeList.Children.Clear();
            _visible.Clear();
            foreach (int id in BiomeIds)
            {
                if (!BiomeInVersion(id, mc)) continue;
                string en = Cubiomes.Available ? Cubiomes.BiomeName(mc, id) : "";
                if (string.IsNullOrEmpty(en)) continue;      // 这个版本没有这个 id
                string zh = BiomeNames.Zh(en);
                if (string.IsNullOrEmpty(zh)) continue;
                // 先定好勾选状态再接事件，免得每加一行就重绘一次地图
                bool on = keep == null || keep.Contains(id);
                var cb = new CheckBox
                {
                    Content = MakeRowContent(BiomeColors.Get(id), zh),
                    Tag = new RowTag { Id = id, Text = zh + " " + en },
                    IsChecked = on,
                    Focusable = false,          // 勾选面板不吃键盘焦点，免得切窗口时列表自己滚动
                    Margin = new Thickness(0, 2, 0, 2),
                    FontSize = 12.5,
                };
                cb.Checked += BiomeCheck_Changed;
                cb.Unchecked += BiomeCheck_Changed;
                if (on) _visible.Add(id);
                BiomeList.Children.Add(cb);
            }
            ApplyBiomeFilter();
        }

        /// <summary>一行清单内容：颜色小方块（有图标就显示图标）+ 文字</summary>
        static StackPanel MakeRowContent(Color color, string text, ImageSource icon = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != null)
            {
                row.Children.Add(new Image
                {
                    Source = icon,
                    Width = 15,
                    Height = 15,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else
            {
                row.Children.Add(new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                    BorderThickness = new Thickness(0.5),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        void UpdateBiomeHint(int? shown = null)
        {
            if (BiomeHint == null || BiomeList == null) return;
            int total = 0;
            foreach (var c in BiomeList.Children) if (c is CheckBox) total++;
            string head = shown.HasValue && shown.Value != total ? $"匹配 {shown.Value} / {total} 个；" : "";
            BiomeHint.Text = _visible.Count == 0
                ? $"{head}一个群系都没勾（共 {total} 个）：图上只剩网格和结构"
                : $"{head}已勾选 {_visible.Count} / {total} 个；不勾选的不渲染";
        }

        void BiomeCheck_Changed(object sender, RoutedEventArgs e)
        {
            var cb = sender as CheckBox;
            var tag = cb?.Tag as RowTag;
            if (tag == null) return;
            if (cb.IsChecked == true) _visible.Add(tag.Id); else _visible.Remove(tag.Id);
            UpdateBiomeHint();
            RenderSeed();
        }

        void BiomeAll_Click(object sender, RoutedEventArgs e) => SetAllBiomes(true);
        void BiomeNone_Click(object sender, RoutedEventArgs e) => SetAllBiomes(false);

        void SetAllBiomes(bool on)
        {
            foreach (var c in BiomeList.Children)
            {
                var cb = c as CheckBox;
                var tag = cb?.Tag as RowTag;
                if (tag == null) continue;
                cb.IsChecked = on;
                if (on) _visible.Add(tag.Id); else _visible.Remove(tag.Id);
            }
            UpdateBiomeHint();
            RenderSeed();
        }

        // ---------- 结构表 ----------
        //
        // 类型编号必须和 cubiomes 的 enum StructureType 完全一致，不能改顺序、不能插队。
        static readonly string[] StructNames =
        {
            "旧版特征点",   // 0  Feature
            "沙漠神殿",     // 1  Desert_Pyramid
            "丛林神庙",     // 2  Jungle_Temple
            "沼泽小屋",     // 3  Swamp_Hut
            "雪屋",         // 4  Igloo
            "村庄",         // 5  Village
            "海底废墟",     // 6  Ocean_Ruin
            "沉船",         // 7  Shipwreck
            "海底神殿",     // 8  Monument
            "林地府邸",     // 9  Mansion
            "掠夺者前哨",   // 10 Outpost
            "废弃传送门",   // 11 Ruined_Portal
            "下界传送门",   // 12 Ruined_Portal_N
            "远古城市",     // 13 Ancient_City
            "埋藏的宝藏",   // 14 Treasure
            "废弃矿井",     // 15 Mineshaft
            "沙漠水井",     // 16 Desert_Well
            "紫水晶洞",     // 17 Geode
            "下界要塞",     // 18 Fortress
            "堡垒遗迹",     // 19 Bastion
            "下界化石",     // 20 Nether_Fossil
            "末地城",       // 21 End_City
            "末地折跃门",   // 22 End_Gateway
            "末地小岛",     // 23 End_Island
            "古迹废墟",     // 24 Trail_Ruins
            "试炼密室",     // 25 Trial_Chambers
            "废弃营地",     // 26 Abandoned_Camp
            "要塞",         // 27 Stronghold
        };

        /// <summary>英文 id（cubiomes 的 struct2str 名字），搜索框要用</summary>
        static readonly string[] StructIds =
        {
            "feature", "desert_pyramid", "jungle_pyramid", "swamp_hut", "igloo", "village", "ocean_ruin",
            "shipwreck", "monument", "mansion", "pillager_outpost", "ruined_portal", "ruined_portal_nether",
            "ancient_city", "buried_treasure", "mineshaft", "desert_well", "amethyst_geode", "fortress",
            "bastion_remnant", "nether_fossil", "end_city", "end_gateway", "end_island", "trail_ruins",
            "trial_chambers", "abandoned_camp", "stronghold",
        };

        /// <summary>结构编号：要塞（27）。它没有 region 网格，走 StrongholdIter</summary>
        const int StructTypeStronghold = 27;
        /// <summary>旧版特征点（0）：1.12 以前才有，没有生物群系过滤（尝试点就是位置）</summary>
        const int StructTypeFeature = 0;

        /// <summary>标记上的一个字，太小放不下的就用符号</summary>
        static readonly string[] StructBadges =
        {
            "特", "沙", "庙", "巫", "雪", "村", "墟", "船", "殿", "邸",
            "哨", "门", "狱", "古", "宝", "井", "泉", "晶", "堡", "垒",
            "骨", "末", "跃", "岛", "迹", "炼", "营", "塞",
        };

        static readonly int[] StructDims =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0,
            -1, -1, -1, 1, 1, 1, 0, 0, 0, 0,
        };

        static readonly Color[] StructColors =
        {
            Color.FromRgb(0x78, 0x86, 0x94), // 0
            Color.FromRgb(0xD8, 0xA8, 0x4A), // 1 沙漠神殿
            Color.FromRgb(0x3E, 0x8E, 0x4E), // 2 丛林神庙
            Color.FromRgb(0x53, 0x6B, 0x3F), // 3 沼泽小屋
            Color.FromRgb(0x7F, 0xB6, 0xD8), // 4 雪屋
            Color.FromRgb(0xC7, 0x9A, 0x3C), // 5 村庄
            Color.FromRgb(0x2A, 0x9D, 0x8F), // 6 海底废墟
            Color.FromRgb(0x9C, 0x6B, 0x3F), // 7 沉船
            Color.FromRgb(0x2A, 0x7F, 0x9E), // 8 海底神殿
            Color.FromRgb(0x8E, 0x44, 0xAD), // 9 林地府邸
            Color.FromRgb(0xB0, 0x3A, 0x2E), // 10 前哨
            Color.FromRgb(0x8D, 0x6E, 0xE8), // 11 废弃传送门
            Color.FromRgb(0xA6, 0x3B, 0xD8), // 12 下界传送门
            Color.FromRgb(0x34, 0x49, 0x5E), // 13 远古城市
            Color.FromRgb(0xD4, 0xA0, 0x17), // 14 宝藏
            Color.FromRgb(0x7F, 0x8C, 0x8D), // 15 废弃矿井
            Color.FromRgb(0xC2, 0xB2, 0x80), // 16 沙漠水井
            Color.FromRgb(0xA5, 0x69, 0xBD), // 17 紫水晶洞
            Color.FromRgb(0xC0, 0x39, 0x2B), // 18 下界要塞
            Color.FromRgb(0x2C, 0x3E, 0x50), // 19 堡垒遗迹
            Color.FromRgb(0x9E, 0x9E, 0x9E), // 20 下界化石
            Color.FromRgb(0x7D, 0x3C, 0x98), // 21 末地城
            Color.FromRgb(0x5B, 0x2C, 0x6F), // 22 末地折跃门
            Color.FromRgb(0xA9, 0xA9, 0xA9), // 23 末地小岛
            Color.FromRgb(0xA0, 0x52, 0x2D), // 24 古迹废墟
            Color.FromRgb(0x16, 0xA0, 0x85), // 25 试炼密室
            Color.FromRgb(0x6E, 0x7B, 0x8B), // 26 废弃营地
            Color.FromRgb(0x1F, 0x61, 0x8D), // 27 要塞
        };

        /// <summary>默认勾上的几个：村庄、海底神殿、林地府邸、下界要塞、堡垒遗迹、末地城</summary>
        static readonly HashSet<int> StructDefaultOn = new HashSet<int> { 5, 8, 9, 18, 19, 21 };

        const int MaxMarksPerType = 260;   // 紫水晶洞 / 宝藏这种一格一个的太密了，画不下就截断

        readonly HashSet<int> _showStruct = new HashSet<int>();

        void BuildStructList(bool keepChecks = false)
        {
            if (StructList == null) return;
            HashSet<int> keep = keepChecks && _showStruct.Count > 0 ? new HashSet<int>(_showStruct) : null;
            int mc = CurMc;
            StructList.Children.Clear();
            _showStruct.Clear();
            for (int t = 0; t < StructNames.Length; t++)
            {
                int rs = Cubiomes.StructureRegionSize(t, mc);
                // 要塞没有 region 配置，但 DLL 里有 StrongholdIter，所以它也算"可用"
                bool stronghold = t == StructTypeStronghold;
                bool ok = stronghold ? (Cubiomes.Available && StructDims[t] == 0) : rs > 0;
                bool on = ok && (keep == null ? StructDefaultOn.Contains(t) : keep.Contains(t));
                Color sc = StructColors[t];
                string tip = stronghold
                    ? "只有主世界有 · 要塞不在区块网格上，靠要塞迭代器找（勾上后会画出来）"
                    : ok
                        ? $"{DimName(StructDims[t])} · 每 {rs} 个区块一个区域（1 区域 ≈ {rs}×{rs} 区块）"
                        : "这个版本没有它（换版本后再看）";
                var cb = new CheckBox
                {
                    Content = MakeRowContent(sc, StructNames[t], StructIcons.Get(t)),
                    Tag = new RowTag { Id = t, Text = StructNames[t] + " " + StructIds[t] },
                    IsChecked = on,
                    IsEnabled = ok,
                    Focusable = false,
                    Margin = new Thickness(0, 2, 0, 2),
                    FontSize = 12.5,
                    ToolTip = tip,
                };
                cb.Checked += StructCheck_Changed;
                cb.Unchecked += StructCheck_Changed;
                if (on) _showStruct.Add(t);
                StructList.Children.Add(cb);
            }
            ApplyStructFilter();
        }

        void StructCheck_Changed(object sender, RoutedEventArgs e)
        {
            var cb = sender as CheckBox;
            var tag = cb?.Tag as RowTag;
            if (tag == null) return;
            if (cb.IsChecked == true) _showStruct.Add(tag.Id); else _showStruct.Remove(tag.Id);
            RenderSeed();
        }

        void StructAll_Click(object sender, RoutedEventArgs e) => SetAllStructs(true);
        void StructNone_Click(object sender, RoutedEventArgs e) => SetAllStructs(false);

        void SetAllStructs(bool on)
        {
            foreach (var c in StructList.Children)
            {
                var cb = c as CheckBox;
                var tag = cb?.Tag as RowTag;
                if (tag == null || !cb.IsEnabled) continue;
                cb.IsChecked = on;
                if (on) _showStruct.Add(tag.Id); else _showStruct.Remove(tag.Id);
            }
            RenderSeed();
        }

        /// <summary>命令行 types=20,23：把指定的结构类型勾上（截图 / 调试用）</summary>
        void ForceStructTypes(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv) || StructList == null) return;
            foreach (var part in csv.Split(','))
            {
                if (!int.TryParse(part.Trim(), out int t)) continue;
                if (t < 0 || t >= StructNames.Length) continue;
                _showStruct.Add(t);
                foreach (var c in StructList.Children)
                {
                    var cb = c as CheckBox;
                    var tag = cb?.Tag as RowTag;
                    if (tag == null || tag.Id != t) continue;
                    if (cb.IsEnabled) cb.IsChecked = true;
                }
            }
        }

        // 鼠标交互 / 悬停查询 / 渲染都在 ToolboxPage.Seed.Canvas.cs

        void CopyTp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText($"/tp @s {_dotX} 100 {_dotZ}");
                Dialogs.Toast($"已复制 /tp @s {_dotX} 100 {_dotZ}", MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { Log.Warn("复制 TP 指令失败：" + ex.Message); }
        }

        // 缩放 / 平移 / 渲染见 ToolboxPage.Seed.Canvas.cs

        // ---------- 渲染 ----------
        static int ParseInt(string s, int def)
            => int.TryParse((s ?? "").Trim(), out var v) ? v : def;

        /// <summary>一个结构标记（方块坐标）</summary>
        sealed class StructMark
        {
            public int Type, X, Z;
        }

        // RenderSeed / BuildMap 在 ToolboxPage.Seed.Canvas.cs（画布自适应版本）

        /// <summary>遍历可见范围内的结构区域。调用方必须已经 setup + applySeed，并且持有 Cubiomes.Sync</summary>
        List<StructMark> ScanStructures(int mcVer, int dim, double leftX, double leftZ, double wBlocks, double hBlocks, HashSet<int> types)
        {
            var marks = new List<StructMark>();
            if (types == null || types.Count == 0 || !Cubiomes.Available) return marks;
            int bx0 = (int)Math.Floor(leftX), bz0 = (int)Math.Floor(leftZ);
            int bx1 = (int)Math.Ceiling(leftX + wBlocks), bz1 = (int)Math.Ceiling(leftZ + hBlocks);
            foreach (int t in types)
            {
                if (t < 0 || t >= StructDims.Length) continue;
                if (StructDims[t] != dim) continue;          // 只画当前维度里有的结构
                if (t == StructTypeStronghold)
                {
                    CollectStrongholds(mcVer, bx0, bz0, bx1, bz1, marks);
                    continue;
                }
                int rs = Cubiomes.StructureRegionSize(t, mcVer);
                if (rs <= 0) continue;                       // 这个版本没有它 / 不能按区域遍历
                int regionBlocks = rs * 16;
                int rx0 = FloorDiv(bx0, regionBlocks), rx1 = FloorDiv(bx1, regionBlocks);
                int rz0 = FloorDiv(bz0, regionBlocks), rz1 = FloorDiv(bz1, regionBlocks);
                int found = 0;
                for (int rz = rz0; rz <= rz1 && found < MaxMarksPerType; rz++)
                {
                    for (int rx = rx0; rx <= rx1 && found < MaxMarksPerType; rx++)
                    {
                        if (!Cubiomes.StructurePos(t, mcVer, rx, rz, out int x, out int z)) continue;
                        if (x < bx0 || x > bx1 || z < bz0 || z > bz1) continue;
                        // 旧版特征点没有生物群系过滤（尝试点本身就是位置），别用 viable 判死
                        if (t != StructTypeFeature && !Cubiomes.IsViableStructure(t, x, z)) continue;
                        marks.Add(new StructMark { Type = t, X = x, Z = z });
                        found++;
                    }
                }
            }
            return marks;
        }

        /// <summary>要塞：迭代全部要塞，挑出落在这个矩形里的（要塞只有主世界有）</summary>
        void CollectStrongholds(int mcVer, int bx0, int bz0, int bx1, int bz1, List<StructMark> marks)
        {
            const int MaxStrongholds = 128;   // 现代版本一共 128 个，够用了
            var buf = new int[MaxStrongholds * 2];
            int n = Cubiomes.Strongholds(mcVer, MaxStrongholds, buf);
            for (int i = 0; i < n; i++)
            {
                int x = buf[i * 2], z = buf[i * 2 + 1];
                if (x < bx0 || x > bx1 || z < bz0 || z > bz1) continue;
                marks.Add(new StructMark { Type = StructTypeStronghold, X = x, Z = z });
            }
        }

        // 结构标记绘制 / 弹框在 ToolboxPage.Seed.Canvas.cs

        void CloseStructInfo_Click(object sender, RoutedEventArgs e) => CloseStructInfo();

        void CopyStructTp_Click(object sender, RoutedEventArgs e)
        {
            if (_infoIsDot)
            {
                try
                {
                    Clipboard.SetText($"/tp @s {_dotX} 100 {_dotZ}");
                    Dialogs.Toast($"已复制 /tp @s {_dotX} 100 {_dotZ}", MainWindow.ToastKind.Ok);
                }
                catch (Exception ex) { Log.Warn("复制 TP 指令失败：" + ex.Message); }
                return;
            }
            var m = _infoMark;
            if (m == null) return;
            try
            {
                Clipboard.SetText($"/tp @s {m.X} ~ {m.Z}");
                Dialogs.Toast($"已复制 /tp @s {m.X} ~ {m.Z}", MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { Log.Warn("复制结构 TP 指令失败：" + ex.Message); }
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

        // 群系配色在 Core/BiomeColors.cs，位图绘制在 ToolboxPage.Seed.Canvas.cs
    }
}
