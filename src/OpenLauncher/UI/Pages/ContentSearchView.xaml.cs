using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenLauncher.Content;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
{
    public partial class ContentSearchView : UserControl, IPage
    {
        readonly ContentType _type;
        readonly ObservableCollection<ContentProject> _items = new ObservableCollection<ContentProject>();
        CancellationTokenSource _cts;
        int _offset, _total;
        bool _busy, _init, _suppress;
        Instance _target;

        public ContentSearchView(ContentType type)
        {
            InitializeComponent();
            _type = type;
            Header.Text = type switch
            {
                ContentType.Modpack => "整合包",
                ContentType.ResourcePack => "资源包",
                ContentType.Shader => "光影包",
                ContentType.DataPack => "数据包",
                _ => "Mod",
            };
            Results.ItemsSource = _items;
            LoaderPanel.Visibility = type == ContentType.Mod || type == ContentType.Modpack ? Visibility.Visible : Visibility.Collapsed;
            TargetPanel.Visibility = type == ContentType.Modpack ? Visibility.Collapsed : Visibility.Visible;
            _suppress = true;
            Sort.SelectedIndex = 0;
            Loader.SelectedIndex = 0;
            (Settings.PreferredContentSource == "curseforge" ? SrcCurse : SrcModrinth).IsChecked = true;
            _suppress = false;
        }

        ContentSource Source => SrcCurse.IsChecked == true ? ContentSource.CurseForge : ContentSource.Modrinth;

        public void SetTarget(Instance inst)
        {
            _target = inst;
            if (_init) ApplyTargetDefaults();
        }

        public async void OnShown()
        {
            if (_init) { RefreshTargets(); return; }
            _init = true;
            _suppress = true;
            GameVersion.Items.Add(new ComboBoxItem { Content = "全部", Tag = "" });
            GameVersion.SelectedIndex = 0;
            RefreshTargets();
            ApplyTargetDefaults();
            _suppress = false;
            try
            {
                var versions = await Task.Run(() => VersionList.GetMinecraftAsync());
                _suppress = true;
                var sel = TagOf(GameVersion);
                foreach (var v in versions.Where(v => v.Type == "release"))
                    GameVersion.Items.Add(new ComboBoxItem { Content = v.Id, Tag = v.Id });
                SelectTag(GameVersion, sel);
                _suppress = false;
            }
            catch { _suppress = false; }
            _ = SearchAsync(true);
        }

        void RefreshTargets()
        {
            var keep = _target?.Id ?? (Target.SelectedItem as ComboBoxItem)?.Tag as string ?? Settings.SelectedVersion;
            var old = _suppress;
            _suppress = true;
            Target.Items.Clear();
            foreach (var i in GameRepo.Current.ListInstances().Where(i => i.IsValid))
                Target.Items.Add(new ComboBoxItem { Content = i.Id, Tag = i.Id, DataContext = i, ToolTip = i.Description });
            Target.SelectedItem = Target.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == keep) ?? (Target.Items.Count > 0 ? Target.Items[0] : null);
            _target = (Target.SelectedItem as ComboBoxItem)?.DataContext as Instance;
            _suppress = old;
        }

        /// <summary>按目标版本自动设置 MC 版本和加载器筛选</summary>
        void ApplyTargetDefaults()
        {
            if (_target == null || _type == ContentType.Modpack) return;
            var old = _suppress;
            _suppress = true;
            var mc = _target.McVersion;
            if (!GameVersion.Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag == mc))
                GameVersion.Items.Insert(1, new ComboBoxItem { Content = mc, Tag = mc });
            SelectTag(GameVersion, mc);
            SelectTag(Loader, LoaderInfo.Slug(_target.PrimaryLoader) ?? "");
            Target.SelectedItem = Target.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == _target.Id) ?? Target.SelectedItem;
            _suppress = old;
            if (!old) _ = SearchAsync(true);
        }

        static string TagOf(ComboBox b) => (b.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        static void SelectTag(ComboBox b, string tag) => b.SelectedItem = b.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? b.Items[0];

        void Source_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            Settings.PreferredContentSource = Source == ContentSource.CurseForge ? "curseforge" : "modrinth";
            _ = SearchAsync(true);
        }

        void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!_suppress && _init) _ = SearchAsync(true); }

        void Target_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            _target = (Target.SelectedItem as ComboBoxItem)?.DataContext as Instance;
            ApplyTargetDefaults();
        }

        void Query_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) _ = SearchAsync(true); }
        void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(true);

        async Task SearchAsync(bool reset)
        {
            if (!reset && (_busy || _items.Count >= _total)) return;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            if (reset) { _offset = 0; _total = 0; _items.Clear(); }
            _busy = true;
            if (_items.Count == 0) ShowStatus("搜索中…", true);
            var q = new SearchQuery
            {
                Text = Query.Text.Trim(),
                Type = _type,
                GameVersion = TagOf(GameVersion),
                Loader = LoaderPanel.Visibility == Visibility.Visible ? TagOf(Loader) : null,
                Sort = TagOf(Sort),
                Offset = _offset,
                Limit = 20,
            };
            if (string.IsNullOrEmpty(q.GameVersion)) q.GameVersion = null;
            if (string.IsNullOrEmpty(q.Loader)) q.Loader = null;
            var src = Source;
            try
            {
                var r = await Task.Run(() => ContentService.SearchAsync(src, q, ct), ct);
                if (ct.IsCancellationRequested) return;
                foreach (var p in r.Items) _items.Add(p);
                _offset += r.Items.Count;
                _total = r.Total;
                if (_items.Count == 0) ShowStatus("没有找到结果，换个关键词试试" + (ZhNames.ContainsCjk(q.Text) ? "（中文名不在词表里时，试试英文名）" : ""), false);
                else StatusPanel.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_items.Count == 0) ShowStatus($"搜索失败：{ex.Message}\n\n可以切换到另一个来源，或在 设置 → 下载 里调整 Mod 下载源", false);
                else Dialogs.Toast("加载更多失败：" + ex.Message, MainWindow.ToastKind.Error);
            }
            finally { _busy = false; }
        }

        void ShowStatus(string text, bool loading)
        {
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = text;
            StatusBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }

        void Results_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalOffset > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 80) _ = SearchAsync(false);
        }

        void Results_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (Results.SelectedItem is ContentProject p)
            {
                Results.SelectedItem = null;
                MainWindow.Current.Push(new ProjectPage(p, _type, _type == ContentType.Modpack ? null : _target, TagOf(GameVersion), LoaderPanel.Visibility == Visibility.Visible ? TagOf(Loader) : null));
            }
        }
    }
}
