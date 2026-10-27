using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
{
    public partial class MorePage : UserControl, IPage
    {
        public MorePage()
        {
            InitializeComponent();
            AboutTitle.Text = Branding.DisplayName;
            AboutSub.Text = $"版本 {AppInfo.VersionString} · {Branding.Tagline} · 数据目录 {Paths.DataDir}";
        }

        public void OnShown() { }

        void Repo_Click(object sender, RoutedEventArgs e) => Util.OpenUrl(Branding.RepoUrl);
        void DataDir_Click(object sender, RoutedEventArgs e) => Util.OpenFolder(Paths.DataDir);

        void Update_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.Current.GoTab("settings");
            MainWindow.Current.SettingsPage.ScrollTo("update");
        }

        async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            var f = Pickers.PickFile("选择日志或崩溃报告", "日志|*.log;*.txt|所有文件|*.*");
            if (f == null) return;
            var inst = LaunchPage.SelectedInstance();
            if (inst == null) { await Dialogs.Alert("需要一个版本", "崩溃分析需要知道是哪个版本（用来查看 Mod 列表），先选一个版本。"); return; }
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8); }
            catch (Exception ex) { await Dialogs.Alert("读取失败", ex.Message); return; }
            var r = await Task.Run(() => CrashAnalyzer.Analyze(inst, text, 1, DateTime.MinValue, null));
            var sb = new StringBuilder();
            sb.AppendLine("可能的原因：");
            foreach (var x in r.Reasons) sb.AppendLine("  • " + x);
            if (r.Suggestions.Count > 0) { sb.AppendLine("\n建议："); foreach (var x in r.Suggestions) sb.AppendLine("  • " + x); }
            if (r.SuspectMods.Count > 0) { sb.AppendLine("\n可疑的 Mod："); foreach (var x in r.SuspectMods) sb.AppendLine("  • " + x); }
            if (!string.IsNullOrWhiteSpace(r.KeyLines)) { sb.AppendLine("\n关键日志："); sb.AppendLine(r.KeyLines); }
            await Dialogs.Long("分析结果：" + Path.GetFileName(f), sb.ToString());
        }

        async void LauncherLog_Click(object sender, RoutedEventArgs e)
        {
            var r = await Dialogs.Long("启动器日志（最近 500 行）", Log.RecentText(), new[] { "关闭", "打开日志文件夹" });
            if (r == 1) Util.OpenFolder(Paths.Logs);
        }

        async void Clean_Click(object sender, RoutedEventArgs e)
        {
            long size = 0;
            foreach (var d in new[] { Paths.Temp, Path.Combine(Paths.Cache, "icons"), Path.Combine(Paths.Cache, "skins") })
                try { if (Directory.Exists(d)) size += new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length); } catch { }
            if (!await Dialogs.Confirm("清理", $"将删除临时文件和图标缓存，约 {Util.FormatSize(size)}。游戏文件不受影响。", "清理")) return;
            foreach (var d in new[] { Paths.Temp, Path.Combine(Paths.Cache, "icons"), Path.Combine(Paths.Cache, "skins") }) Util.TryDeleteDir(d);
            Dialogs.Toast("已清理", MainWindow.ToastKind.Ok);
        }

        async void Ping_Click(object sender, RoutedEventArgs e)
        {
            var hosts = new List<(string name, string url)>
            {
                ("Mojang 官方", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"),
            };
            foreach (var g in Mirrors.Rules.GroupBy(r => r.Name))
                hosts.Add((g.Key, g.First().To));
            hosts.Add(("Modrinth 官方", "https://api.modrinth.com/v2/tag/loader"));
            hosts.Add(("CurseForge 官方", "https://api.curseforge.com/"));
            hosts.Add(("Fabric 官方", "https://meta.fabricmc.net/v2/versions/loader"));
            hosts.Add(("NeoForge 官方", "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge"));
            Dialogs.Toast("正在测试，请稍候…");
            var results = await Task.WhenAll(hosts.Select(async h =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (var resp = await Http.Client.GetAsync(h.url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        return $"{h.name,-16} {(int)resp.StatusCode}  {sw.ElapsedMilliseconds} ms";
                }
                catch (Exception ex) { return $"{h.name,-16} 失败  {ex.GetBaseException().Message}"; }
            }));
            await Dialogs.Long("下载源连通性", string.Join("\n", results) + "\n\n（能返回状态码就说明连得上；4xx 对首页地址是正常的）");
        }
    }
}
