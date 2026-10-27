using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI
{
    public static class CrashDialog
    {
        public static async Task ShowAsync(GameSession s, int code)
        {
            CrashResult r;
            try { r = await Task.Run(() => CrashAnalyzer.Analyze(s.Instance, s.Output, code, s.StartTime, s.Java)); }
            catch (Exception ex)
            {
                Log.Error("崩溃分析失败", ex);
                r = new CrashResult();
                r.Reasons.Add("崩溃分析本身出错了：" + ex.Message);
            }
            if (r.IsUserTriggered) { Dialogs.Toast("检测到手动触发的调试崩溃（F3+C），不是错误"); return; }

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = s.WindowShown ? $"游戏运行中崩溃了（退出码 {code}）" : $"游戏没能启动起来（退出码 {code}）",
                Style = (Style)Application.Current.FindResource("Sub"),
                Margin = new Thickness(0, 0, 0, 10)
            });
            void section(string title, System.Collections.Generic.IEnumerable<string> items, Brush color)
            {
                var list = items?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (list == null || list.Count == 0) return;
                panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = color, Margin = new Thickness(0, 6, 0, 4) });
                foreach (var it in list)
                    panel.Children.Add(new TextBlock { Text = "• " + it, Style = (Style)Application.Current.FindResource("Body"), Margin = new Thickness(4, 0, 0, 2) });
            }
            section("可能的原因", r.Reasons, (Brush)Application.Current.FindResource("Danger"));
            section("建议这样做", r.Suggestions, (Brush)Application.Current.FindResource("Accent"));
            section("可疑的 Mod", r.SuspectMods, (Brush)Application.Current.FindResource("Warn"));

            var choice = await Dialogs.ShowAsync("游戏崩溃了", new ScrollViewer { Content = panel, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                new[] { "关闭", "查看日志", "导出错误报告" }, primary: 2, width: 560);
            if (choice == 1)
            {
                var text = string.IsNullOrWhiteSpace(r.KeyLines) ? s.Output : r.KeyLines + "\n\n========== 完整输出 ==========\n" + s.Output;
                await Dialogs.Long("游戏日志", text.Length > 150_000 ? text.Substring(text.Length - 150_000) : text);
            }
            else if (choice == 2)
            {
                try
                {
                    var path = CrashAnalyzer.ExportReport(s.Instance, s.Output, r, s.Java);
                    Dialogs.Toast("错误报告已保存到桌面", MainWindow.ToastKind.Ok);
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                catch (Exception ex) { await Dialogs.Alert("导出失败", ex.Message); }
            }
        }
    }
}
