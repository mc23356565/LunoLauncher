using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenLauncher.Accounts;
using OpenLauncher.Content;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
{
    public class FileEntry
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Info { get; set; }
    }

    public partial class InstancePage : UserControl, IPage
    {
        Instance _inst;
        bool _loading;
        List<LocalMod> _mods = new List<LocalMod>();
        string _filesDir;
        ContentType _filesType;

        public InstancePage(Instance inst, string tab = null)
        {
            InitializeComponent();
            _inst = inst;
            PackName.Text = inst.Id;
            (tab == "mods" ? TabMods : TabOverview).IsChecked = true;
        }

        public void OnShown()
        {
            _inst.Reload();
            Title.Text = _inst.Id;
            SubTitle.Text = _inst.IsValid ? _inst.Description : "版本文件不完整";
            InfoText.Text = BuildInfo();
            TabMods.Visibility = _inst.IsModded ? Visibility.Visible : Visibility.Collapsed;
            if (TabSettings.IsChecked == true) LoadSettings();
            if (TabMods.IsChecked == true) _ = LoadModsAsync();
        }

        string BuildInfo()
        {
            if (!_inst.IsValid) return "读取失败：" + _inst.LoadError;
            var i = _inst.Info;
            return $"Minecraft {i.McVersion}（{(i.Type == "release" ? "正式版" : i.Type)}）\n" +
                   $"加载器：{(_inst.Loaders.Count == 0 ? "无（原版）" : string.Join("，", _inst.Loaders))}\n" +
                   $"需要 Java {i.JavaMajor}\n" +
                   $"版本文件夹：{_inst.Dir}\n" +
                   $"游戏目录：{_inst.GameDir}{(_inst.Isolated ? "（版本隔离）" : "")}";
        }

        void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Back();

        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelOverview == null) return;
            PanelOverview.Visibility = TabOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelSettings.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelMods.Visibility = TabMods.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelExport.Visibility = TabExport.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            bool files = TabResource.IsChecked == true || TabShader.IsChecked == true || TabSaves.IsChecked == true || TabShots.IsChecked == true;
            PanelFiles.Visibility = files ? Visibility.Visible : Visibility.Collapsed;

            if (sender == TabSettings) LoadSettings();
            else if (sender == TabMods) _ = LoadModsAsync();
            else if (sender == TabResource) LoadFiles(_inst.ResourcePacksDir, ContentType.ResourcePack, "资源包：把 .zip 拖进来就能安装");
            else if (sender == TabShader) LoadFiles(_inst.ShaderPacksDir, ContentType.Shader, "光影包：需要 Iris / OptiFine 等光影加载 Mod");
            else if (sender == TabSaves) LoadFiles(_inst.SavesDir, ContentType.DataPack, "存档");
            else if (sender == TabShots) LoadFiles(_inst.ScreenshotsDir, ContentType.Mod, "游戏截图（F2 截图）");
        }

        // ---------- 概览 ----------
        void OpenDir_Click(object sender, RoutedEventArgs e) => Util.OpenFolder(_inst.Dir);
        void OpenGameDir_Click(object sender, RoutedEventArgs e) => Util.OpenFolder(_inst.GameDir);

        void Complete_Click(object sender, RoutedEventArgs e)
        {
            var inst = _inst;
            TaskManager.Start($"补全 {inst.Id}", async t =>
            {
                inst.Reload();
                await GameFiles.CompleteAsync(inst.Repo, inst.Info, t, 0, 1);
            });
            MainWindow.Current.ToggleTasks(true);
        }

        async void ExportScript_Click(object sender, RoutedEventArgs e)
        {
            var acc = AccountStore.Selected;
            if (acc == null) { await Dialogs.Alert("需要账号", "先添加一个账号再导出启动脚本。"); return; }
            var path = Pickers.SaveFile("导出启动脚本", "批处理文件 (*.bat)|*.bat", _inst.Id + ".bat");
            if (path == null) return;
            if (!await Dialogs.Confirm("注意", "启动脚本里会包含账号的访问令牌，不要发给别人。继续导出？")) return;
            var inst = _inst;
            TaskManager.Start("导出启动脚本", async t => await LaunchService.ExportScriptAsync(inst, acc, path, t));
        }

        async void Rename_Click(object sender, RoutedEventArgs e)
        {
            var name = await Dialogs.Prompt("重命名版本", "新的版本名（会同时重命名文件夹和里面的 json / jar）", _inst.Id);
            if (string.IsNullOrWhiteSpace(name) || name == _inst.Id) return;
            name = Util.SafeFileName(name);
            try
            {
                var repo = _inst.Repo;
                if (repo.HasVersion(name) || Directory.Exists(repo.VersionDir(name))) throw new Exception("已经有同名版本了");
                var oldId = _inst.Id;
                var oldDir = _inst.Dir;
                var newDir = repo.VersionDir(name);
                Directory.Move(oldDir, newDir);
                void mv(string ext)
                {
                    var a = Path.Combine(newDir, oldId + ext);
                    if (File.Exists(a)) File.Move(a, Path.Combine(newDir, name + ext));
                }
                mv(".json"); mv(".jar");
                var jsonPath = repo.VersionJson(name);
                var json = JNode.ParseFile(jsonPath);
                json.Set("id", name);
                json.SaveFile(jsonPath);
                Util.TryDeleteDir(Path.Combine(newDir, oldId + "-natives"));
                if (Settings.SelectedVersion == oldId) Settings.SelectedVersion = name;
                _inst = new Instance(repo, name);
                OnShown();
                MainWindow.Current.LaunchPage.RefreshVersion();
                Dialogs.Toast("已重命名", MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { await Dialogs.Alert("重命名失败", ex.Message + "\n（游戏正在运行时不能重命名）"); }
        }

        async void Describe_Click(object sender, RoutedEventArgs e)
        {
            var d = await Dialogs.Prompt("修改描述", "显示在版本列表里的描述", _inst.GetSetting("description"));
            if (d == null) return;
            _inst.SetSetting("description", d);
            OnShown();
        }

        async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!await Dialogs.Confirm("删除版本", $"确定把 {_inst.Id} 放进回收站？{(_inst.Isolated ? "\n这个版本开启了版本隔离，里面的存档和 Mod 也会一起删除！" : "")}", "删除", danger: true)) return;
            try
            {
                RecycleBin.Delete(_inst.Dir);
                if (Settings.SelectedVersion == _inst.Id) Settings.SelectedVersion = null;
                MainWindow.Current.LaunchPage.RefreshVersion();
                Dialogs.Toast("已删除", MainWindow.ToastKind.Ok);
                MainWindow.Current.Back();
            }
            catch (Exception ex) { await Dialogs.Alert("删除失败", ex.Message); }
        }

        // ---------- 设置 ----------
        void LoadSettings()
        {
            _loading = true;
            var s = _inst.Settings;
            Select(Isolation, s["isolation"].Str ?? "");
            Select(MemMode, s["memoryMode"].Str ?? "");
            MemSlider.Maximum = Math.Max(4096, Util.Memory().totalMb / 1024 * 1024);
            MemSlider.Value = s["memoryMb"].IntOr(4096);
            MemText.Text = $"{MemSlider.Value / 1024:0.##} GB";
            MemCustomRow.Visibility = s["memoryMode"].Str == "custom" ? Visibility.Visible : Visibility.Collapsed;
            TitleBox.Text = s["title"].Str ?? "";
            ServerBox.Text = s["server"].Str ?? "";
            VersionTypeBox.Text = s["versionType"].Str ?? "";
            JvmBox.Text = s["jvmArgs"].Str ?? "";
            GameArgsBox.Text = s["gameArgs"].Str ?? "";
            SkipCheck.IsChecked = s["skipFileCheck"].Bool;
            LoadJavaBox(s["java"].Str);
            _loading = false;
        }

        void LoadJavaBox(string selected)
        {
            JavaBox.Items.Clear();
            JavaBox.Items.Add(new ComboBoxItem { Content = "自动选择（推荐）", Tag = "" });
            foreach (var j in JavaManager.Known)
                JavaBox.Items.Add(new ComboBoxItem { Content = j.Display, Tag = j.JavawPath, ToolTip = j.JavawPath });
            if (!string.IsNullOrEmpty(selected) && !JavaManager.Known.Any(j => string.Equals(j.JavawPath, selected, StringComparison.OrdinalIgnoreCase)))
                JavaBox.Items.Add(new ComboBoxItem { Content = selected, Tag = selected });
            Select(JavaBox, selected ?? "");
            if (JavaManager.Known.Count == 0)
                _ = Task.Run(() => JavaManager.ScanAsync()).ContinueWith(_ => Dispatcher.BeginInvoke((Action)(() => { _loading = true; LoadJavaBox(_inst.Settings["java"].Str); _loading = false; })));
        }

        static void Select(ComboBox box, string tag)
        {
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, tag, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0];
        }

        static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var s = _inst.Settings;
            s.Set("isolation", TagOf(Isolation));
            s.Set("memoryMode", TagOf(MemMode));
            s.Set("java", TagOf(JavaBox));
            s.Set("skipFileCheck", SkipCheck.IsChecked == true);
            MemCustomRow.Visibility = TagOf(MemMode) == "custom" ? Visibility.Visible : Visibility.Collapsed;
            _inst.SaveSettings();
            InfoText.Text = BuildInfo();
        }

        void MemSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MemText == null) return;
            MemText.Text = $"{MemSlider.Value / 1024:0.##} GB";
            if (_loading) return;
            _inst.SetSetting("memoryMb", (int)MemSlider.Value);
        }

        void Text_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var s = _inst.Settings;
            s.Set("title", TitleBox.Text.Trim());
            s.Set("server", ServerBox.Text.Trim());
            s.Set("versionType", string.IsNullOrWhiteSpace(VersionTypeBox.Text) ? null : VersionTypeBox.Text.Trim());
            s.Set("jvmArgs", JvmBox.Text.Trim());
            s.Set("gameArgs", GameArgsBox.Text.Trim());
            _inst.SaveSettings();
        }

        void BrowseJava_Click(object sender, RoutedEventArgs e)
        {
            var f = Pickers.PickFile("选择 javaw.exe", "javaw.exe|javaw.exe;java.exe");
            if (f == null) return;
            var info = JavaManager.Inspect(f);
            if (info == null) { Dialogs.Toast("这不是一个可用的 Java", MainWindow.ToastKind.Error); return; }
            JavaManager.AddManual(info.JavawPath);
            _inst.SetSetting("java", info.JavawPath);
            _loading = true; LoadJavaBox(info.JavawPath); _loading = false;
        }

        // ---------- Mod ----------
        async Task LoadModsAsync()
        {
            var dir = _inst.ModsDir;
            ModStats.Text = "读取中…";
            _mods = await Task.Run(() => LocalMods.Scan(dir));
            ApplyModFilter();
        }

        void ApplyModFilter()
        {
            var q = ModSearch.Text.Trim();
            var list = string.IsNullOrEmpty(q) ? _mods : _mods.Where(m =>
                (m.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (m.FileName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (m.ModId ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            ModList.ItemsSource = list;
            int enabled = _mods.Count(m => m.Enabled);
            int upd = _mods.Count(m => m.HasUpdate);
            ModStats.Text = _mods.Count == 0 ? "还没有 Mod，点右上角下载，或把 .jar 拖进下面" : $"共 {_mods.Count} 个，已启用 {enabled} 个" + (upd > 0 ? $"，{upd} 个可更新" : "");
            UpdateAllBtn.Visibility = upd > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void ModSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyModFilter();
        void OpenMods_Click(object sender, RoutedEventArgs e) => Util.OpenFolder(_inst.ModsDir);

        void DownloadMods_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.Current.GoTab("download");
            MainWindow.Current.DownloadPage.OpenContent(ContentType.Mod, _inst);
        }

        async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdatesBtn.IsEnabled = false;
            CheckUpdatesBtn.Content = "检查中…";
            try
            {
                var loader = LoaderInfo.Slug(_inst.PrimaryLoader);
                await LocalMods.CheckUpdatesAsync(_mods, _inst.McVersion, loader, CancellationToken.None);
                ApplyModFilter();
                int n = _mods.Count(m => m.HasUpdate);
                Dialogs.Toast(n == 0 ? "所有 Mod 都是最新的（只能检查 Modrinth 上有的 Mod）" : $"{n} 个 Mod 可以更新", MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { await Dialogs.Alert("检查更新失败", ex.Message); }
            finally { CheckUpdatesBtn.IsEnabled = true; CheckUpdatesBtn.Content = "检查更新"; }
        }

        void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            var list = _mods.Where(m => m.HasUpdate).ToList();
            TaskManager.Start($"更新 {list.Count} 个 Mod", async t =>
            {
                for (int i = 0; i < list.Count; i++)
                {
                    t.Report((double)i / list.Count, $"更新 {list[i].Name}（{i + 1}/{list.Count}）");
                    await LocalMods.UpdateAsync(list[i], t);
                }
                TaskManager.OnUi(() => _ = LoadModsAsync());
            });
            MainWindow.Current.ToggleTasks(true);
        }

        void UpdateMod_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is LocalMod m)) return;
            TaskManager.Start($"更新 {m.Name}", async t =>
            {
                await LocalMods.UpdateAsync(m, t);
                TaskManager.OnUi(() => _ = LoadModsAsync());
            });
        }

        void ToggleMod_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is LocalMod m)) return;
            try { LocalMods.SetEnabled(m, !m.Enabled); }
            catch (Exception ex) { Dialogs.Toast("操作失败：" + ex.Message, MainWindow.ToastKind.Error); }
            ApplyModFilter();
            ModList.Items.Refresh();
        }

        async void DeleteMod_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is LocalMod m)) return;
            if (!await Dialogs.Confirm("删除 Mod", $"把 {m.Name}（{m.FileName}）放进回收站？", "删除", danger: true)) return;
            try { LocalMods.Delete(m); _mods.Remove(m); ApplyModFilter(); }
            catch (Exception ex) { await Dialogs.Alert("删除失败", ex.Message); }
        }

        void ModList_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            Directory.CreateDirectory(_inst.ModsDir);
            int n = 0;
            foreach (var f in files.Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Copy(f, Path.Combine(_inst.ModsDir, Path.GetFileName(f)), true); n++; } catch { }
            }
            Dialogs.Toast($"已添加 {n} 个 Mod", MainWindow.ToastKind.Ok);
            _ = LoadModsAsync();
        }

        // ---------- 文件夹类 ----------
        void LoadFiles(string dir, ContentType type, string hint)
        {
            _filesDir = dir;
            _filesType = type;
            FilesHint.Text = hint;
            FilesDownload.Visibility = TabSaves.IsChecked == true || TabShots.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            var list = new List<FileEntry>();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var d in Directory.GetDirectories(dir))
                        list.Add(new FileEntry { Path = d, Name = Path.GetFileName(d), Info = "文件夹 · " + Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm") });
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var fi = new FileInfo(f);
                        list.Add(new FileEntry { Path = f, Name = fi.Name, Info = Util.FormatSize(fi.Length) + " · " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") });
                    }
                }
            }
            catch (Exception ex) { Log.Warn("读取文件夹失败：" + ex.Message); }
            FileList.ItemsSource = list.OrderByDescending(x => x.Info).ToList();
        }

        void OpenFiles_Click(object sender, RoutedEventArgs e) => Util.OpenFolder(_filesDir);

        void FilesDownload_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.Current.GoTab("download");
            MainWindow.Current.DownloadPage.OpenContent(_filesType, _inst);
        }

        async void DeleteFile_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is FileEntry f)) return;
            if (!await Dialogs.Confirm("删除", $"把 {f.Name} 放进回收站？", "删除", danger: true)) return;
            try { RecycleBin.Delete(f.Path); }
            catch (Exception ex) { await Dialogs.Alert("删除失败", ex.Message); }
            Tab_Checked(TabResource.IsChecked == true ? TabResource : TabShader.IsChecked == true ? TabShader : TabSaves.IsChecked == true ? TabSaves : TabShots, null);
        }

        void FileList_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files) || _filesDir == null) return;
            Directory.CreateDirectory(_filesDir);
            foreach (var f in files)
            {
                try
                {
                    if (File.Exists(f)) File.Copy(f, Path.Combine(_filesDir, Path.GetFileName(f)), true);
                    else if (Directory.Exists(f)) Util.CopyDir(f, Path.Combine(_filesDir, Path.GetFileName(f)));
                }
                catch { }
            }
            LoadFiles(_filesDir, _filesType, FilesHint.Text);
        }

        // ---------- 导出 ----------
        void Export_Click(object sender, RoutedEventArgs e)
        {
            var name = string.IsNullOrWhiteSpace(PackName.Text) ? _inst.Id : PackName.Text.Trim();
            var path = Pickers.SaveFile("导出整合包", "Modrinth 整合包 (*.mrpack)|*.mrpack", Util.SafeFileName(name) + ".mrpack");
            if (path == null) return;
            var inst = _inst;
            var ver = PackVersion.Text.Trim();
            bool saves = PackSaves.IsChecked == true;
            TaskManager.Start("导出整合包 " + name, t => ModpackExporter.ExportMrpackAsync(inst, path, name, ver, saves, t));
            MainWindow.Current.ToggleTasks(true);
        }
    }
}
