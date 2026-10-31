using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LunoLauncher.Content;
using LunoLauncher.Core;

namespace LunoLauncher.UI.Pages
{
    /// <summary>
    /// R2：结构查找 + R4：两个清单的搜索框。
    ///
    /// 为什么不是「一次渲染到很远」：2500 万区块那种范围既算不动也画不下
    /// （单点 getBiomeAt 大约几微秒，一个 5000×5000 区块的地图就是两亿次采样）。
    /// 找远处稀有结构的正解是它 + R1：
    ///   R1 让地图能无限拖到任意远处（不再有固定画布边界）；
    ///   这里按结构的 regionSize 批量扫候选点，只把「真的会生成」的那些列出来，双击定位。
    /// </summary>
    public partial class ToolboxPage
    {
        static readonly int[] FinderRadii = { 512, 1024, 2048, 4096, 8192 };
        static readonly string[] FinderRadiusNames = { "512 方块", "1024 方块", "2048 方块", "4096 方块", "8192 方块" };

        const long FinderMaxRegions = 60000;     // 一次扫描的区域上限，超过就自动收窄
        const int FinderMaxResults = 4000;

        bool _finderReady;
        readonly List<int> _finderTypes = new List<int>();
        int _finderAutoType = -1;

        /// <summary>命令行 finder=5：打开面板并自动查一次（截图 / 快捷方式用）</summary>
        void AutoRunFinder()
        {
            if (_finderAutoType < 0) return;
            int type = _finderAutoType;
            _finderAutoType = -1;
            EnsureFinderReady();
            FinderPanel.Visibility = Visibility.Visible;
            int idx = _finderTypes.IndexOf(type);
            if (idx >= 0) FinderTypeBox.SelectedIndex = idx;
            FinderGo_Click(null, null);
        }

        /// <summary>一条查找结果（绑定到列表面板）</summary>
        sealed class FindResult
        {
            public int Type, X, Z;
            public string Title { get; set; }
            public string CoordText { get; set; }
            public Brush Color { get; set; }
        }

        void FinderToggle_Click(object sender, RoutedEventArgs e)
        {
            if (FinderPanel == null) return;
            bool show = FinderPanel.Visibility != Visibility.Visible;
            FinderPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) EnsureFinderReady();
        }

        void EnsureFinderReady()
        {
            if (FinderRadiusBox != null && FinderRadiusBox.ItemsSource == null)
            {
                FinderRadiusBox.ItemsSource = FinderRadiusNames;
                FinderRadiusBox.SelectedIndex = 2;
            }
            // 维度 / 版本变了会被 Dim_Changed、Version_Changed 单独刷新，这里只管第一次
            if (!_finderReady) RebuildFinderTypes();
            _finderReady = true;
        }

        /// <summary>结构下拉只列：当前维度里有的 + 这个版本能算出来的（要塞走迭代器，也算能算）</summary>
        void RebuildFinderTypes(int keepType = -1)
        {
            if (FinderTypeBox == null) return;
            int mc = CurMc, dim = CurDim;
            _finderTypes.Clear();
            var names = new List<string>();
            for (int t = 0; t < StructNames.Length; t++)
            {
                if (StructDims[t] != dim) continue;
                bool stronghold = t == StructTypeStronghold;
                if (!stronghold && Cubiomes.StructureRegionSize(t, mc) <= 0) continue;
                if (stronghold && !Cubiomes.Available) continue;
                _finderTypes.Add(t);
                names.Add(StructNames[t]);
            }
            FinderTypeBox.ItemsSource = names;
            if (names.Count == 0) { FinderTypeBox.SelectedIndex = -1; return; }
            int sel = keepType >= 0 ? _finderTypes.IndexOf(keepType) : 0;
            FinderTypeBox.SelectedIndex = sel >= 0 && sel < names.Count ? sel : 0;
        }

        async void FinderGo_Click(object sender, RoutedEventArgs e)
        {
            EnsureFinderReady();
            int idx = FinderTypeBox == null ? -1 : FinderTypeBox.SelectedIndex;
            if (idx < 0 || idx >= _finderTypes.Count)
            {
                FinderStatus.Text = "当前维度没有可查找的结构。";
                return;
            }
            int type = _finderTypes[idx];
            int radius = FinderRadii[Math.Max(0, Math.Min(FinderRadii.Length - 1, FinderRadiusBox.SelectedIndex))];
            long seed;
            if (!SeedTools.TryParseSeed(SeedBox.Text, out seed))
            {
                FinderStatus.Text = "先在上面填种子。";
                return;
            }
            int mc = CurMc, dim = CurDim, cx = _viewX, cz = _viewZ;
            FinderStatus.Text = $"正在扫描 {StructNames[type]}（半径 {radius} 方块）…";
            FinderList.ItemsSource = null;
            List<FindResult> res;
            try
            {
                res = await Task.Run(() => ScanRadius(seed, mc, dim, type, radius, cx, cz));
            }
            catch (Exception ex)
            {
                FinderStatus.Text = "扫描失败：" + ex.Message;
                return;
            }
            FinderList.ItemsSource = res;
            FinderStatus.Text = res.Count == 0
                ? $"{StructNames[type]}：半径 {radius} 方块内没找到（换个大点的半径试试）"
                : $"{StructNames[type]}：半径 {radius} 方块内 {res.Count} 个，按距离排序，双击定位";
        }

        /// <summary>在半径内按区域遍历候选点并过滤。跑在后台线程，全程持有原生库锁</summary>
        List<FindResult> ScanRadius(long seed, int mc, int dim, int type, int radius, int cx, int cz)
        {
            var res = new List<FindResult>();
            if (!Cubiomes.Available) return res;
            int rs = Cubiomes.StructureRegionSize(type, mc);
            if (type != StructTypeStronghold && rs <= 0) return res;

            lock (Cubiomes.Sync)
            {
                if (!Cubiomes.Setup(mc, false)) return res;
                if (!Cubiomes.ApplySeed(seed, dim)) return res;

                if (type == StructTypeStronghold)
                {
                    // 要塞：迭代全部要塞，只留半径内的
                    var buf = new int[128 * 2];
                    int sn = Cubiomes.Strongholds(mc, 128, buf);
                    for (int i = 0; i < sn && res.Count < FinderMaxResults; i++)
                    {
                        int sx = buf[i * 2], sz = buf[i * 2 + 1];
                        long ddx = sx - cx, ddz = sz - cz;
                        if (ddx * ddx + ddz * ddz > (long)radius * radius) continue;
                        res.Add(MakeResult(type, sx, sz));
                    }
                }
                else
                {
                    int rb = rs * 16;
                    int rx0 = FloorDiv(cx - radius, rb), rx1 = FloorDiv(cx + radius, rb);
                    int rz0 = FloorDiv(cz - radius, rb), rz1 = FloorDiv(cz + radius, rb);
                    long regions = (long)(rx1 - rx0 + 1) * (rz1 - rz0 + 1);
                    // 小结构（regionSize=1）在 8192 半径下会有几十万区域，收窄到上限以内
                    if (regions > FinderMaxRegions)
                    {
                        int span = (int)Math.Sqrt(FinderMaxRegions);
                        rx1 = Math.Min(rx1, rx0 + span);
                        rz1 = Math.Min(rz1, rz0 + span);
                    }

                    for (int rz = rz0; rz <= rz1; rz++)
                    {
                        for (int rx = rx0; rx <= rx1; rx++)
                        {
                            if (!Cubiomes.StructurePos(type, mc, rx, rz, out int x, out int z)) continue;
                            long dx = x - cx, dz = z - cz;
                            if (dx * dx + dz * dz > (long)radius * radius) continue;
                            if (type != StructTypeFeature && !Cubiomes.IsViableStructure(type, x, z)) continue;
                            res.Add(MakeResult(type, x, z));
                            if (res.Count >= FinderMaxResults) break;
                        }
                        if (res.Count >= FinderMaxResults) break;
                    }
                }
            }

            res.Sort((a, b) =>
            {
                long da = ((long)a.X - cx) * (a.X - cx) + ((long)a.Z - cz) * (a.Z - cz);
                long db = ((long)b.X - cx) * (b.X - cx) + ((long)b.Z - cz) * (b.Z - cz);
                return da.CompareTo(db);
            });
            return res;
        }

        /// <summary>一条查找结果（画刷要 Freeze 之后才能给界面绑定，因为是在后台线程建的）</summary>
        static FindResult MakeResult(int type, int x, int z)
        {
            var brush = new SolidColorBrush(type >= 0 && type < StructColors.Length ? StructColors[type] : Colors.Gray);
            brush.Freeze();
            return new FindResult
            {
                Type = type,
                X = x,
                Z = z,
                Title = StructNames[type],
                CoordText = $"{x}, {z}",
                Color = brush,
            };
        }

        /// <summary>双击结果：视角与玩家点都跳过去</summary>
        void FinderList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var r = FinderList == null ? null : FinderList.SelectedItem as FindResult;
            if (r == null) return;
            _dotX = r.X;
            _dotZ = r.Z;
            PlayerXBox.Text = r.X.ToString();
            PlayerZBox.Text = r.Z.ToString();
            _viewX = r.X;
            _viewZ = r.Z;
            if (FinderPanel != null) FinderPanel.Visibility = Visibility.Collapsed;
            RenderSeed();
        }

        // ---------- R4：搜索框（只改 Visibility，不重建控件） ----------
        void BiomeSearch_Changed(object sender, TextChangedEventArgs e) => ApplyBiomeFilter();

        void StructSearch_Changed(object sender, TextChangedEventArgs e) => ApplyStructFilter();

        void ApplyBiomeFilter()
        {
            if (BiomeSearch == null || BiomeList == null) return;
            string q = (BiomeSearch.Text ?? "").Trim().ToLowerInvariant();
            int shown = 0;
            foreach (var c in BiomeList.Children)
            {
                var cb = c as CheckBox;
                var tag = cb == null ? null : cb.Tag as RowTag;
                if (tag == null) continue;
                bool hit = q.Length == 0 || (tag.Text ?? "").ToLowerInvariant().Contains(q);
                cb.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                if (hit) shown++;
            }
            UpdateBiomeHint(shown);
        }

        void ApplyStructFilter()
        {
            if (StructSearch == null || StructList == null) return;
            string q = (StructSearch.Text ?? "").Trim().ToLowerInvariant();
            foreach (var c in StructList.Children)
            {
                var cb = c as CheckBox;
                var tag = cb == null ? null : cb.Tag as RowTag;
                if (tag == null) continue;
                bool hit = q.Length == 0 || (tag.Text ?? "").ToLowerInvariant().Contains(q);
                cb.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
