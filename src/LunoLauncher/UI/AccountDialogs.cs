using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LunoLauncher.Accounts;
using LunoLauncher.Core;

namespace LunoLauncher.UI
{
    public static class AccountDialogs
    {
        static Style S(string k) => (Style)Application.Current.FindResource(k);

        public static async Task ManageAsync()
        {
            while (true)
            {
                var panel = new StackPanel();
                var list = new ListBox { Style = S("CardList"), MaxHeight = 260 };
                foreach (var a in AccountStore.All)
                {
                    var row = new Grid { Tag = a };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var info = new StackPanel();
                    info.Children.Add(new TextBlock { Text = a.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
                    info.Children.Add(new TextBlock { Text = a.TypeText + (a.Type == AccountType.Yggdrasil ? " · " + a.LoginName : ""), Style = S("Sub") });
                    row.Children.Add(info);
                    var del = new Button { Style = S("Ghost"), ToolTip = "删除这个账号", Content = new TextBlock { Text = "", Style = S("Icon") } };
                    del.Click += async (s, e) =>
                    {
                        e.Handled = true;
                        if (await Dialogs.Confirm("删除账号", $"确定删除账号 {a.Name}（{a.TypeText}）？", "删除", danger: true))
                        {
                            AccountStore.Remove(a);
                            list.Items.Remove(list.Items.Cast<object>().FirstOrDefault(x => (x as Grid)?.Tag == a));
                        }
                    };
                    Grid.SetColumn(del, 1);
                    row.Children.Add(del);
                    list.Items.Add(row);
                    if (a == AccountStore.Selected) list.SelectedItem = row;
                }
                if (AccountStore.All.Count == 0)
                    panel.Children.Add(new TextBlock
                    {
                        Text = (Branding.AllowOfflineAccount || Branding.AllowThirdPartyLogin)
                            ? "还没有账号。正版需要微软账号；也支持 LittleSkin 等外置登录。"
                            : "还没有账号。请添加微软正版账号。",
                        Style = S("Body"),
                        Margin = new Thickness(0, 0, 0, 10),
                    });
                else panel.Children.Add(list);
                list.SelectionChanged += (s, e) =>
                {
                    if ((list.SelectedItem as Grid)?.Tag is Account sel) AccountStore.Selected = sel;
                };

                panel.Children.Add(new TextBlock { Text = "添加账号", Style = S("Sub"), Margin = new Thickness(0, 12, 0, 6) });

                // 按 Branding.AllowOfflineAccount 决定按钮组合：发行版默认不出现「离线」
                var labels = new System.Collections.Generic.List<string> { "完成" };
                var actions = new System.Collections.Generic.List<string> { "done" };
                if (Branding.AllowOfflineAccount) { labels.Add("离线"); actions.Add("offline"); }
                labels.Add("微软正版"); actions.Add("ms");
                if (Branding.AllowThirdPartyLogin) { labels.Add("外置登录"); actions.Add("ygg"); }

                var r = await Dialogs.ShowAsync("账号", panel, labels.ToArray(), primary: 0, width: 500);
                if (r <= 0 || r >= actions.Count) return;
                switch (actions[r])
                {
                    case "offline": await AddOfflineAsync(); break;
                    case "ms": await AddMicrosoftAsync(); break;
                    case "ygg": await AddYggdrasilAsync(); break;
                    default: return;
                }
            }
        }

        static async Task AddOfflineAsync()
        {
            if (!Branding.AllowOfflineAccount)
            {
                await Dialogs.Alert("这个版本不提供离线账号",
                    "当前发行版关闭了离线账号（不提供绕过正版验证的途径）。\n\n" +
                    "请添加微软正版账号，或使用 LittleSkin / Blessing Skin 等外置登录。");
                return;
            }
            var name = await Dialogs.Prompt("添加离线账号", "玩家名（3~16 个英文字母、数字或下划线）", "", "Steve");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!OfflineAuth.IsValidName(name, out var reason)) { await Dialogs.Alert("名字不行", reason); return; }
            if (!string.IsNullOrEmpty(reason) && !await Dialogs.Confirm("名字可能有问题", reason + "\n\n仍然使用这个名字？")) return;
            var acc = OfflineAuth.Create(name);
            AccountStore.AddOrUpdate(acc);
            AccountStore.Selected = acc;
            Dialogs.Toast($"已添加离线账号 {name}", MainWindow.ToastKind.Ok);
        }

        static async Task AddMicrosoftAsync()
        {
            if (!MicrosoftAuth.HasClientId)
            {
                var go = await Dialogs.ShowAsync("需要先配置微软登录",
                    Dialogs.Text("微软正版登录需要一个“Azure 应用 Client ID”，并且要向 Mojang 申请 Minecraft API 权限。\n\n" +
                                 "本项目不能共用一个会被封的 Client ID，所以由发布者或你自己申请：\n" +
                                 "1. 在 Azure 门户注册应用（个人 Microsoft 账户，允许公共客户端流）\n" +
                                 "2. 在 Mojang 的申请表提交 Client ID，等待审核通过\n" +
                                 "3. 把 Client ID 填到 设置 → 账户\n\n详细步骤见项目文档 docs/微软登录.md。"),
                    new[] { "取消", "去设置" }, primary: 1, width: 520);
                if (go == 1) { MainWindow.Current.GoTab("settings"); MainWindow.Current.SettingsPage.ScrollTo("account"); }
                return;
            }

            var cts = new CancellationTokenSource();
            DeviceCodeInfo info;
            try { info = await MicrosoftAuth.StartAsync(cts.Token); }
            catch (Exception ex) { await Dialogs.Alert("微软登录失败", ex.Message); return; }

            try { Clipboard.SetText(info.UserCode); } catch { }
            Util.OpenUrl(info.VerificationUri);

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "浏览器已经打开微软登录页，输入下面的代码（已复制到剪贴板）：", Style = S("Body") });
            panel.Children.Add(new TextBox { Text = info.UserCode, IsReadOnly = true, FontSize = 28, FontWeight = FontWeights.Bold, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 8), FontFamily = new FontFamily("Consolas") });
            var status = new TextBlock { Text = "等待你在浏览器里完成登录…", Style = S("Sub") };
            panel.Children.Add(status);
            var linkBtn = new Button { Content = "没打开？点这里", Style = S("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 6, 0, 0) };
            linkBtn.Click += (s, e) => Util.OpenUrl(info.VerificationUri);
            panel.Children.Add(linkBtn);

            var dialog = Dialogs.ShowAsync("微软登录", panel, new[] { "取消" }, primary: 0);
            var login = MicrosoftAuth.CompleteAsync(info, cts.Token);
            var done = await Task.WhenAny(dialog, login);
            if (done == dialog) { cts.Cancel(); return; }

            // 登录流程结束，关掉等待框
            CloseTop();
            try
            {
                var acc = await login;
                AccountStore.AddOrUpdate(acc);
                AccountStore.Selected = acc;
                Dialogs.Toast($"已登录 {acc.Name}", MainWindow.ToastKind.Ok);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await Dialogs.Alert("微软登录失败", ex.Message); }
        }

        static async Task AddYggdrasilAsync()
        {
            if (!Branding.AllowThirdPartyLogin)
            {
                await Dialogs.Alert("这个版本只支持微软正版登录",
                    "当前发行版关闭了外置登录（authlib-injector）。\n\n" +
                    "官方审核要求应用必须验证玩家拥有 Minecraft: Java Edition，" +
                    "只接受第三方皮肤站账号的登录方式不满足这一条。");
                return;
            }
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "认证服务器（皮肤站）地址", Style = S("Sub") });
            var server = new TextBox { Text = Settings.Get("account.lastYggdrasil", "littleskin.cn"), Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(server);
            panel.Children.Add(new TextBlock { Text = "邮箱 / 用户名", Style = S("Sub") });
            var user = new TextBox { Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(user);
            panel.Children.Add(new TextBlock { Text = "密码", Style = S("Sub") });
            var pass = new PasswordBox { Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(pass);
            panel.Children.Add(new TextBlock { Text = "使用 authlib-injector，支持 LittleSkin、Blessing Skin 等 Yggdrasil 皮肤站。密码只发给你填的服务器，不会保存。", Style = S("Sub"), Margin = new Thickness(0, 10, 0, 0) });
            user.Loaded += (s, e) => user.Focus();

            if (await Dialogs.ShowAsync("外置登录", panel, new[] { "取消", "登录" }, primary: 1, width: 460) != 1) return;
            try
            {
                var api = await YggdrasilAuth.ResolveApiAsync(server.Text.Trim(), CancellationToken.None);
                Settings.Set("account.lastYggdrasil", server.Text.Trim());
                var accounts = await YggdrasilAuth.LoginAsync(api, user.Text.Trim(), pass.Password, CancellationToken.None);
                if (accounts.Count == 0) { await Dialogs.Alert("登录失败", "这个账号下没有角色，先去皮肤站创建一个角色吧。"); return; }
                var acc = accounts[0];
                if (accounts.Count > 1)
                {
                    var idx = await Dialogs.Choose("选择角色", "这个账号有多个角色：", accounts.Select(a => a.Name).ToList());
                    if (idx < 0) return;
                    acc = accounts[idx];
                    if (string.IsNullOrEmpty(acc.AccessToken) || accounts.Count > 1)
                        acc = await YggdrasilAuth.SelectProfileAsync(acc, acc.Uuid, acc.Name, CancellationToken.None);
                }
                AccountStore.AddOrUpdate(acc);
                AccountStore.Selected = acc;
                Dialogs.Toast($"已登录 {acc.Name}（{acc.TypeText}）", MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { await Dialogs.Alert("外置登录失败", ex.Message); }
        }

        static void CloseTop()
        {
            if (MainWindow.Current.DialogHost.Content is FrameworkElement fe) MainWindow.Current.CloseDialogContent(fe);
        }
    }
}
