using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenLauncher.Accounts;
using OpenLauncher.Content;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
{
    public partial class LaunchPage : UserControl, IPage
    {
        LauncherTask _launchTask;
        GameSession _session;
        readonly StringBuilder _pendingLog = new StringBuilder();
        readonly DispatcherTimer _logTimer;
        bool _latestLoaded;

        public LaunchPage()
        {
            InitializeComponent();
            AccountStore.Changed += () => Dispatcher.BeginInvoke((Action)RefreshAccount);
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += (s, e) => FlushLog();
            var h = DateTime.Now.Hour;
            Greeting.Text = h < 6 ? "夜深了，注意休息" : h < 12 ? "早上好" : h < 14 ? "中午好" : h < 18 ? "下午好" : "晚上好";
        }

        public void OnShown()
        {
            RefreshAccount();
            RefreshVersion();
            if (!_latestLoaded) _ = LoadLatestAsync();
        }

        // ---------- 账号 ----------
        public async void RefreshAccount()
        {
            var acc = AccountStore.Selected;
            if (acc == null)
            {
                PlayerName.Text = "未登录";
                AccountType.Text = "点击下方添加账号";
                Avatar.Source = null;
                AvatarFallback.Visibility = Visibility.Visible;
                return;
            }
            PlayerName.Text = acc.Name;
            AccountType.Text = acc.TypeText + "账号";
            Avatar.Source = null;
            AvatarFallback.Visibility = Visibility.Visible;
            try
            {
                var head = await Skins.GetHeadAsync(acc, 64);
                if (head != null && AccountStore.Selected == acc) { Avatar.Source = head; AvatarFallback.Visibility = Visibility.Collapsed; }
            }
            catch (Exception ex) { Log.Warn("获取头像失败：" + ex.Message); }
        }

        void Accounts_Click(object sender, RoutedEventArgs e) => _ = AccountDialogs.ManageAsync();

        // ---------- 版本 ----------
        public static Instance SelectedInstance()
        {
            var repo = GameRepo.Current;
            var id = Settings.SelectedVersion;
            if (!string.IsNullOrEmpty(id) && repo.HasVersion(id)) return new Instance(repo, id);
            var first = repo.ListInstances().Where(i => i.IsValid).OrderByDescending(i => Directory_LastWrite(i.Dir)).FirstOrDefault();
            if (first != null) Settings.SelectedVersion = first.Id;
            return first;
        }

        static DateTime Directory_LastWrite(string dir)
        {
            try { return System.IO.Directory.GetLastWriteTime(dir); } catch { return DateTime.MinValue; }
        }

        public void RefreshVersion()
        {
            var inst = SelectedInstance();
            if (inst == null)
            {
                LaunchSub.Text = "还没有安装任何版本";
                InstanceCard.Visibility = Visibility.Collapsed;
                return;
            }
            LaunchSub.Text = inst.Id;
            InstanceCard.Visibility = Visibility.Visible;
            InstanceDesc.Text = $"{inst.Id}\n{inst.Description}\n游戏目录：{inst.GameDir}" + (inst.Isolated ? "（已开启版本隔离）" : "");
        }

        void Versions_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Push(new VersionsPage());

        void VersionSettings_Click(object sender, RoutedEventArgs e)
        {
            var inst = SelectedInstance();
            if (inst == null) { Dialogs.Toast("先选择或安装一个版本"); return; }
            MainWindow.Current.Push(new InstancePage(inst));
        }

        void ManageMods_Click(object sender, RoutedEventArgs e)
        {
            var inst = SelectedInstance();
            if (inst != null) MainWindow.Current.Push(new InstancePage(inst, "mods"));
        }

        void DownloadMods_Click(object sender, RoutedEventArgs e)
        {
            var inst = SelectedInstance();
            MainWindow.Current.GoTab("download");
            MainWindow.Current.DownloadPage.OpenContent(ContentType.Mod, inst);
        }

        void OpenGameDir_Click(object sender, RoutedEventArgs e)
        {
            var inst = _session?.Instance ?? SelectedInstance();
            if (inst != null) Util.OpenFolder(inst.GameDir);
        }

        void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Current.GoTab("download");

        async Task LoadLatestAsync()
        {
            try
            {
                var list = await Task.Run(() => VersionList.GetMinecraftAsync());
                _latestLoaded = true;
                var rel = list.FirstOrDefault(v => v.Type == "release");
                var snap = list.FirstOrDefault(v => v.Type == "snapshot" && !v.IsAprilFools);
                var repo = GameRepo.Current;
                if (rel != null)
                {
                    LatestRelease.Content = repo.HasVersion(rel.Id) ? $"最新正式版 {rel.Id}（已安装）" : $"安装最新正式版 {rel.Id}";
                    LatestRelease.Tag = rel.Id;
                    LatestRelease.IsEnabled = !repo.HasVersion(rel.Id);
                    LatestRelease.Visibility = Visibility.Visible;
                }
                if (snap != null && list.IndexOf(snap) < list.IndexOf(rel))
                {
                    LatestSnapshot.Content = $"最新快照 {snap.Id}";
                    LatestSnapshot.Tag = snap.Id;
                    LatestSnapshot.IsEnabled = !repo.HasVersion(snap.Id);
                    LatestSnapshot.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) { Log.Warn("获取最新版本失败：" + ex.Message); }
        }

        void InstallLatest_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((Button)sender).Tag;
            ((Button)sender).IsEnabled = false;
            var repo = GameRepo.Current;
            TaskManager.Start($"安装 {id}", async t =>
            {
                var name = await Installer.InstallAsync(new InstallRequest { Repo = repo, McVersion = id }, t);
                Settings.SelectedVersion = name;
                TaskManager.OnUi(RefreshVersion);
            });
            MainWindow.Current.ToggleTasks(true);
        }

        // ---------- 启动 ----------
        void Launch_Click(object sender, RoutedEventArgs e) => _ = LaunchAsync();

        public async Task LaunchAsync(Instance inst = null, string serverOverride = null)
        {
            if (_launchTask != null && _launchTask.IsActive) return;
            inst = inst ?? SelectedInstance();
            if (inst == null)
            {
                if (await Dialogs.Confirm("还没有版本", "当前游戏目录里没有可启动的版本。现在去下载一个？", "去下载"))
                    MainWindow.Current.GoTab("download");
                return;
            }
            var acc = AccountStore.Selected;
            if (acc == null)
            {
                await AccountDialogs.ManageAsync();
                acc = AccountStore.Selected;
                if (acc == null) return;
            }

            LaunchButton.IsEnabled = false;
            LaunchProgressPanel.Visibility = Visibility.Visible;
            LaunchStep.Text = "准备启动…";
            LaunchBar.Value = 0;

            var tcs = new TaskCompletionSource<GameSession>();
            _launchTask = TaskManager.Start($"启动 {inst.Id}", async t =>
            {
                try { tcs.TrySetResult(await LaunchService.LaunchAsync(inst, acc, t, serverOverride)); }
                catch (Exception ex) { tcs.TrySetException(ex); throw; }
            });
            _launchTask.PropertyChanged += (s, e2) => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_launchTask == null) return;
                LaunchStep.Text = _launchTask.Detail;
                LaunchBar.IsIndeterminate = _launchTask.Indeterminate;
                if (!_launchTask.Indeterminate) LaunchBar.Value = _launchTask.Progress;
            }));

            try
            {
                var session = await tcs.Task;
                AttachSession(session);
            }
            catch (OperationCanceledException) { Dialogs.Toast("已取消启动"); }
            catch (Exception ex)
            {
                await Dialogs.ShowAsync("启动失败", Dialogs.Text(TaskManager.Flatten(ex)), new[] { "知道了" });
            }
            finally
            {
                LaunchButton.IsEnabled = true;
                LaunchProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        void CancelLaunch_Click(object sender, RoutedEventArgs e) => _launchTask?.Cancel();

        void AttachSession(GameSession s)
        {
            _session = s;
            RunningCard.Visibility = Visibility.Visible;
            RunningTitle.Text = $"游戏运行中：{s.Instance.Id}";
            GameLog.Clear();
            lock (_pendingLog) _pendingLog.Clear();
            _logTimer.Start();
            s.Line += line => { lock (_pendingLog) _pendingLog.AppendLine(line); };
            var early = s.Output;
            if (!string.IsNullOrEmpty(early)) GameLog.Text = early + "\n";
            s.WindowAppeared += () => Dispatcher.BeginInvoke((Action)(() =>
            {
                Dialogs.Toast("游戏窗口已打开，玩得开心！", MainWindow.ToastKind.Ok);
                switch (Settings.AfterLaunch)
                {
                    case "minimize": MainWindow.Current.WindowState = WindowState.Minimized; break;
                    case "hide": MainWindow.Current.Hide(); break;
                    case "close": Application.Current.Shutdown(); break;
                }
            }));
            s.Exited += code => Dispatcher.BeginInvoke((Action)(() => OnGameExit(s, code)));
        }

        void FlushLog()
        {
            string add;
            lock (_pendingLog)
            {
                if (_pendingLog.Length == 0) return;
                add = _pendingLog.ToString();
                _pendingLog.Clear();
            }
            GameLog.AppendText(add);
            if (GameLog.Text.Length > 200_000) GameLog.Text = GameLog.Text.Substring(GameLog.Text.Length - 100_000);
            GameLog.ScrollToEnd();
        }

        async void OnGameExit(GameSession s, int code)
        {
            FlushLog();
            _logTimer.Stop();
            if (_session == s) _session = null;
            RunningCard.Visibility = Visibility.Collapsed;
            if (!MainWindow.Current.IsVisible) MainWindow.Current.Show();
            if (MainWindow.Current.WindowState == WindowState.Minimized && (code != 0 || !s.WindowShown)) MainWindow.Current.WindowState = WindowState.Normal;

            if (s.KilledByUser) { Dialogs.Toast("游戏已结束"); return; }
            if (code == 0 && s.WindowShown) return;
            await CrashDialog.ShowAsync(s, code);
        }

        void KillGame_Click(object sender, RoutedEventArgs e) => _session?.Kill();
    }
}
