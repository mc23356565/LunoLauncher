using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenLauncher.Core;

namespace OpenLauncher.UI.Pages
{
    /// <summary>
    /// 联机页：隧道开关与配置 + 房间列表。
    /// 隧道本身由 EasyTier 提供；这里只做配置、启停、状态显示和一键进服。
    /// </summary>
    public partial class CoopPage : UserControl, IPage
    {
        List<CoopRoom> _rooms = new List<CoopRoom>();

        public CoopPage()
        {
            InitializeComponent();
            LoadConfig();
            RefreshRooms();
            RefreshTunnel();
            RefreshElevation();
        }

        public void OnShown()
        {
            RefreshTunnel();
            RefreshRooms();
            RefreshElevation();
        }

        void RefreshElevation()
        {
            var admin = Tunnel.IsElevated();
            ElevHint.Text = admin
                ? "当前权限：管理员 · 启动隧道不会再弹 UAC"
                : "当前权限：普通用户 · 隧道需要管理员权限；如果点「启动隧道」后系统没弹授权窗口，先点「以管理员身份重启」";
            ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        }

        async void Elevate_Click(object sender, RoutedEventArgs e)
        {
            if (!await Dialogs.Confirm("以管理员身份重启",
                "启动器会关闭，并以管理员身份重新打开。\n\n之后点「启动隧道」不会再提权，停止隧道也能直接杀掉进程。", "重启")) return;
            try
            {
                Tunnel.RestartElevated();
                Application.Current.Shutdown();
            }
            catch (Exception ex) { await Dialogs.Alert("重启失败", ex.Message); }
        }

        // ---------- 配置 ----------

        void LoadConfig()
        {
            NameBox.Text = Tunnel.NetworkName;
            SecretBox.Text = Tunnel.NetworkSecret;
            PeersBox.Text = Tunnel.PeersText;
            ListenBox.Text = Tunnel.ListenPort;
            DirBox.Text = Tunnel.EasyTierDir;
            DhcpCheck.IsChecked = Tunnel.Dhcp;
            LatencyCheck.IsChecked = Tunnel.LatencyFirst;
            BroadcastCheck.IsChecked = Tunnel.BroadcastRelay;
            NodeHint.Text = "已内置 " + Tunnel.FallbackPeers.Length + " 个公共节点，双方必须一致，可自行增删";
        }

        void SaveConfig()
        {
            Tunnel.NetworkName = NameBox.Text.Trim();
            Tunnel.NetworkSecret = SecretBox.Text.Trim();
            Tunnel.PeersText = PeersBox.Text.Trim();
            Tunnel.ListenPort = ListenBox.Text.Trim();
            Tunnel.EasyTierDir = DirBox.Text.Trim();
            Tunnel.Dhcp = DhcpCheck.IsChecked == true;
            Tunnel.LatencyFirst = LatencyCheck.IsChecked == true;
            Tunnel.BroadcastRelay = BroadcastCheck.IsChecked == true;
        }

        // ---------- 刷新 ----------

        void RefreshTunnel() => TunnelState.Text = Tunnel.Summary();

        void RefreshTunnelSoon()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (s, e) => { timer.Stop(); RefreshTunnel(); };
            timer.Start();
        }

        /// <summary>启动/停止之后连着刷新几次，让状态自己跟上（不必手点）。</summary>
        void RefreshTunnelRepeatedly()
        {
            var n = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                n++;
                RefreshTunnel();
                if (n >= 6) timer.Stop();
            };
            timer.Start();
        }

        void RefreshRooms()
        {
            _rooms = Coop.Rooms();
            RoomList.Items.Clear();
            foreach (var r in _rooms) RoomList.Items.Add(r.ToString());
            if (_rooms.Count > 0) RoomList.SelectedIndex = 0;
            RoomHint.Text = _rooms.Count == 0
                ? "房主：正常「启动」游戏 → 选世界 → 对局域网开放，再把 房主虚拟IP:端口 发给朋友。加入方：把这条地址存成房间，点「启动并直连」。"
                : "「启动并直连」是给加入方用的：游戏会跳过主菜单直接进服。房主不要点（房主自己正常启动、选世界、再对局域网开放）。";
        }

        CoopRoom Selected() =>
            RoomList.SelectedIndex >= 0 && RoomList.SelectedIndex < _rooms.Count ? _rooms[RoomList.SelectedIndex] : null;

        // ---------- 隧道 ----------

        void Save_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            Dialogs.Toast("配置已保存", MainWindow.ToastKind.Ok);
            RefreshTunnel();
        }

        void Official_Click(object sender, RoutedEventArgs e) => PeersBox.Text = Tunnel.OfficialNode;

        void StartTunnel_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            TunnelState.Text = "隧道：正在启动…";
            try
            {
                var message = Tunnel.Start();
                if (message.StartsWith("隧道已经") || message.StartsWith("启动 easytier"))
                    _ = Dialogs.Alert("启动隧道", message);
                else
                    Dialogs.Toast("已启动，等几秒看状态", MainWindow.ToastKind.Ok);
                RefreshTunnelRepeatedly();
            }
            catch (Exception ex)
            {
                TunnelState.Text = "隧道：启动失败";
                _ = Dialogs.Alert("启动失败", ex.Message);
            }
        }

        void StopTunnel_Click(object sender, RoutedEventArgs e)
        {
            _ = Dialogs.Alert("停止隧道", Tunnel.Stop());
            RefreshTunnelRepeatedly();
        }

        async void Report_Click(object sender, RoutedEventArgs e)
            => await Dialogs.Long("隧道状态与日志", await Task.Run(() => Tunnel.Report()));

        async void Diagnose_Click(object sender, RoutedEventArgs e)
            => await Dialogs.Long("诊断报告", await Task.Run(() => Tunnel.Diagnose()));

        async void MyAddress_Click(object sender, RoutedEventArgs e)
            => await Dialogs.Long("我的地址（当共享节点用）", await Task.Run(() => Tunnel.MyAddressReport()));

        async void Firewall_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            var port = 11010;
            if (int.TryParse(ListenBox.Text.Trim(), out var p) && p > 0) port = p;
            var text = await Task.Run(() => Tunnel.OpenFirewall(port));
            await Dialogs.Alert("放行防火墙端口 " + port, text);
        }

        async void Peers_Click(object sender, RoutedEventArgs e)
            => await Dialogs.Long("peer 表", await Task.Run(() => Tunnel.PeersTable()));

        void OpenLogs_Click(object sender, RoutedEventArgs e) => Util.OpenFolder(Tunnel.LogDir);

        void Script_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
            try
            {
                var path = Tunnel.WriteScript();
                _ = Dialogs.Alert("启动脚本已生成", path + "\n\n右键它选「以管理员身份运行」即可。");
            }
            catch (Exception ex) { _ = Dialogs.Alert("生成失败", ex.Message); }
        }

        // ---------- 房间 ----------

        async void AddRoom_Click(object sender, RoutedEventArgs e)
        {
            var addr = await Dialogs.Prompt("添加房间", "房主的地址：虚拟IP:端口（例如 10.126.126.1:51234）");
            if (addr == null) return;
            string host;
            int port;
            if (!Coop.TryParseAddress(addr, out host, out port))
            {
                await Dialogs.Alert("地址不对", "格式是 虚拟IP:端口，例如 10.126.126.1:51234。");
                return;
            }
            var name = await Dialogs.Prompt("房间名称", "给这个房间起个名字（留空就用地址）", host);
            if (name == null) return;

            var rooms = Coop.Rooms();
            rooms.Add(new CoopRoom
            {
                Name = string.IsNullOrWhiteSpace(name) ? host : name.Trim(),
                Host = host,
                Port = port,
            });
            Coop.Save(rooms);
            RefreshRooms();
            Dialogs.Toast("已添加 " + host + ":" + port, MainWindow.ToastKind.Ok);
        }

        async void DetectPort_Click(object sender, RoutedEventArgs e)
        {
            var room = Selected();
            if (room == null) { await Dialogs.Alert("先选一个房间", "在列表里点一下要更新的房间。"); return; }

            var inst = LaunchPage.SelectedInstance();
            DateTime written;
            int port = Coop.DetectLanPort(inst, out written);
            if (port == 0)
            {
                await Dialogs.Alert("没读到端口",
                    "没在这个版本的 logs/latest.log 里找到「Local game hosted on port …」。\n\n" +
                    "让房主在游戏里点一次「对局域网开放」，再回来点「读取端口」；\n" +
                    "或者手动把端口改对（游戏聊天栏里会显示）。");
                return;
            }
            room.Port = port;
            Coop.Save(_rooms);
            RefreshRooms();
            Dialogs.Toast($"读到端口 {port}：{room.Address}", MainWindow.ToastKind.Ok);
        }

        async void Copy_Click(object sender, RoutedEventArgs e)
        {
            var room = Selected();
            if (room == null) { await Dialogs.Alert("先选一个房间", "在列表里点一下要复制的房间。"); return; }
            try
            {
                Clipboard.SetText(room.Address);
                Dialogs.Toast("已复制 " + room.Address, MainWindow.ToastKind.Ok);
            }
            catch (Exception ex) { await Dialogs.Alert("复制失败", ex.Message); }
        }

        async void Launch_Click(object sender, RoutedEventArgs e)
        {
            var room = Selected();
            if (room == null) { await Dialogs.Alert("先选一个房间", "在列表里点一下要进的房间。"); return; }
            if (!Tunnel.IsRunning())
            {
                await Dialogs.Alert("隧道没开", "先在上面点「启动隧道」，否则连不到对方。");
                return;
            }
            var inst = LaunchPage.SelectedInstance();
            if (inst == null) { await Dialogs.Alert("先选一个版本", "「启动并直连」要用当前选中的游戏版本。"); return; }

            // 一次性直连：只影响这一次启动，不写回版本设置（否则以后每次「启动」都会自动去连服务器）
            Dialogs.Toast("启动后自动进 " + room.Address, MainWindow.ToastKind.Ok);
            _ = MainWindow.Current.LaunchPage.LaunchAsync(inst, room.Address);
        }

        async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var room = Selected();
            if (room == null) { await Dialogs.Alert("先选一个房间", "在列表里点一下要删除的房间。"); return; }
            if (!await Dialogs.Confirm("删除房间", "删除「" + room.Name + "」？只是删掉这条记录，不影响游戏存档。", "删除", danger: true)) return;

            _rooms.Remove(room);
            Coop.Save(_rooms);
            RefreshRooms();
            Dialogs.Toast("已删除", MainWindow.ToastKind.Ok);
        }
    }
}
