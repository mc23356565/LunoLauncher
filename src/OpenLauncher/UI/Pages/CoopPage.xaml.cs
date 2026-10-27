using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenLauncher.Core;

namespace OpenLauncher.UI.Pages
{
    /// <summary>
    /// 联机页：以「房间」为中心。
    /// 创建房间 / 加入房间 会背后自动把隧道配好并启动，用户只需要关心两件事：
    /// 我这个房间的地址是什么，房间里现在有谁。
    /// </summary>
    public partial class CoopPage : UserControl, IPage
    {
        CoopRoom _current;           // 当前打开的房间（等于 Coop.Active）
        DispatcherTimer _peerTimer;  // 房间开着时定时刷新成员

        public CoopPage()
        {
            InitializeComponent();
            RefreshRooms();
            RefreshElevation();

            // 上次没关的房间，进来直接恢复（隧道不自动重启，由用户点一下）
            var lastName = Coop.ActiveName;
            if (!string.IsNullOrEmpty(lastName))
            {
                var room = Coop.Rooms().FirstOrDefault(r => r.Name == lastName);
                if (room != null) OpenRoom(room, autoStart: false);
            }
            UpdateView();
        }

        public void OnShown()
        {
            RefreshElevation();
            UpdateView();
            if (_current != null) RefreshPeersAsync();
        }

        static Style S(string key) => (Style)Application.Current.FindResource(key);

        // ================= 视图 =================

        void UpdateView()
        {
            var has = _current != null;
            RoomCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            // 没有房间时才显示引导卡；已经有房间就只看列表，避免按钮重复
            EmptyCard.Visibility = (!has && RoomList.Items.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
            if (!has)
            {
                StopPeerTimer();
                return;
            }

            RoomTitle.Text = _current.Name;
            RoomSub.Text = _current.IsHost
                ? "你是房主：先启动隧道，然后正常开游戏、选世界、对局域网开放，再点「读取端口」。"
                : "你是加入方：隧道连上后点「启动游戏」，会直接进房主的世界。";

            var running = Tunnel.IsRunning();
            var ip = running ? Tunnel.VirtualIp() : "";
            TunnelState.Text = running
                ? (string.IsNullOrEmpty(ip) ? "隧道：已启动，正在获取虚拟 IP…" : "隧道：已连接 · 我的虚拟 IP " + ip)
                : "隧道：未启动";

            if (_current.IsHost)
            {
                AddrLine.Text = _current.Port > 0
                    ? "房间地址（发给朋友）：" + (string.IsNullOrEmpty(ip) ? "（等虚拟 IP）" : ip) + ":" + _current.Port
                    : "房间地址：还没读到端口 —— 游戏里点一次「对局域网开放」，再点「读取端口」。";
                DetectButton.Visibility = Visibility.Visible;
                LaunchButton.Content = "启动游戏";
            }
            else
            {
                AddrLine.Text = string.IsNullOrEmpty(_current.Address)
                    ? "房主地址：还没填 —— 点「房间设置」填房主的虚拟IP:端口。"
                    : "房主地址：" + _current.Address;
                DetectButton.Visibility = Visibility.Collapsed;
                LaunchButton.Content = "启动并进服";
            }

            TunnelStartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            TunnelStopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (!running) PeerList.Visibility = Visibility.Collapsed;

            PeerHint.Text = running
                ? "成员来自 easytier-cli peer；刚连上时可能要等几秒才有数据。"
                : "隧道没开时看不到成员。";
        }

        void RefreshElevation()
        {
            var admin = Tunnel.IsElevated();
            ElevHint.Text = admin
                ? "当前权限：管理员 · 启动隧道不会再弹授权窗口"
                : "当前权限：普通用户 · 启动隧道需要授权；如果点了没反应，先点「以管理员身份重启」";
            ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        }

        void RefreshRooms()
        {
            RoomList.Items.Clear();
            foreach (var r in Coop.Rooms()) RoomList.Items.Add(r.ToString());
            RoomList.Visibility = RoomList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        CoopRoom SelectedRoom()
        {
            var rooms = Coop.Rooms();
            var i = RoomList.SelectedIndex;
            return i >= 0 && i < rooms.Count ? rooms[i] : null;
        }

        // ================= 打开 / 关闭房间 =================

        void OpenRoom(CoopRoom room, bool autoStart)
        {
            _current = room;
            Coop.Active = room;
            Coop.ActiveName = room.Name;
            Coop.ApplyToTunnel(room);
            UpdateView();
            RefreshPeersAsync();
            if (autoStart) StartTunnelForRoom();
            else StartPeerTimer();
        }

        async void StartTunnelForRoom()
        {
            TunnelState.Text = "隧道：正在启动…";
            try
            {
                var message = Tunnel.Start();
                if (message.StartsWith("隧道已经") || message.StartsWith("启动 easytier"))
                    await Dialogs.Alert("启动隧道", message);
                StartPeerTimer();
                RefreshTunnelRepeatedly();
            }
            catch (Exception ex)
            {
                TunnelState.Text = "隧道：启动失败";
                await Dialogs.Alert("启动失败", ex.Message + "\n\n可以先点「诊断」看看缺什么。");
            }
        }

        void StartTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            Coop.ApplyToTunnel(_current);
            StartTunnelForRoom();
        }

        void StopTunnel_Click(object sender, RoutedEventArgs e)
        {
            _ = Task.Run(() => Tunnel.Stop());
            PeerList.Items.Clear();
            PeerList.Visibility = Visibility.Collapsed;
            UpdateView();
            Dialogs.Toast("隧道已停止", MainWindow.ToastKind.Ok);
        }

        void RefreshTunnelRepeatedly()
        {
            var n = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                n++;
                UpdateView();
                RefreshPeersAsync();
                if (n >= 6) timer.Stop();
            };
            timer.Start();
        }

        async void CloseRoom_Click(object sender, RoutedEventArgs e)
        {
            if (!await Dialogs.Confirm("关闭房间", "会停止隧道并退出房间（房间配置仍然保存）。", "关闭")) return;
            _ = Task.Run(() => Tunnel.Stop());
            _current = null;
            Coop.Active = null;
            Coop.ActiveName = "";
            PeerList.Items.Clear();
            StopPeerTimer();
            UpdateView();
            RefreshRooms();
        }

        async void Open_Click(object sender, RoutedEventArgs e)
        {
            var room = SelectedRoom();
            if (room == null) { await Dialogs.Alert("先选一个房间", "在列表里点一下要进入的房间。"); return; }
            OpenRoom(room, autoStart: true);
        }

        async void RoomList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var room = SelectedRoom();
            if (room == null) return;
            OpenRoom(room, autoStart: true);
        }

        async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var room = SelectedRoom();
            if (room == null) { await Dialogs.Alert("先选一个房间", "在列表里点一下要删除的房间。"); return; }
            if (!await Dialogs.Confirm("删除房间", "删除「" + room.Name + "」？房间配置会一起删掉，不影响游戏存档。", "删除", danger: true)) return;
            Coop.Remove(room.Name);
            if (_current != null && _current.Name == room.Name) { _current = null; Coop.Active = null; Coop.ActiveName = ""; UpdateView(); }
            RefreshRooms();
            Dialogs.Toast("已删除", MainWindow.ToastKind.Ok);
        }

        // ================= 创建房间 =================

        async void Create_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel { Width = 460 };

            panel.Children.Add(new TextBlock { Text = "房间名", Style = S("Sub") });
            var nameBox = new TextBox { Text = "我的世界", Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock { Text = "网络名（同一房间的成员必须一致）", Style = S("Sub") });
            var netBox = new TextBox { Text = Coop.NewNetworkName(), Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(netBox);

            panel.Children.Add(new TextBlock { Text = "网络密钥（相当于房间密码）", Style = S("Sub") });
            var secretBox = new TextBox { Text = Coop.NewSecret(), Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(secretBox);

            panel.Children.Add(new TextBlock { Text = "公共节点（一行一个，留空用内置默认）", Style = S("Sub") });
            var peersBox = new TextBox
            {
                Text = Tunnel.PeersText,
                AcceptsReturn = true,
                Height = 70,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 10),
            };
            panel.Children.Add(peersBox);

            panel.Children.Add(new TextBlock
            {
                Style = S("Sub"),
                TextWrapping = TextWrapping.Wrap,
                Text = "创建后会自动启动隧道。等游戏里「对局域网开放」之后，点「读取端口」，再把「复制邀请信息」发给朋友。",
            });

            if (await Dialogs.ShowAsync("创建房间", panel, new[] { "取消", "创建" }, primary: 1, width: 520) != 1) return;

            var room = new CoopRoom
            {
                Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "我的房间" : nameBox.Text.Trim(),
                Role = CoopRole.Host,
                Network = netBox.Text.Trim(),
                Secret = secretBox.Text.Trim(),
                Peers = peersBox.Text.Trim(),
            };
            Coop.AddOrUpdate(room);
            RefreshRooms();
            OpenRoom(room, autoStart: true);
            Dialogs.Toast("房间已创建，正在建立隧道", MainWindow.ToastKind.Ok);
        }

        // ================= 加入房间 =================

        async void Join_Click(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel { Width = 460 };

            panel.Children.Add(new TextBlock { Text = "把房主发来的邀请信息粘贴到这里（会自动识别）", Style = S("Sub") });
            var inviteBox = new TextBox
            {
                AcceptsReturn = true,
                Height = 110,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 10),
            };
            panel.Children.Add(inviteBox);

            panel.Children.Add(new TextBlock { Text = "或者手动填：房主地址（虚拟IP:端口）", Style = S("Sub") });
            var addrBox = new TextBox { Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(addrBox);

            panel.Children.Add(new TextBlock { Text = "房间名（可选）", Style = S("Sub") });
            var nameBox = new TextBox { Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(nameBox);

            // 粘贴后实时解析
            void parse()
            {
                var r = Coop.ParseInvite(inviteBox.Text);
                if (r == null) return;
                if (!string.IsNullOrEmpty(r.Address) && string.IsNullOrWhiteSpace(addrBox.Text)) addrBox.Text = r.Address;
                if (string.IsNullOrWhiteSpace(nameBox.Text)) nameBox.Text = r.Name;
            }
            inviteBox.TextChanged += (s, a) => parse();

            if (await Dialogs.ShowAsync("加入房间", panel, new[] { "取消", "加入" }, primary: 1, width: 520) != 1) return;

            var parsed = Coop.ParseInvite(inviteBox.Text) ?? new CoopRoom { Role = CoopRole.Guest };
            string host; int port;
            var manual = addrBox.Text.Trim();
            if (manual.Length > 0 && Coop.TryParseAddress(manual, out host, out port))
            {
                parsed.HostIp = host;
                parsed.Port = port;
            }
            if (string.IsNullOrEmpty(parsed.HostIp))
            {
                await Dialogs.Alert("缺少房主地址", "邀请信息里没有房主地址，手动填一个：房主的虚拟IP:端口。\n\n（房主发布地址前需要先在游戏里「对局域网开放」并「读取端口」）");
                return;
            }
            parsed.Role = CoopRole.Guest;
            if (!string.IsNullOrWhiteSpace(nameBox.Text)) parsed.Name = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(parsed.Name)) parsed.Name = parsed.HostIp;

            Coop.AddOrUpdate(parsed);
            RefreshRooms();
            OpenRoom(parsed, autoStart: true);
            Dialogs.Toast("已加入房间，正在建立隧道", MainWindow.ToastKind.Ok);
        }

        // ================= 房间里能做的事 =================

        async void Launch_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var inst = LaunchPage.SelectedInstance();
            if (inst == null) { await Dialogs.Alert("先选一个版本", "回「启动」页选一个已安装的版本。"); return; }

            if (!Tunnel.IsRunning())
            {
                await Dialogs.Alert("隧道没开", "房间的隧道还没启动，先点「进入选中房间」或重新打开房间。");
                return;
            }

            if (_current.IsHost)
            {
                Dialogs.Toast("启动游戏，记得进去后「对局域网开放」", MainWindow.ToastKind.Ok);
                _ = MainWindow.Current.LaunchPage.LaunchAsync(inst);
            }
            else
            {
                if (string.IsNullOrEmpty(_current.Address))
                {
                    await Dialogs.Alert("还没有房主地址", "点「房间设置」填房主的虚拟IP:端口。");
                    return;
                }
                Dialogs.Toast("启动后自动进入 " + _current.Address, MainWindow.ToastKind.Ok);
                _ = MainWindow.Current.LaunchPage.LaunchAsync(inst, _current.Address);
            }
        }

        async void Detect_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || !_current.IsHost) return;
            var inst = LaunchPage.SelectedInstance();
            DateTime written;
            int port = Coop.DetectLanPort(inst, out written);
            if (port == 0)
            {
                await Dialogs.Alert("没读到端口",
                    "没在这个版本的 logs/latest.log 里找到「Local game hosted on port …」。\n\n" +
                    "确认游戏里真的点了「对局域网开放」（聊天栏会显示端口），再点一次这个按钮。");
                return;
            }
            _current.Port = port;
            Coop.AddOrUpdate(_current);
            UpdateView();
            RefreshRooms();
            Dialogs.Toast("读到端口 " + port, MainWindow.ToastKind.Ok);
        }

        async void Invite_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var ip = Tunnel.IsRunning() ? Tunnel.VirtualIp() : "";
            if (_current.IsHost && !string.IsNullOrEmpty(ip)) _current.HostIp = ip;
            var text = Coop.InviteText(_current);
            try
            {
                Clipboard.SetText(text);
                await Dialogs.Long("邀请信息（已复制到剪贴板，发给朋友即可）", text);
            }
            catch (Exception ex) { await Dialogs.Alert("复制失败", ex.Message); }
        }

        async void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var ip = Tunnel.IsRunning() ? Tunnel.VirtualIp() : _current.HostIp;
            if (_current.IsHost && !string.IsNullOrEmpty(ip)) _current.HostIp = ip;
            try
            {
                Clipboard.SetText(_current.Address);
                Dialogs.Toast("已复制 " + _current.Address, MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { await Dialogs.Alert("复制失败", ex.Message); }
        }

        void RefreshPeers_Click(object sender, RoutedEventArgs e) => RefreshPeersAsync();

        async void RefreshPeersAsync()
        {
            if (_current == null) return;
            if (!Tunnel.IsRunning())
            {
                PeerList.Items.Clear();
                return;
            }
            var list = await Task.Run(() => Tunnel.LivePeers());
            if (_current == null) return;
            PeerList.Items.Clear();
            foreach (var p in list) PeerList.Items.Add(p.ToString());
            PeerList.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PeerHint.Text = list.Count == 0
                ? "还没看到其他成员。两个人都要启动同一房间的隧道，且网络名、密钥、节点一致。"
                : "房间成员：" + list.Count + " 个";
        }

        void StartPeerTimer()
        {
            StopPeerTimer();
            _peerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _peerTimer.Tick += (s, e) => RefreshPeersAsync();
            _peerTimer.Start();
        }

        void StopPeerTimer()
        {
            _peerTimer?.Stop();
            _peerTimer = null;
        }

        // ================= 房间设置 =================

        async void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var panel = new StackPanel { Width = 460 };

            panel.Children.Add(new TextBlock { Text = "房间名", Style = S("Sub") });
            var nameBox = new TextBox { Text = _current.Name, Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock { Text = "网络名（和房主保持一致）", Style = S("Sub") });
            var netBox = new TextBox { Text = _current.Network, Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(netBox);

            panel.Children.Add(new TextBlock { Text = "网络密钥", Style = S("Sub") });
            var secretBox = new TextBox { Text = _current.Secret, Margin = new Thickness(0, 4, 0, 10) };
            panel.Children.Add(secretBox);

            panel.Children.Add(new TextBlock { Text = "公共节点（一行一个）", Style = S("Sub") });
            var peersBox = new TextBox
            {
                Text = _current.Peers,
                AcceptsReturn = true,
                Height = 70,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 10),
            };
            panel.Children.Add(peersBox);

            panel.Children.Add(new TextBlock { Text = _current.IsHost ? "局域网端口（对局域网开放后点读取）" : "房主地址（虚拟IP:端口）", Style = S("Sub") });
            var addrBox = new TextBox
            {
                Text = _current.IsHost ? _current.Port.ToString() : _current.Address,
                Margin = new Thickness(0, 4, 0, 0),
            };
            panel.Children.Add(addrBox);

            if (await Dialogs.ShowAsync("房间设置", panel, new[] { "取消", "保存" }, primary: 1, width: 520) != 1) return;

            _current.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? _current.Name : nameBox.Text.Trim();
            _current.Network = netBox.Text.Trim();
            _current.Secret = secretBox.Text.Trim();
            _current.Peers = peersBox.Text.Trim();

            var v = addrBox.Text.Trim();
            if (_current.IsHost)
            {
                int p; if (int.TryParse(v, out p) && p > 0 && p <= 65535) _current.Port = p;
            }
            else
            {
                string h; int p;
                if (Coop.TryParseAddress(v, out h, out p)) { _current.HostIp = h; _current.Port = p; }
            }

            Coop.AddOrUpdate(_current);
            Coop.ActiveName = _current.Name;
            Coop.ApplyToTunnel(_current);
            RefreshRooms();
            UpdateView();
            Dialogs.Toast("已保存。改动网络名 / 密钥 / 节点后需要重开房间才生效", MainWindow.ToastKind.Ok);
        }

        // ================= 杂项 =================

        async void Elevate_Click(object sender, RoutedEventArgs e)
        {
            if (!await Dialogs.Confirm("以管理员身份重启",
                "启动器会关闭并以管理员身份重新打开。之后启动隧道不会再弹授权，停止隧道也能直接生效。", "重启")) return;
            try
            {
                Tunnel.RestartElevated();
                Application.Current.Shutdown();
            }
            catch (Exception ex) { await Dialogs.Alert("重启失败", ex.Message); }
        }

        async void Diagnose_Click(object sender, RoutedEventArgs e)
            => await Dialogs.Long("诊断报告", await Task.Run(() => Tunnel.Diagnose()));

        async void Report_Click(object sender, RoutedEventArgs e)
            => await Dialogs.Long("隧道状态与日志", await Task.Run(() => Tunnel.Report()));
    }
}
