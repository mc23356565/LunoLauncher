using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenLauncher.Content;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
{
    public class InstanceItem
    {
        public Instance Instance { get; set; }
        public string Id => Instance.Id;
        public string Desc { get; set; }
        public Brush Badge { get; set; }
        public string BadgeText { get; set; }

        public static InstanceItem From(Instance i)
        {
            var (color, text) = i.IsValid ? BadgeFor(i.PrimaryLoader) : ("#E5484D", "!");
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            b.Freeze();
            return new InstanceItem { Instance = i, Desc = i.Description, Badge = b, BadgeText = text };
        }

        public static (string color, string text) BadgeFor(LoaderType t) => t switch
        {
            LoaderType.Forge => ("#3D4A5C", "Fo"),
            LoaderType.NeoForge => ("#D7742F", "Ne"),
            LoaderType.Fabric => ("#B89A6B", "Fa"),
            LoaderType.Quilt => ("#8B5CF6", "Q"),
            LoaderType.OptiFine => ("#C0392B", "Op"),
            LoaderType.LiteLoader => ("#2C82C9", "Li"),
            _ => ("#4CAF50", "MC"),
        };
    }

    public partial class VersionsPage : UserControl, IPage
    {
        List<InstanceItem> _all = new List<InstanceItem>();
        bool _loading;

        public VersionsPage()
        {
            InitializeComponent();
        }

        public void OnShown()
        {
            LoadFolders();
        }

        void LoadFolders()
        {
            _loading = true;
            var folders = Settings.GameFolders;
            Folders.ItemsSource = folders;
            Folders.SelectedItem = folders.FirstOrDefault(f => string.Equals(f.Path, Settings.SelectedGameFolder, StringComparison.OrdinalIgnoreCase)) ?? folders[0];
            _loading = false;
            _ = LoadVersionsAsync();
        }

        async Task LoadVersionsAsync()
        {
            var repo = GameRepo.Current;
            var items = await Task.Run(() => repo.ListInstances().Select(InstanceItem.From)
                .OrderByDescending(i => i.Instance.IsValid)
                .ThenByDescending(i => SafeTime(i.Instance.Dir)).ToList());
            _all = items;
            ApplyFilter();
            var sel = Settings.SelectedVersion;
            Versions.SelectedItem = _all.FirstOrDefault(i => i.Id == sel);
        }

        static DateTime SafeTime(string d) { try { return Directory.GetLastWriteTime(d); } catch { return DateTime.MinValue; } }

        void ApplyFilter()
        {
            var q = Search.Text.Trim();
            var list = string.IsNullOrEmpty(q) ? _all : _all.Where(i => i.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || i.Desc.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Versions.ItemsSource = list;
            Empty.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
        void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadVersionsAsync();
        void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Back();
        void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Current.GoTab("download");

        void Folders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !(Folders.SelectedItem is GameFolder f)) return;
            Settings.SelectedGameFolder = f.Path;
            _ = LoadVersionsAsync();
        }

        void Select(InstanceItem item)
        {
            if (item == null) return;
            Settings.SelectedVersion = item.Id;
            MainWindow.Current.LaunchPage.RefreshVersion();
        }

        void Versions_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Versions.SelectedItem is InstanceItem item)
            {
                Select(item);
                MainWindow.Current.Back();
            }
        }

        static InstanceItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as InstanceItem;

        void LaunchItem_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            Select(item);
            MainWindow.Current.Back();
            _ = MainWindow.Current.LaunchPage.LaunchAsync(item.Instance);
        }

        void SettingsItem_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            Select(item);
            MainWindow.Current.Push(new InstancePage(item.Instance));
        }

        void FolderItem_Click(object sender, RoutedEventArgs e) => Util.OpenFolder(ItemOf(sender).Instance.Dir);

        async void ImportPack_Click(object sender, RoutedEventArgs e)
        {
            var file = Pickers.PickFile("选择整合包", "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件|*.*");
            if (file == null) return;
            await ImportPackAsync(file);
        }

        public static async Task ImportPackAsync(string file)
        {
            var name = await Dialogs.Prompt("导入整合包", "给这个整合包起个版本名（留空用整合包自己的名字）", "");
            if (name == null) return;
            var repo = GameRepo.Current;
            TaskManager.Start("导入整合包 " + Path.GetFileName(file), async t =>
            {
                var id = await ModpackImporter.ImportAsync(repo, file, string.IsNullOrWhiteSpace(name) ? null : name, t);
                Settings.SelectedVersion = id;
                TaskManager.OnUi(() => MainWindow.Current.LaunchPage.RefreshVersion());
            });
            MainWindow.Current.ToggleTasks(true);
        }

        // ---------- 游戏目录管理 ----------
        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = Pickers.PickFolder("选择一个 .minecraft 文件夹");
            if (path == null) return;
            if (!Directory.Exists(Path.Combine(path, "versions")) && Directory.Exists(Path.Combine(path, ".minecraft"))) path = Path.Combine(path, ".minecraft");
            AddFolder(path, Path.GetFileName(Path.GetDirectoryName(path)) ?? "游戏目录");
        }

        void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = Pickers.PickFolder("选择新游戏目录的位置（会在里面创建 .minecraft）");
            if (path == null) return;
            path = Path.Combine(path, ".minecraft");
            Directory.CreateDirectory(path);
            AddFolder(path, "新游戏目录");
        }

        void AddFolder(string path, string name)
        {
            var list = Settings.GameFolders;
            if (!list.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                list.Add(new GameFolder { Name = name, Path = path });
            Settings.GameFolders = list;
            Settings.SelectedGameFolder = path;
            LoadFolders();
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Folders.SelectedItem is GameFolder f) Util.OpenFolder(f.Path); }

        async void RenameFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(Folders.SelectedItem is GameFolder f)) return;
            var n = await Dialogs.Prompt("重命名", "显示名称", f.Name);
            if (string.IsNullOrWhiteSpace(n)) return;
            var list = Settings.GameFolders;
            foreach (var x in list) if (string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase)) x.Name = n;
            Settings.GameFolders = list;
            LoadFolders();
        }

        void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(Folders.SelectedItem is GameFolder f)) return;
            var list = Settings.GameFolders.Where(x => !string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == 0) { Dialogs.Toast("至少要保留一个游戏目录"); return; }
            Settings.GameFolders = list;
            Settings.SelectedGameFolder = list[0].Path;
            LoadFolders();
        }
    }
}
