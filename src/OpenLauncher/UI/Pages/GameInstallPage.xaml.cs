using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenLauncher.Content;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
{
    public partial class GameInstallPage : UserControl, IPage
    {
        readonly McVersionEntry _mc;
        LoaderType _loader = LoaderType.None;
        CancellationTokenSource _cts;
        bool _nameEdited;

        public GameInstallPage(McVersionEntry mc)
        {
            InitializeComponent();
            _mc = mc;
            Header.Text = $"安装 Minecraft {mc.Id}";
            NameBox.Text = mc.Id;
            NameBox.TextChanged += (s, e) => { if (NameBox.IsKeyboardFocused) _nameEdited = true; };
        }

        public void OnShown()
        {
            TargetText.Text = "安装到：" + Settings.SelectedGameFolder;
        }

        void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Back();

        async void Loader_Checked(object sender, RoutedEventArgs e)
        {
            if (LoaderRow == null) return;
            _loader = sender == LFabric ? LoaderType.Fabric : sender == LForge ? LoaderType.Forge : sender == LNeo ? LoaderType.NeoForge : sender == LQuilt ? LoaderType.Quilt : LoaderType.None;
            WithApi.Visibility = _loader == LoaderType.Fabric || _loader == LoaderType.Quilt ? Visibility.Visible : Visibility.Collapsed;
            OptiFineRow.Visibility = _loader == LoaderType.None || _loader == LoaderType.Forge ? Visibility.Visible : Visibility.Collapsed;
            if (OptiFineRow.Visibility != Visibility.Visible) WithOptiFine.IsChecked = false;
            WithApi.Content = _loader == LoaderType.Quilt ? "同时安装 Quilted Fabric API（QFAPI）" : "同时安装 Fabric API（绝大多数 Fabric Mod 都需要它）";
            LoaderVersions.ItemsSource = null;
            if (_loader == LoaderType.None)
            {
                LoaderRow.Visibility = Visibility.Collapsed;
                InstallButton.IsEnabled = true;
                UpdateName();
                return;
            }
            LoaderRow.Visibility = Visibility.Visible;
            LoaderLabel.Text = LoaderInfo.Name(_loader) + " 版本";
            LoaderStatus.Text = "获取中…";
            InstallButton.IsEnabled = false;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var loader = _loader;
            try
            {
                var list = await Task.Run(() => VersionList.GetLoaderVersionsAsync(loader, _mc.Id, ct));
                if (ct.IsCancellationRequested) return;
                if (list.Count == 0)
                {
                    LoaderStatus.Text = $"{LoaderInfo.Name(loader)} 还不支持 {_mc.Id}";
                    return;
                }
                LoaderStatus.Text = $"共 {list.Count} 个";
                LoaderVersions.ItemsSource = list;
                LoaderVersions.SelectedItem = list.FirstOrDefault(v => v.Recommended) ?? list[0];
                InstallButton.IsEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LoaderStatus.Text = "获取失败：" + ex.Message; }
        }

        void LoaderVersions_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateName();

        void UpdateName()
        {
            if (_nameEdited || NameBox == null) return;
            var v = LoaderVersions.SelectedItem as LoaderVersionEntry;
            var name = Installer.DefaultName(_mc.Id, _loader, v?.Version);
            if (WithOptiFine.IsChecked == true && OptiFineVersions.SelectedItem is OptiFineEntry of)
                name += "-OptiFine_" + of.Name;
            NameBox.Text = name;
        }

        List<OptiFineEntry> _optifine;

        async void WithOptiFine_Click(object sender, RoutedEventArgs e)
        {
            bool on = WithOptiFine.IsChecked == true;
            OptiFineVersions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) { OptiFineStatus.Text = ""; UpdateName(); return; }
            if (_optifine == null)
            {
                OptiFineStatus.Text = "获取中…";
                try { _optifine = await Task.Run(() => OptiFine.ListAsync(_mc.Id)); }
                catch (Exception ex) { OptiFineStatus.Text = "获取失败：" + ex.Message; return; }
            }
            if (_optifine.Count == 0) { OptiFineStatus.Text = $"OptiFine 没有 {_mc.Id} 的版本"; OptiFineVersions.Visibility = Visibility.Collapsed; return; }
            OptiFineStatus.Text = "";
            OptiFineVersions.ItemsSource = _optifine;
            OptiFineVersions.SelectedItem = _optifine.FirstOrDefault(x => !x.Preview) ?? _optifine[0];
            UpdateName();
        }

        void Install_Click(object sender, RoutedEventArgs e)
        {
            var repo = GameRepo.Current;
            var name = Util.SafeFileName(NameBox.Text.Trim());
            if (string.IsNullOrEmpty(name)) { Dialogs.Toast("版本名不能为空"); return; }
            if (repo.HasVersion(name)) { Dialogs.Toast($"已经有叫 {name} 的版本了，换个名字"); return; }
            var req = new InstallRequest
            {
                Repo = repo,
                McVersion = _mc.Id,
                Loader = _loader,
                LoaderVersion = (LoaderVersions.SelectedItem as LoaderVersionEntry)?.Version,
                OptiFineVersion = WithOptiFine.IsChecked == true && OptiFineRow.Visibility == Visibility.Visible ? (OptiFineVersions.SelectedItem as OptiFineEntry)?.Name : null,
                Name = name,
            };
            if (req.Loader == LoaderType.Forge && req.OptiFineVersion != null && OptiFineVersions.SelectedItem is OptiFineEntry ofe && ofe.Forge == null)
                Dialogs.Toast("提示：这个 OptiFine 版本没有标注兼容的 Forge 版本，可能无法一起使用");
            bool api = WithApi.Visibility == Visibility.Visible && WithApi.IsChecked == true;
            TaskManager.Start($"安装 {name}", async t =>
            {
                var id = await Installer.InstallAsync(req, t, 0, api ? 0.92 : 1);
                if (api)
                {
                    t.Report(0.93, "安装 Fabric API…");
                    try { await InstallApiAsync(repo, id, req, t); }
                    catch (Exception ex) { Log.Warn("Fabric API 安装失败：" + ex.Message); t.Detail = "游戏装好了，但 Fabric API 下载失败，可以稍后在 Mod 下载里装"; }
                }
                Settings.SelectedVersion = id;
                TaskManager.OnUi(() => MainWindow.Current.LaunchPage.RefreshVersion());
            });
            MainWindow.Current.ToggleTasks(true);
            MainWindow.Current.Back();
        }

        static async Task InstallApiAsync(GameRepo repo, string id, InstallRequest req, LauncherTask t)
        {
            var inst = new Instance(repo, id);
            var slug = req.Loader == LoaderType.Quilt ? "qsl" : "fabric-api";
            var loader = LoaderInfo.Slug(req.Loader);
            var files = await ContentService.Modrinth.GetFilesAsync(slug, req.McVersion, loader, t.Token);
            var file = files.FirstOrDefault(f => f.ReleaseType == "release") ?? files.FirstOrDefault();
            if (file == null) return;
            Directory.CreateDirectory(inst.ModsDir);
            await ContentService.InstallAsync(file, inst.ModsDir, true, req.McVersion, loader, t);
        }
    }
}
