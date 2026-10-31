using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LunoLauncher.Content;
using LunoLauncher.Core;
using LunoLauncher.UI;

namespace LunoLauncher.UI.Pages
{
    /// <summary>
    /// 种子查询的地图画布（R1：画布自适应）。
    ///
    /// 和旧实现最大的区别：
    ///  - 没有 ScrollViewer 了。画布永远是地图区域的可用尺寸，100% 时内容正好填满，不出现滚动条；
    ///  - 视角用「可视区中心的方块坐标 _viewX/_viewZ」+ 「缩放 _zoom（1 方块 = zoom 个 DIP）」描述，
    ///    所以可以无限往任意方向拖，窗口拉大就自动多渲染一片；
    ///  - 拖动期间只移动内容层（RenderTransform），一个原生调用都不发 → 拖到 60fps 不掉帧；
    ///    松手后才在后台线程重算（期间那块区域是浅色占位，右上角显示「补算中…」）；
    ///  - 缩放以鼠标为锚点：锚点下面那个方块在缩放前后保持贴住鼠标；
    ///  - 每 1 个屏幕最多画 4 方块一采样的格子，采样量跟窗口大小成正比、跟缩放无关（约 6 万次），
    ///    所以不会出现「一次渲染 2500 万区块」这种自杀式操作。
    /// </summary>
    public partial class ToolboxPage
    {
        const int ChunkBlocks = 16;
        /// <summary>100% 时每几个方块取一次群系（cubiomes 的 1:4 分辨率）</summary>
        const int SampleBlockDefault = 4;
        const double MinZoom = 1.0;
        const double MaxZoom = 8.0;

        // ---------- 几何：对应最近一次渲染出来的那张位图 ----------
        double _dpi = 1.0;
        double _ppb = 1;            // 位图像素 / 方块 = zoom * dpi
        double _leftBlockX, _leftBlockZ;   // 位图左上角对应的方块（带小数，网格才对得齐）
        int _sampleBlocks = SampleBlockDefault;
        int _gridW, _gridH;
        int[] _grid;
        int _gridMc;

        // ---------- 交互 ----------
        bool _dragging;
        bool _pressSeen;          // 这次 MouseUp 有没有对应的 MouseDown
        Point _pressPos;
        double _shiftX, _shiftY;
        int _clickCount;
        DispatcherTimer _resizeTimer;
        bool _busy;

        double AreaW => MapHost == null ? 0 : MapHost.ActualWidth;
        double AreaH => MapHost == null ? 0 : MapHost.ActualHeight;
        /// <summary>1 方块 = 多少 DIP（画布和屏幕 1:1，缩放就是这个比值）</summary>
        double DipPerBlock => _zoom;

        void EnsureDpi()
        {
            try
            {
                var src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null)
                {
                    double s = src.CompositionTarget.TransformToDevice.M11;
                    if (s > 0) _dpi = s;
                }
            }
            catch { }
            if (_dpi <= 0) _dpi = 1.0;
        }

        /// <summary>屏幕 DIP 偏移 → 方块坐标</summary>
        int BlockXAt(double dipX) => (int)Math.Floor(_leftBlockX + dipX / _zoom);
        int BlockZAt(double dipY) => (int)Math.Floor(_leftBlockZ + dipY / _zoom);
        /// <summary>方块坐标 → 屏幕 DIP 偏移</summary>
        double DipXOf(double blockX) => (blockX - _leftBlockX) * _zoom;
        double DipZOf(double blockZ) => (blockZ - _leftBlockZ) * _zoom;

        // ---------- 尺寸变化：防抖后重画 ----------
        void MapHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_seedReady) return;
            if (_resizeTimer == null)
            {
                _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
                _resizeTimer.Tick += (s, a) => { _resizeTimer.Stop(); RenderSeed(); };
            }
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        // ---------- 缩放 ----------
        void MapHost_PreviewWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            ZoomAt(e.GetPosition(MapHost), _zoom * Math.Pow(2, e.Delta / 8.0 / 100.0));
        }

        void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(AreaCenterPoint(), _zoom * 1.25);
        void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(AreaCenterPoint(), _zoom / 1.25);

        Point AreaCenterPoint() => new Point(AreaW / 2, AreaH / 2);

        /// <summary>以某个点为锚点缩放：那个点下面的方块缩放前后不动</summary>
        void ZoomAt(Point anchor, double newZoom)
        {
            newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));
            if (Math.Abs(newZoom - _zoom) < 0.0005) return;
            double blockX = _leftBlockX + anchor.X / _zoom;
            double blockZ = _leftBlockZ + anchor.Y / _zoom;
            _zoom = newZoom;
            _viewX = (int)Math.Round(blockX - (anchor.X - AreaW / 2) / _zoom);
            _viewZ = (int)Math.Round(blockZ - (anchor.Y - AreaH / 2) / _zoom);
            if (ZoomText != null) ZoomText.Text = $"{_zoom * 100:0}%";
            RenderSeed();
        }

        // ---------- 鼠标：拖动平移 / 单击选点 ----------
        void MapHost_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            CloseStructInfo();
            _pressPos = e.GetPosition(MapHost);
            _dragging = false;
            _shiftX = _shiftY = 0;
            _pressSeen = true;
            _clickCount = e.ClickCount;      // ClickCount 在 MouseDown 上才靠得住（MouseUp 往往是 1）
            MapHost.CaptureMouse();
        }

        void MapHost_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(MapHost);
            if (!MapHost.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            {
                UpdateHover(p);
                return;
            }
            double dx = p.X - _pressPos.X, dy = p.Y - _pressPos.Y;
            if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 4) _dragging = true;
            if (!_dragging) return;
            // 只移动内容层，不重画 → 拖动跟手
            _shiftX = dx;
            _shiftY = dy;
            MapShift.X = dx;
            MapShift.Y = dy;
        }

        void MapHost_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (MapHost.IsMouseCaptured) MapHost.ReleaseMouseCapture();

            // 这次抬起没有对应的按下（比如点在结构图标 / 弹框 / 红点上，那些地方 Handled 了）
            // 就完全不算画布点击 —— 否则会拿上一次双击留下的 _clickCount 去移动红点
            bool pressSeen = _pressSeen;
            int clicks = _clickCount;
            _pressSeen = false;
            _clickCount = 0;
            if (!pressSeen) return;

            if (!_dragging)
            {
                // R9：单击不落点（只更新悬停信息），双击才把红点移到这个方块
                if (clicks >= 2)
                {
                    var q = e.GetPosition(MapHost);
                    if (q.X < 0 || q.Y < 0 || q.X >= AreaW || q.Y >= AreaH) return;
                    _dotX = BlockXAt(q.X);
                    _dotZ = BlockZAt(q.Y);
                    PlayerXBox.Text = _dotX.ToString();
                    PlayerZBox.Text = _dotZ.ToString();
                    UpdateCoordText();
                    PlaceDot();
                    CloseStructInfo();
                }
                return;
            }

            _dragging = false;
            double dx = _shiftX, dy = _shiftY;
            _shiftX = _shiftY = 0;
            // 这里先不把内容层归位：等新位图算完再一起归位（ResetContentTransform），
            // 否则中间会闪一下旧地图。没拖动到位就什么都不做。
            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) { MapShift.X = 0; MapShift.Y = 0; return; }
            // 拖动距离换算成方块距离：内容跟着手走，所以视角中心往反方向挪
            _viewX -= (int)Math.Round(dx / _zoom);
            _viewZ -= (int)Math.Round(dy / _zoom);
            RenderSeed();
        }

        void MapHost_Leave(object sender, MouseEventArgs e) => UpdateCoordText();

        // ---------- 群系查询：只查渲染时留下的那张表 ----------
        int GridBiome(int bx, int bz)
        {
            int[] g = _grid;
            if (g == null) return int.MinValue;
            int sx = (int)Math.Floor((bx - _leftBlockX) / _sampleBlocks);
            int sz = (int)Math.Floor((bz - _leftBlockZ) / _sampleBlocks);
            if (sx < 0 || sz < 0 || sx >= _gridW || sz >= _gridH) return int.MinValue;
            return g[sz * _gridW + sx];
        }

        string BiomeLabel(int id)
        {
            if (id == int.MinValue || id < 0 || _gridMc <= 0) return "";
            string en = Cubiomes.BiomeName(_gridMc, id);
            return string.IsNullOrEmpty(en) ? "" : BiomeNames.Zh(en);
        }

        void UpdateHover(Point p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= AreaW || p.Y >= AreaH) { UpdateCoordText(); return; }
            int bx = BlockXAt(p.X), bz = BlockZAt(p.Y);
            string biome = BiomeLabel(GridBiome(bx, bz));
            CoordText.Text = string.IsNullOrEmpty(biome)
                ? $"光标：{bx}, {bz}"
                : $"光标：{bx}, {bz} · 群系 {biome}";
        }

        void UpdateCoordText()
        {
            string biome = BiomeLabel(GridBiome(_dotX, _dotZ));
            CoordText.Text = string.IsNullOrEmpty(biome)
                ? $"玩家：{_dotX}, {_dotZ}"
                : $"玩家：{_dotX}, {_dotZ} · 群系 {biome}";
        }

        // ---------- 渲染 ----------
        /// <summary>一次渲染的结果（位图 + 采样表 + 结构点 + 状态栏文案）</summary>
        sealed class MapRender
        {
            public BitmapSource Bitmap;
            public int[] Grid;
            public int GridW, GridH;
            public double LeftX, LeftZ;
            public int SampleBlocks;
            public int Mc, Dim;
            public double Zoom;
            public List<StructMark> Marks;
            public string Summary;
        }

        async void RenderSeed()
        {
            if (!_seedReady || SeedBox == null || MapHost == null) return;
            if (!SeedTools.TryParseSeed(SeedBox.Text, out _))
            {
                SeedHint.Text = "先填一个种子（数字或文本都行）。";
                return;
            }
            if (_busy) { _renderPending = true; return; }
            _busy = true;
            MapBusy.Visibility = Visibility.Visible;
            try
            {
                do
                {
                    _renderPending = false;
                    EnsureDpi();
                    double aw = AreaW, ah = AreaH;
                    if (aw < 40 || ah < 40) { Log.Info($"[seed] 跳过渲染：区域还没量出来 {aw}x{ah}"); break; }          // 还没布局完，等 SizeChanged 再来
                    long seed;
                    if (!SeedTools.TryParseSeed(SeedBox.Text, out seed)) break;
                    int mcVer = CurMc, dim = CurDim, y = CurQueryY;
                    // 能力探测：这个版本 cubiomes 生成不出来就明确说"不支持"，不画错也不画空
                    if (!Cubiomes.CanSetup(mcVer)) { ShowUnsupported(mcVer); break; }
                    int vx = _viewX, vz = _viewZ;
                    double zoom = _zoom;
                    bool showSlime = dim == 0 && SlimeToggle.IsChecked == true;
                    var visible = new HashSet<int>(_visible);
                    var structs = new HashSet<int>(_showStruct);

                    MapRender res;
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        res = await Task.Run(() => BuildMap(seed, mcVer, dim, y, vx, vz, zoom, aw, ah, showSlime, visible, structs));
                        sw.Stop();
                        if (sw.ElapsedMilliseconds > 400)
                            Log.Info($"[seed] 渲染偏慢 {aw:0}x{ah:0} zoom={zoom} {sw.ElapsedMilliseconds}ms");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("绘制种子地图失败：" + ex.Message);
                        SeedHint.Text = "绘制失败：" + ex.Message;
                        return;
                    }
                    if (res == null) break;

                    _ppb = zoom * _dpi;
                    _leftBlockX = res.LeftX;
                    _leftBlockZ = res.LeftZ;
                    _sampleBlocks = res.SampleBlocks;
                    _gridW = res.GridW;
                    _gridH = res.GridH;
                    _grid = res.Grid;
                    _gridMc = res.Mc;
                    SeedMap.Source = res.Bitmap;
                    ResetContentTransform();
                    DrawStructures(res.Marks);
                    PlaceDot();
                    UpdateCoordText();
                    SeedHint.Text = res.Summary;
                } while (_renderPending);
            }
            finally
            {
                _busy = false;
                MapBusy.Visibility = Visibility.Collapsed;
            }
        }

        void ResetContentTransform()
        {
            MapShift.X = 0;
            MapShift.Y = 0;
        }

        /// <summary>版本 / 原生库不支持时：清空地图并说明原因，绝不画错或画空当"就是这样"</summary>
        void ShowUnsupported(int mcVer)
        {
            SeedMap.Source = null;
            StructLayer.Children.Clear();
            _grid = null;
            _dot.Visibility = Visibility.Collapsed;
            CloseStructInfo();
            SeedHint.Text = Cubiomes.Available
                ? $"{VersionName(mcVer)}：cubiomes 里没有这个版本的生成器，暂时不支持（换一个版本试试）"
                : "种子查询用不了：" + (Cubiomes.LastError ?? "cubiomes 原生库加载失败");
        }

        MapRender BuildMap(long seed, int mcVer, int dim, int y, int viewX, int viewZ, double zoom,
                           double areaW, double areaH, bool showSlime, HashSet<int> visible, HashSet<int> structs)
        {
            EnsureDpi();
            int bmpW = Math.Max(16, (int)Math.Ceiling(areaW * _dpi));
            int bmpH = Math.Max(16, (int)Math.Ceiling(areaH * _dpi));
            double ppb = Math.Max(0.1, zoom * _dpi);        // 位图像素 / 方块
            int sampleBlocks = zoom <= 1.5 ? 4 : zoom <= 3 ? 2 : 1;
            double leftX = viewX - areaW / (2 * zoom);
            double leftZ = viewZ - areaH / (2 * zoom);
            int gw = Math.Max(1, (int)Math.Ceiling(areaW / zoom / sampleBlocks));
            int gh = Math.Max(1, (int)Math.Ceiling(areaH / zoom / sampleBlocks));
            int cellW = Math.Max(1, (int)Math.Round(sampleBlocks * ppb));
            int cellH = cellW;

            bool allBiomes = visible.Count == 0;   // 一个都没勾 = 什么都不画（不能当"全部"用）
            var px = new byte[bmpW * bmpH * 4];
            var grid = new int[gw * gh];
            for (int i = 0; i < grid.Length; i++) grid[i] = -1;
            List<StructMark> marks = new List<StructMark>();
            int slimeCount = 0;

            bool biomes = Cubiomes.Available && Cubiomes.Setup(mcVer, false);
            if (biomes)
            {
                lock (Cubiomes.Sync)
                {
                    Cubiomes.ApplySeed(seed, dim);
                    int qy = y >> 2;      // cubiomes 的 1:4 采样：x/y/z 都要右移 2 位
                    for (int gy = 0; gy < gh; gy++)
                    {
                        int bz = (int)Math.Floor(leftZ + gy * sampleBlocks);
                        for (int gx = 0; gx < gw; gx++)
                        {
                            int bx = (int)Math.Floor(leftX + gx * sampleBlocks);
                            grid[gy * gw + gx] = Cubiomes.BiomeAt(4, bx >> 2, qy, bz >> 2);
                        }
                    }
                    marks = ScanStructures(mcVer, dim, leftX, leftZ, areaW / zoom, areaH / zoom, structs);
                }

                // 采样表 → 像素
                for (int gy = 0; gy < gh; gy++)
                {
                    int y0 = gy * cellH;
                    int y1 = Math.Min(y0 + cellH, bmpH);
                    if (y0 >= bmpH) break;
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int x0 = gx * cellW;
                        int x1 = Math.Min(x0 + cellW, bmpW);
                        if (x0 >= bmpW) break;
                        int id = grid[gy * gw + gx];
                        bool keep = !allBiomes && visible.Contains(id);
                        if (!keep) continue;   // 不勾选的群系不渲染：留透明，露出浅色占位
                        Color bc = BiomeColors.Get(id);
                        for (int yy = y0; yy < y1; yy++)
                        {
                            int row = yy * bmpW;
                            for (int xx = x0; xx < x1; xx++)
                            {
                                int o = (row + xx) * 4;
                                px[o] = bc.B; px[o + 1] = bc.G; px[o + 2] = bc.R; px[o + 3] = 255;
                            }
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < px.Length; i += 4) { px[i] = 0xF5; px[i + 1] = 0xF5; px[i + 2] = 0xF5; px[i + 3] = 0x00; }
            }

            // 史莱姆区块（只在主世界）：起点 +1 像素，避开压在区块边界上的网格线
            if (showSlime)
            {
                int cx0 = FloorDiv((int)Math.Floor(leftX), ChunkBlocks);
                int cz0 = FloorDiv((int)Math.Floor(leftZ), ChunkBlocks);
                int cx1 = FloorDiv((int)Math.Floor(leftX + areaW / zoom), ChunkBlocks);
                int cz1 = FloorDiv((int)Math.Floor(leftZ + areaH / zoom), ChunkBlocks);
                int chunkPx = Math.Max(1, (int)Math.Round(ChunkBlocks * ppb));
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    int py0 = (int)Math.Round((cz * ChunkBlocks - leftZ) * ppb) + 1;
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        if (!SeedTools.IsSlimeChunk(seed, cx, cz)) continue;
                        slimeCount++;
                        int px0 = (int)Math.Round((cx * ChunkBlocks - leftX) * ppb) + 1;
                        for (int yy = py0; yy < py0 + chunkPx - 1; yy++)
                        {
                            if (yy < 0 || yy >= bmpH) continue;
                            for (int xx = px0; xx < px0 + chunkPx - 1; xx++)
                            {
                                if (xx < 0 || xx >= bmpW) continue;
                                int o = (yy * bmpW + xx) * 4;
                                if (px[o + 3] == 0) continue;   // 该格没渲染，别把它涂绿
                                px[o] = (byte)((px[o] * 45 + 0x6B * 55) / 100);
                                px[o + 1] = (byte)((px[o + 1] * 45 + 0xA0 * 55) / 100);
                                px[o + 2] = (byte)((px[o + 2] * 45 + 0x22 * 55) / 100);
                            }
                        }
                    }
                }
            }

            // 区块网格：和史莱姆覆盖层用同一个"对齐到区块边界"的原点
            int gridPx = Math.Max(2, (int)Math.Round(ChunkBlocks * ppb));
            int firstX = (int)Math.Ceiling(leftX / ChunkBlocks) * ChunkBlocks;
            for (double b = firstX; b < leftX + areaW / zoom; b += ChunkBlocks)
            {
                int x = (int)Math.Round((b - leftX) * ppb);
                if (x < 0 || x >= bmpW) continue;
                for (int yy = 0; yy < bmpH; yy++) PutPixel(px, bmpW, bmpH, x, yy, 70);
            }
            int firstZ = (int)Math.Ceiling(leftZ / ChunkBlocks) * ChunkBlocks;
            for (double b = firstZ; b < leftZ + areaH / zoom; b += ChunkBlocks)
            {
                int y2 = (int)Math.Round((b - leftZ) * ppb);
                if (y2 < 0 || y2 >= bmpH) continue;
                for (int xx = 0; xx < bmpW; xx++) PutPixel(px, bmpW, bmpH, xx, y2, 70);
            }

            var bmp = BitmapSource.Create(bmpW, bmpH, 96, 96, PixelFormats.Bgra32, null, px, bmpW * 4);
            bmp.Freeze();

            string mode = biomes ? (allBiomes ? "没有勾选群系" : $"已勾选 {visible.Count} 个群系") : "cubiomes 未加载";
            string dimText = DimName(dim) + (dim == 0 && mcVer >= 22 ? " · " + LayerName(y) : "");
            string range = VersionRange(mcVer);
            string verText = VersionName(mcVer) + (string.IsNullOrEmpty(range) || range == VersionName(mcVer) ? "" : $"（{range}）");
            int blocksW = (int)Math.Round(areaW / zoom), blocksH = (int)Math.Round(areaH / zoom);
            string summary =
                $"{verText} · 种子 {seed} · {dimText} · 视角 ({viewX}, {viewZ}) · " +
                $"可见 {blocksW}×{blocksH} 方块（缩放 {zoom * 100:0}%） · " +
                (showSlime ? $"史莱姆区块 {slimeCount} 个 · " : "") +
                $"结构 {marks.Count} 个 · {mode}";

            return new MapRender
            {
                Bitmap = bmp,
                Grid = grid,
                GridW = gw,
                GridH = gh,
                LeftX = leftX,
                LeftZ = leftZ,
                SampleBlocks = sampleBlocks,
                Mc = mcVer,
                Dim = dim,
                Zoom = zoom,
                Marks = marks,
                Summary = summary,
            };
        }

        // ---------- 覆盖层 ----------
        /// <summary>把玩家点画到覆盖层</summary>
        void PlaceDot()
        {
            if (_dot == null) return;
            double px = DipXOf(_dotX);
            double py = DipZOf(_dotZ);
            if (px < -20 || py < -20 || px > AreaW + 20 || py > AreaH + 20)
            {
                _dot.Visibility = Visibility.Collapsed;
                return;
            }
            Canvas.SetLeft(_dot, px - _dot.Width / 2);
            Canvas.SetTop(_dot, py - _dot.Height / 2);
            _dot.Visibility = Visibility.Visible;
        }

        /// <summary>把结构标记画到覆盖层上（UI 线程，坐标是屏幕 DIP）</summary>
        void DrawStructures(List<StructMark> marks)
        {
            StructLayer.Children.Clear();
            _infoMark = null;
            StructInfo.Visibility = Visibility.Collapsed;
            if (marks == null) return;
            foreach (var m in marks)
            {
                double px = DipXOf(m.X);
                double pz = DipZOf(m.Z);
                if (px < -20 || pz < -20 || px > AreaW + 20 || pz > AreaH + 20) continue;
                Color c = m.Type >= 0 && m.Type < StructColors.Length ? StructColors[m.Type] : Colors.Gray;
                ImageSource icon = StructIcons.Get(m.Type);
                FrameworkElement content;
                Brush background;
                if (icon != null)
                {
                    // 图标本体是透明的，垫一层半透明白底，深色群系上也看得清
                    content = new Image { Source = icon, Stretch = Stretch.Uniform, Width = 30, Height = 30, IsHitTestVisible = false };
                    background = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
                }
                else
                {
                    content = new TextBlock
                    {
                        Text = m.Type >= 0 && m.Type < StructBadges.Length ? StructBadges[m.Type] : "?",
                        Foreground = Brushes.White,
                        FontSize = 16,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false,
                    };
                    background = new SolidColorBrush(c);
                }
                var box = new Border
                {
                    Width = 38,
                    Height = 38,
                    CornerRadius = new CornerRadius(7),
                    Background = background,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                    BorderThickness = new Thickness(1),
                    Child = content,
                    Tag = m,
                    ToolTip = $"{StructNames[m.Type]}  {m.X}, {m.Z}",
                    Cursor = Cursors.Hand,
                };
                box.MouseLeftButtonDown += StructMarker_Click;
                box.MouseEnter += StructMarker_Enter;      // R4：悬停就弹小框（名称 + 坐标 + 复制 /tp）
                box.MouseLeave += StructMarker_Leave;
                Canvas.SetLeft(box, px - box.Width / 2);
                Canvas.SetTop(box, pz - box.Height / 2);
                StructLayer.Children.Add(box);
            }
            // 调试 / 截图用：--page seed … popup=1 直接把第一个结构的小框打开（每帧都打开，方便截图）
            if (_popupFirstMark && marks.Count > 0)
            {
                ShowStructInfo(marks[0]);
                CancelHideStructInfo();
            }
        }

        void StructMarker_Click(object sender, MouseButtonEventArgs e)
        {
            // 点结构标记只是"看信息 + 复制 TP"，不要动红点（红点只能靠双击画布移动）
            e.Handled = true;
            var mark = (sender as FrameworkElement)?.Tag as StructMark;
            if (mark == null) return;
            ShowStructInfo(mark);
            _infoPinned = true;          // 点开的小框不自动收
            CancelHideStructInfo();
        }

        /// <summary>点红点：显示玩家坐标 + 复制 /tp</summary>
        void Dot_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowDotInfo();
            _infoPinned = true;          // 点开就常驻，直到点别处或按「关闭」
            CancelHideStructInfo();
        }

        void ShowDotInfo()
        {
            _infoMark = null;
            _infoIsDot = true;
            StructInfoTitle.Text = "玩家位置";
            StructInfoCoord.Text = $"{_dotX}, {_dotZ} · {DimName(CurDim)}";
            double px = DipXOf(_dotX);
            double pz = DipZOf(_dotZ);
            Canvas.SetLeft(StructInfo, Math.Max(4, Math.Min(px + 16, Math.Max(4, AreaW - 200))));
            Canvas.SetTop(StructInfo, Math.Max(4, Math.Min(pz + 16, Math.Max(4, AreaH - 110))));
            StructInfo.Visibility = Visibility.Visible;
        }

        void StructMarker_Enter(object sender, MouseEventArgs e)
        {
            var mark = (sender as FrameworkElement)?.Tag as StructMark;
            if (mark == null) return;
            CancelHideStructInfo();
            ShowStructInfo(mark);
            _infoPinned = false;         // 悬停出来的小框：鼠标移开就收
        }

        void StructMarker_Leave(object sender, MouseEventArgs e)
        {
            // 给 300ms 缓冲：鼠标从标记移到小框上时不要把它收掉
            if (StructInfo == null || StructInfo.Visibility != Visibility.Visible) return;
            HideStructInfoSoon();
        }

        DispatcherTimer _infoHideTimer;

        /// <summary>过 300ms 再收起小框（鼠标进到小框里就取消）。点开的小框常驻，不自动收</summary>
        void HideStructInfoSoon()
        {
            if (_infoPinned || _infoIsDot) return;
            if (_infoHideTimer == null)
            {
                _infoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _infoHideTimer.Tick += (s, a) => { _infoHideTimer.Stop(); CloseStructInfo(); };
            }
            _infoHideTimer.Stop();
            _infoHideTimer.Start();
        }

        void CancelHideStructInfo()
        {
            if (_infoHideTimer != null) _infoHideTimer.Stop();
        }

        /// <summary>点结构标记：弹小框显示坐标 + 复制 /tp</summary>
        void ShowStructInfo(StructMark mark)
        {
            _infoMark = mark;
            _infoIsDot = false;
            StructInfoTitle.Text = mark.Type >= 0 && mark.Type < StructNames.Length ? StructNames[mark.Type] : "结构";
            StructInfoCoord.Text = $"{mark.X}, {mark.Z} · {DimName(CurDim)}";
            double px = DipXOf(mark.X);
            double pz = DipZOf(mark.Z);
            Canvas.SetLeft(StructInfo, Math.Max(4, Math.Min(px + 14, Math.Max(4, AreaW - 200))));
            Canvas.SetTop(StructInfo, Math.Max(4, Math.Min(pz + 14, Math.Max(4, AreaH - 110))));
            StructInfo.Visibility = Visibility.Visible;
        }

        void CloseStructInfo()
        {
            _infoPinned = false;
            if (StructInfo == null || StructInfo.Visibility != Visibility.Visible) { _infoMark = null; _infoIsDot = false; return; }
            StructInfo.Visibility = Visibility.Collapsed;
            _infoMark = null;
            _infoIsDot = false;
        }

        static void PutPixel(byte[] px, int w, int h, int x, int y, byte alpha)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int o = (y * w + x) * 4;
            if (px[o + 3] == 0) return;   // 透明格子上不画网格线
            px[o] = (byte)((px[o] * (255 - alpha) + 0 * alpha) / 255);
            px[o + 1] = (byte)((px[o + 1] * (255 - alpha) + 0 * alpha) / 255);
            px[o + 2] = (byte)((px[o + 2] * (255 - alpha) + 0 * alpha) / 255);
        }
    }
}
