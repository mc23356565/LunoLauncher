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
    public class FileRow
    {
        public ContentFile File { get; set; }
        public string DisplayName => string.IsNullOrEmpty(File.DisplayName) ? File.FileName : File.DisplayName;
        public string ReleaseTypeText => File.ReleaseTypeText;
        public DateTime Published => File.Published;
        public string GameVersionsText
        {
            get
            {
                var v = File.GameVersions ?? new List<string>();
                return v.Count == 0 ? "未标注版本" : v.Count <= 4 ? string.Join(", ", v) : $"{v.First()} ~ {v.Last()}（{v.Count} 个版本）";
            }
        }
        public string LoadersText => File.Loaders == null || File.Loaders.Count == 0 ? "通用" : string.Join(" / ", File.Loaders);
        public string SizeText => File.Size > 0 ? Util.FormatSize(File.Size) : "";
    }

    public partial class ProjectPage : UserControl, IPage
    {
        readonly ContentProject _p;
        readonly ContentType _type;
        readonly Instance _target;
        readonly string _mc, _loader;
        List<ContentFile> _files;
        bool _loaded;

        public ProjectPage(ContentProject p, ContentType type, Instance target, string mc, string loader)
        {
            InitializeComponent();
            _p = p; _type = type; _target = target;
            _mc = string.IsNullOrEmpty(mc) ? null : mc;
            _loader = string.IsNullOrEmpty(loader) ? null : loader;
            PTitle.Text = p.Title;
            PMeta.Text = $"{p.Author} · {p.SourceText} · 下载 {p.DownloadsText} · 更新于 {p.Updated:yyyy-MM-dd}" + (p.Categories?.Count > 0 ? " · " + string.Join(" ", p.Categories.Take(6)) : "");
            PDesc.Text = p.Description;
            TargetInfo.Text = type == ContentType.Modpack ? "整合包会安装成一个新版本" : target != null ? $"安装到 {target.Id}" : "未选择目标版本";
            OnlyMatch.Visibility = _mc == null && _loader == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public async void OnShown()
        {
            if (_loaded) return;
            _loaded = true;
            Icon.Source = await RemoteImage.LoadAsync(_p.IconUrl ?? "", 128);
            await LoadFilesAsync();
        }

        async Task LoadFilesAsync()
        {
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = "加载文件列表…";
            StatusBar.Visibility = Visibility.Visible;
            try
            {
                var prov = ContentService.Get(_p.Source);
                _files = await Task.Run(() => prov.GetFilesAsync(_p.Id, null, null, CancellationToken.None));
                Apply();
            }
            catch (Exception ex)
            {
                StatusText.Text = "加载失败：" + ex.Message;
                StatusBar.Visibility = Visibility.Collapsed;
            }
        }

        void Apply()
        {
            if (_files == null) return;
            IEnumerable<ContentFile> q = _files;
            if (OnlyMatch.IsChecked == true && OnlyMatch.Visibility == Visibility.Visible)
            {
                if (_mc != null) q = q.Where(f => f.GameVersions == null || f.GameVersions.Count == 0 || f.GameVersions.Contains(_mc));
                if (_loader != null && (_type == ContentType.Mod || _type == ContentType.Modpack))
                    q = q.Where(f => f.Loaders == null || f.Loaders.Count == 0 || f.Loaders.Any(l => string.Equals(l, _loader, StringComparison.OrdinalIgnoreCase)));
            }
            var rows = q.Select(f => new FileRow { File = f }).ToList();
            Files.ItemsSource = rows;
            FilesTitle.Text = $"选择要下载的文件（{rows.Count}）";
            if (rows.Count == 0)
            {
                StatusPanel.Visibility = Visibility.Visible;
                StatusBar.Visibility = Visibility.Collapsed;
                StatusText.Text = _files.Count == 0 ? "这个项目没有可下载的文件" : $"没有适配 {_mc} {_loader} 的文件，关掉右上角的筛选看看全部文件";
            }
            else StatusPanel.Visibility = Visibility.Collapsed;
        }

        void OnlyMatch_Click(object sender, RoutedEventArgs e) => Apply();
        void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Back();
        void Web_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_p.WebUrl)) Util.OpenUrl(_p.WebUrl); }

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is FileRow row)) return;
            var file = row.File;

            if (_type == ContentType.Modpack)
            {
                var name = await Dialogs.Prompt("安装整合包", "版本名", Util.SafeFileName(_p.Title));
                if (string.IsNullOrWhiteSpace(name)) return;
                var repo = GameRepo.Current;
                TaskManager.Start($"安装整合包 {_p.Title}", async t =>
                {
                    var id = await ModpackImporter.InstallFromFileAsync(repo, file, name, t);
                    Settings.SelectedVersion = id;
                    TaskManager.OnUi(() => MainWindow.Current.LaunchPage.RefreshVersion());
                });
                MainWindow.Current.ToggleTasks(true);
                return;
            }

            if (_target == null) { await Dialogs.Alert("没有目标版本", "先在上一页的“安装到”里选一个版本（没有的话先去装一个）。"); return; }

            string dir;
            if (_type == ContentType.DataPack)
            {
                var saves = Directory.Exists(_target.SavesDir) ? Directory.GetDirectories(_target.SavesDir).Select(Path.GetFileName).ToList() : new List<string>();
                if (saves.Count == 0) { await Dialogs.Alert("没有存档", "数据包要装进某个存档里。先进游戏创建一个世界吧。"); return; }
                var idx = await Dialogs.Choose("装到哪个存档", "数据包只对选中的世界生效：", saves);
                if (idx < 0) return;
                dir = Path.Combine(_target.SavesDir, saves[idx], "datapacks");
            }
            else dir = ContentService.TargetDir(_target, _type);

            if (_type == ContentType.Mod && file.Loaders != null && file.Loaders.Count > 0 && _target.PrimaryLoader != LoaderType.None)
            {
                var slug = LoaderInfo.Slug(_target.PrimaryLoader);
                if (!file.Loaders.Any(l => string.Equals(l, slug, StringComparison.OrdinalIgnoreCase)) &&
                    !await Dialogs.Confirm("加载器不匹配", $"这个文件是给 {string.Join("/", file.Loaders)} 的，而 {_target.Id} 用的是 {LoaderInfo.Name(_target.PrimaryLoader)}，装了大概率不能用。仍然下载？"))
                    return;
            }

            var mc = _target.McVersion;
            var loader = LoaderInfo.Slug(_target.PrimaryLoader);
            bool deps = _type == ContentType.Mod;
            var title = _p.Title;
            TaskManager.Start($"下载 {title}", async t =>
            {
                Directory.CreateDirectory(dir);
                var installed = await ContentService.InstallAsync(file, dir, deps, mc, loader, t);
                if (installed != null && installed.Count > 1) t.Detail = $"已安装 {installed.Count} 个文件（含前置）";
            });
            Dialogs.Toast($"开始下载 {title}" + (deps ? "（会自动补齐前置 Mod）" : ""));
        }
    }
}
