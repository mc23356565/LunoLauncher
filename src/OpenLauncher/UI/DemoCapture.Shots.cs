#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenLauncher.Accounts;
using OpenLauncher.Content;
using OpenLauncher.Core;
using OpenLauncher.Game;
using OpenLauncher.UI.Pages;
using OpenLauncher.Update;

namespace OpenLauncher.UI
{
    public static partial class DemoCapture
    {
        static async Task<bool> Until(Func<bool> cond, int timeoutMs = 20000, int poll = 200)
        {
            var end = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < end)
            {
                bool ok; try { ok = cond(); } catch { ok = false; }
                if (ok) return true;
                await Task.Delay(poll);
            }
            Log.Warn("采集：等待超时");
            return false;
        }

        static bool Want(string part) => Script == "all" || Script.Split(',').Contains(part);

        static (string, FrameworkElement) M(string k, FrameworkElement e) => (k, e);

        static void CloseDialog()
        {
            if (W.DialogHost.Content is FrameworkElement fe) W.CloseDialogContent(fe);
        }

        static int ImagesLoaded(DependencyObject root) => FindAll<Image>(root, i => i.Source != null && i.ActualWidth > 30).Count;

        public static async Task RunAsync()
        {
            try
            {
                W.Width = 1100; W.Height = 690;
                Settings.AfterLaunch = "keep";
                await Settle(1200);
                var repo = GameRepo.Current;
                var fabric = new Instance(repo, "1.20.1-Fabric_0.19.5");
                Settings.SelectedVersion = fabric.Id;

                // 账号：离线 Steve + 默认皮肤头像
                var acc = AccountStore.All.FirstOrDefault(a => a.Name == "Steve") ?? OfflineAuth.Create("Steve");
                AccountStore.AddOrUpdate(acc);
                AccountStore.Selected = acc;
                BitmapSource head = null;
                var skin = Path.Combine(OutDir, "steve.png");
                if (File.Exists(skin)) head = Skins.RenderHead(File.ReadAllBytes(skin), 64);
                void avatar()
                {
                    var lp = W.LaunchPage;
                    lp.RefreshAccount();
                    if (head != null) { lp.Avatar.Source = head; lp.AvatarFallback.Visibility = Visibility.Collapsed; }
                }

                // ---------- 启动页 ----------
                if (Want("launch"))
                {
                    W.GoTab("launch");
                    W.LaunchPage.RefreshVersion();
                    await Until(() => W.LaunchPage.LatestRelease.Visibility == Visibility.Visible, 30000);
                    avatar();
                    await Settle(600);
                    avatar();
                    var lp = W.LaunchPage;
                    Snap("launch", M("launch", lp.LaunchButton), M("avatar", (FrameworkElement)lp.Avatar.Parent), M("accounts", ButtonByText(lp, "切换 / 管理账号")),
                        M("versions", ButtonByText(lp, "版本选择")), M("vsettings", ButtonByText(lp, "版本设置")), M("latest", lp.LatestRelease),
                        M("nav_launch", W.NavLaunch), M("nav_download", W.NavDownload), M("nav_settings", W.NavSettings), M("nav_more", W.NavMore),
                        M("tasks", W.TasksButton), M("card_decentral", Find<Border>(lp, b => FindAll<TextBlock>(b).Any(t => t.Text == "这个启动器没有服务器"))));
                }

                // ---------- 账号 ----------
                if (Want("accounts"))
                {
                    W.GoTab("launch"); avatar(); await Settle();
                    _ = AccountDialogs.ManageAsync();
                    await Settle(700);
                    var dh = W.DialogHost;
                    Snap("accounts", M("offline", ButtonByText(dh, "离线")), M("ms", ButtonByText(dh, "微软正版")), M("ygg", ButtonByText(dh, "外置登录")), M("done", ButtonByText(dh, "完成")));
                    Click(ButtonByText(dh, "外置登录"));
                    await Settle(700);
                    Snap("ygg", M("server", Find<TextBox>(W.DialogHost)), M("login", ButtonByText(W.DialogHost, "登录")));
                    Click(ButtonByText(W.DialogHost, "取消"));
                    await Settle(500);
                    Click(ButtonByText(W.DialogHost, "完成"));
                    await Settle(400);
                }

                // ---------- 版本选择 ----------
                if (Want("versions"))
                {
                    W.GoTab("launch");
                    var vp = new VersionsPage();
                    W.Push(vp);
                    await Until(() => FindAll<ListBoxItem>(vp.Versions).Count >= 3, 15000);
                    await Settle(700);
                    var items = FindAll<ListBoxItem>(vp.Versions);
                    Snap("versions", items.Select((it, i) => M("v" + i, (FrameworkElement)it)).Concat(new[] { M("folders", (FrameworkElement)vp.Folders), M("import", ButtonByText(vp, "导入整合包")) }).ToArray());
                    W.Back();
                }

                // ---------- 下载：版本列表 ----------
                if (Want("dllist"))
                {
                    W.GoTab("download");
                    var dp = W.DownloadPage;
                    dp.NavGame.IsChecked = true;
                    var gv = (GameListView)dp.Host.Content;
                    await Until(() => gv.Loading.Visibility == Visibility.Collapsed && gv.List.Items.Count > 5, 40000);
                    await Settle(700);
                    var first = FindAll<ListBoxItem>(gv.List).FirstOrDefault();
                    Snap("dl_list", M("first", first), M("install", first != null ? Find<Button>(first) : null), M("pills", (FrameworkElement)gv.FRelease.Parent), M("nav_game", dp.NavGame), M("nav_mod", dp.NavMod), M("nav_pack", dp.NavPack));
                    var sv = ScrollerOf(gv.List);
                    await Seq("dl_scroll", 24, async i => { sv.ScrollToVerticalOffset(900 * Ease(i / 23.0)); await Settle(60); });
                    sv.ScrollToVerticalOffset(0);
                }

                // ---------- 安装向导：逐个加载器 ----------
                if (Want("install"))
                {
                    var list = await VersionList.GetMinecraftAsync();
                    var mc = list.First(v => v.Id == "1.21.11");
                    W.GoTab("download");
                    var ip = new GameInstallPage(mc);
                    W.Push(ip);
                    await Settle(800);
                    (string, FrameworkElement)[] marks() => new[] { M("p_none", (FrameworkElement)ip.LNone), M("p_fabric", ip.LFabric), M("p_forge", ip.LForge), M("p_neo", ip.LNeo), M("p_quilt", ip.LQuilt),
                        M("combo", ip.LoaderVersions), M("install", ip.InstallButton), M("api", ip.WithApi), M("of", ip.OptiFineRow), M("name", ip.NameBox) };
                    Snap("inst_none", marks());
                    foreach (var (rb, key) in new[] { (ip.LFabric, "fabric"), (ip.LForge, "forge"), (ip.LNeo, "neoforge"), (ip.LQuilt, "quilt") })
                    {
                        rb.IsChecked = true;
                        await Until(() => ip.LoaderVersions.Items.Count > 0 || (ip.LoaderStatus.Text ?? "").Contains("失败") || (ip.LoaderStatus.Text ?? "").Contains("不支持"), 40000);
                        await Settle(500);
                        Snap("inst_" + key, marks());
                    }
                    ip.LForge.IsChecked = true;
                    await Until(() => ip.LoaderVersions.Items.Count > 0, 30000);
                    ip.WithOptiFine.IsChecked = true;
                    ip.GetType().GetMethod("WithOptiFine_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Invoke(ip, new object[] { ip.WithOptiFine, new RoutedEventArgs() });
                    await Until(() => ip.OptiFineVersions.Items.Count > 0 || (ip.OptiFineStatus.Text ?? "").Length > 0 && !(ip.OptiFineStatus.Text ?? "").Contains("获取中"), 40000);
                    await Settle(600);
                    Snap("inst_forge_optifine", marks());
                    ip.LFabric.IsChecked = true;
                    await Until(() => ip.LoaderVersions.Items.Count > 0, 30000);
                    await Settle(500);
                    Snap("inst_fabric_final", marks());
                    W.Back();
                }

                // ---------- 真实下载进度（装 26.3 几秒后取消） ----------
                if (Want("tasks"))
                {
                    W.GoTab("launch"); avatar();
                    var t = TaskManager.Start("安装 26.3", async task => { await Installer.InstallAsync(new InstallRequest { Repo = repo, McVersion = "26.3", Name = "26.3" }, task); });
                    await Until(() => t.Speed > 0, 60000);
                    W.ToggleTasks(true);
                    await Settle(400);
                    await Seq("tasks_dl", 28, async i => { await Task.Delay(450); });
                    t.Cancel();
                    await Until(() => !t.IsActive, 30000);
                    W.ToggleTasks(false);
                    TaskManager.ClearFinished();
                    Util.TryDeleteDir(repo.VersionDir("26.3"));
                }

                // ---------- Mod 搜索 ----------
                ContentProject sodium = null;
                if (Want("mods"))
                {
                    W.GoTab("download");
                    W.DownloadPage.OpenContent(ContentType.Mod, fabric);
                    var cv = (ContentSearchView)W.DownloadPage.Host.Content;
                    await Until(() => cv.Results.Items.Count > 3, 40000);
                    await Until(() => ImagesLoaded(cv.Results) >= 4, 40000);
                    await Settle(800);
                    (string, FrameworkElement)[] cm() => new[] { M("query", (FrameworkElement)cv.Query), M("search", ButtonByText(cv, "搜索")), M("src_mr", cv.SrcModrinth), M("src_cf", cv.SrcCurse),
                        M("ver", cv.GameVersion), M("loader", cv.Loader), M("target", cv.Target), M("r0", FindAll<ListBoxItem>(cv.Results).FirstOrDefault()) };
                    Snap("mod_list", cm());
                    cv.Query.Text = "钠";
                    await Settle(200);
                    Snap("mod_typed", cm());
                    Click(ButtonByText(cv, "搜索"));
                    await Until(() => cv.Results.Items.Count > 0 && cv.Results.Items.Cast<ContentProject>().Any(p => p.Slug == "sodium"), 40000);
                    await Until(() => ImagesLoaded(cv.Results) >= 2, 30000);
                    await Settle(800);
                    Snap("mod_sodium", cm());
                    sodium = cv.Results.Items.Cast<ContentProject>().FirstOrDefault(p => p.Slug == "sodium");

                    if (sodium != null)
                    {
                        var pp = new ProjectPage(sodium, ContentType.Mod, fabric, "1.20.1", "fabric");
                        W.Push(pp);
                        await Until(() => pp.Files.Items.Count > 0, 40000);
                        await Settle(900);
                        var dl = FindAll<Button>(pp.Files, b => (b.Content as string) == "下载").FirstOrDefault();
                        Snap("project", M("download", dl), M("files", (FrameworkElement)pp.Files), M("title", pp.PTitle));
                        if (dl != null)
                        {
                            Click(dl);
                            await Settle(350);
                            Snap("project_click", M("download", dl));
                            var mt = TaskManager.Tasks.FirstOrDefault(x => x.Title.Contains("Sodium"));
                            await Until(() => mt == null || !mt.IsActive, 90000);
                            W.ToggleTasks(true);
                            await Settle(500);
                            Snap("project_done");
                            W.ToggleTasks(false);
                        }
                        W.Back();
                    }
                }

                // ---------- Mod 管理 + 检查更新 ----------
                if (Want("manage"))
                {
                    // 装一个旧版 Sodium，演示“有更新”
                    try
                    {
                        var files = await ContentService.Modrinth.GetFilesAsync("sodium", "1.20.1", "fabric", CancellationToken.None);
                        var old = files.Where(f => f.ReleaseType == "release").Skip(2).FirstOrDefault();
                        if (old != null)
                        {
                            var tt = new LauncherTask();
                            await ContentService.InstallAsync(old, fabric.ModsDir, false, "1.20.1", "fabric", tt);
                        }
                    }
                    catch (Exception ex) { Log.Warn("采集：准备旧版 Sodium 失败 " + ex.Message); }
                    W.GoTab("launch");
                    var ip = new InstancePage(fabric, "mods");
                    W.Push(ip);
                    await Until(() => ip.ModList.Items.Count > 0, 20000);
                    await Settle(900);
                    Snap("mods_list", M("check", ip.CheckUpdatesBtn), M("download", ButtonByText(ip, "下载 Mod")), M("list", (FrameworkElement)ip.ModList), M("tab_mods", ip.TabMods),
                        M("m0", FindAll<ListBoxItem>(ip.ModList).FirstOrDefault()));
                    Click(ip.CheckUpdatesBtn);
                    await Until(() => ip.UpdateAllBtn.Visibility == Visibility.Visible || ip.CheckUpdatesBtn.IsEnabled, 60000);
                    await Until(() => ip.CheckUpdatesBtn.IsEnabled, 60000);
                    await Settle(900);
                    var withUpd = FindAll<ListBoxItem>(ip.ModList).FirstOrDefault(li => (li.DataContext as LocalMod)?.HasUpdate == true);
                    Snap("mods_update", M("check", ip.CheckUpdatesBtn), M("updall", ip.UpdateAllBtn), M("upd", withUpd));
                    // 版本设置页
                    ip.TabSettings.IsChecked = true;
                    await Settle(700);
                    Snap("inst_settings", M("isolation", ip.Isolation), M("java", ip.JavaBox), M("mem", ip.MemMode));
                    W.Back();
                }

                // ---------- 崩溃分析 ----------
                if (Want("crash"))
                {
                    W.GoTab("launch"); avatar(); await Settle();
                    var s = new GameSession { Instance = fabric, StartTime = DateTime.Now, Java = JavaManager.Known.FirstOrDefault() };
                    s.WindowShown = true;
                    foreach (var line in new[]
                    {
                        "[12:03:41] [main/INFO]: Loading Minecraft 1.20.1 with Fabric Loader 0.19.5",
                        "[12:03:44] [main/INFO]: Loading 38 mods:",
                        "[12:03:52] [Render thread/ERROR]: Mixin apply failed fancyhud.mixins.json:client.MixinInGameHud -> net.minecraft.class_329: org.spongepowered.asm.mixin.injection.throwables.InvalidInjectionException: Critical injection failure: @Inject annotation on renderHotbar could not find any targets matching 'renderHotbar'",
                        "org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: An unexpected critical error was encountered",
                        "Caused by: org.spongepowered.asm.mixin.throwables.MixinApplyError: Mixin [fancyhud.mixins.json:client.MixinInGameHud] from phase [DEFAULT] in config [fancyhud.mixins.json] FAILED during APPLY",
                    }) s.AddLine(line);
                    _ = CrashDialog.ShowAsync(s, 1);
                    await Until(() => W.DialogHost.Content != null, 10000);
                    await Settle(900);
                    Snap("crash", M("export", ButtonByText(W.DialogHost, "导出错误报告")), M("log", ButtonByText(W.DialogHost, "查看日志")));
                    CloseDialog();
                    await Settle(300);
                }

                // ---------- 设置 ----------
                if (Want("settings"))
                {
                    W.GoTab("settings");
                    var sp = W.SettingsPage;
                    foreach (var sec in new[] { "launch", "java", "download", "account", "update" })
                    {
                        sp.ScrollTo(sec);
                        await Settle(700);
                        Snap("set_" + sec, M("mirrors", (FrameworkElement)sp.MirrorList), M("keys", (FrameworkElement)sp.KeyList), M("check", sp.CheckUpdateBtn),
                            M("gamesrc", sp.GameSource), M("modsrc", sp.ModSource), M("threads", sp.Threads), M("javalist", (FrameworkElement)sp.JavaList), M("msid", sp.MsClientId));
                    }
                }

                // ---------- 主题色 ----------
                if (Want("theme"))
                {
                    W.GoTab("launch"); avatar(); await Settle();
                    int k = 0;
                    foreach (var (name, hex) in Theme.Presets)
                    {
                        Theme.Apply(hex);
                        await Settle(250);
                        Snap($"theme_{k++:00}");
                    }
                    Theme.Apply(Settings.ThemeColor);
                    W.GoTab("settings");
                    W.SettingsPage.ScrollTo("ui");
                    await Settle(700);
                    Snap("set_ui", M("presets", (FrameworkElement)W.SettingsPage.ThemePresets));
                }

                // ---------- 更新提示 ----------
                if (Want("update"))
                {
                    W.GoTab("launch"); avatar(); await Settle();
                    _ = Dialogs.ShowUpdateAsync(new UpdateInfo
                    {
                        Version = "0.2.0", Size = 768_000, Signer = "12c461c38f08483a", FoundAt = "https://cdn.jsdelivr.net/gh/OpenLauncher-MC/OpenLauncher@main/release/update.json",
                        Notes = "· 新增 EasyTier 联机\n· CurseForge 指纹查更新\n· 修复若干问题", Urls = { "https://example.invalid/OpenLauncher.exe" }, Sha256 = "00",
                    });
                    await Settle(900);
                    Snap("update_dialog", M("ok", ButtonByText(W.DialogHost, "立即更新")));
                    CloseDialog();
                    await Settle(300);
                }

                // ---------- 更多 ----------
                if (Want("more"))
                {
                    W.GoTab("more");
                    await Settle(700);
                    Snap("more");
                }

                // ---------- 整合包（最后做：要真实下载几十个文件） ----------
                if (Want("pack"))
                {
                    W.GoTab("download");
                    W.DownloadPage.OpenContent(ContentType.Modpack, null);
                    var cv = (ContentSearchView)W.DownloadPage.Host.Content;
                    await Until(() => cv.Results.Items.Count > 3, 40000);
                    await Until(() => ImagesLoaded(cv.Results) >= 4, 40000);
                    await Settle(800);
                    Snap("pack_list", M("r0", FindAll<ListBoxItem>(cv.Results).FirstOrDefault()));
                    cv.Query.Text = "Fabulously Optimized";
                    Click(ButtonByText(cv, "搜索"));
                    await Until(() => cv.Results.Items.Cast<ContentProject>().Any(p => p.Slug == "fabulously-optimized"), 40000);
                    await Until(() => ImagesLoaded(cv.Results) >= 1, 20000);
                    await Settle(700);
                    Snap("pack_search", M("r0", FindAll<ListBoxItem>(cv.Results).FirstOrDefault()));
                    var fo = cv.Results.Items.Cast<ContentProject>().FirstOrDefault(p => p.Slug == "fabulously-optimized");
                    if (fo != null)
                    {
                        var pp = new ProjectPage(fo, ContentType.Modpack, null, "1.20.1", null);
                        W.Push(pp);
                        await Until(() => pp.Files.Items.Count > 0, 40000);
                        await Settle(900);
                        var dl = FindAll<Button>(pp.Files, b => (b.Content as string) == "下载").FirstOrDefault();
                        Snap("pack_project", M("download", dl));
                        if (dl != null)
                        {
                            Click(dl);
                            await Settle(800);
                            var box = Find<TextBox>(W.DialogHost);
                            if (box != null) box.Text = "Fabulously Optimized";
                            Snap("pack_name", M("ok", ButtonByText(W.DialogHost, "确定")));
                            Click(ButtonByText(W.DialogHost, "确定"));
                            await Settle(1500);
                            var pt = TaskManager.Tasks.FirstOrDefault(x => x.Title.Contains("整合包"));
                            await Seq("pack_import", 40, async i => { await Task.Delay(1500); });
                            await Until(() => pt == null || !pt.IsActive, 600000);
                            await Settle(600);
                            Snap("pack_done");
                            W.ToggleTasks(false);
                        }
                        W.Back();
                    }
                }

                // ---------- 导出整合包 ----------
                if (Want("export"))
                {
                    W.GoTab("launch");
                    var ip = new InstancePage(fabric);
                    W.Push(ip);
                    ip.TabExport.IsChecked = true;
                    ip.PackName.Text = "我的生存整合包";
                    ip.PackVersion.Text = "1.0.0";
                    await Settle(800);
                    Snap("inst_export", M("export", ButtonByText(ip, "导出…")), M("tab", ip.TabExport), M("name", ip.PackName));
                    W.Back();
                }

                // ---------- 启动游戏（录游戏画面由外部脚本完成） ----------
                if (Want("play"))
                {
                    W.GoTab("launch"); avatar(); await Settle();
                    fabric.SetSetting("width", 1600);
                    fabric.SetSetting("height", 900);
                    _ = W.LaunchPage.LaunchAsync(fabric);
                    await Seq("launching", 30, async i => { await Task.Delay(300); }, () => new[] { M("launch", (FrameworkElement)W.LaunchPage.LaunchButton) });
                    await Until(() => W.LaunchPage.RunningCard.Visibility == Visibility.Visible, 120000);
                    await Seq("running", 40, async i => { await Task.Delay(1200); });
                    File.WriteAllText(Path.Combine(OutDir, "game_started.txt"), DateTime.Now.ToString("O"));
                    // 外部脚本录完游戏画面会写 game_recorded.txt
                    await Until(() => File.Exists(Path.Combine(OutDir, "game_recorded.txt")), 300000, 1000);
                    foreach (var s in LaunchService.Running.ToList()) s.Kill();
                    await Settle(1500);
                }
            }
            catch (Exception ex)
            {
                Log.Error("采集失败", ex);
                File.WriteAllText(Path.Combine(OutDir, "error.txt"), ex.ToString());
            }
            File.WriteAllText(Path.Combine(OutDir, "done.txt"), "ok");
            Application.Current.Shutdown();
        }

        static double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }
}
#endif
