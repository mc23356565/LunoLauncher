using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenLauncher.Game;

namespace OpenLauncher.Core
{
    /// <summary>房间角色：房主（开世界的一方）或加入方。</summary>
    public enum CoopRole { Host = 0, Guest = 1 }

    /// <summary>隧道里的一个成员（房间在线成员的一行）。</summary>
    public class TunnelPeer
    {
        public string Ipv4 = "";
        public string Hostname = "";
        public string Latency = "";
        public string Tunnel = "";

        public bool IsSelf => Ipv4 == "10.126.126.1" || Hostname.IndexOf("self", StringComparison.OrdinalIgnoreCase) >= 0;

        public override string ToString()
        {
            var parts = new List<string> { Ipv4, Hostname };
            if (Latency.Length > 0) parts.Add(Latency);
            if (Tunnel.Length > 0) parts.Add(Tunnel.ToUpperInvariant());
            return string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    /// <summary>
    /// 一个联机房间。房间自己带着"怎么连"的全部信息：
    /// 隧道配置（网络名 / 密钥 / 节点）和游戏地址（房主虚拟 IP + 端口）。
    /// 用户只需要"创建房间"或"加入房间"，隧道在背后自动完成。
    /// </summary>
    public class CoopRoom
    {
        public string Name = "我的房间";
        public CoopRole Role = CoopRole.Host;

        // ---- 隧道配置（同一个房间的成员必须完全一致）----
        public string Network = "";
        public string Secret = "";
        public string Peers = "";

        // ---- 游戏地址 ----
        /// <summary>房主的虚拟 IP。加入方必填；房主留空（启动后自动填自己的）。</summary>
        public string HostIp = "";
        /// <summary>房主那边的端口：通常是"对局域网开放"的端口，专用服务器则是 server-port。</summary>
        public int Port;

        public bool IsHost => Role == CoopRole.Host;
        public string RoleText => IsHost ? "房主" : "加入";

        public string Address => Port > 0 ? HostIp + ":" + Port : (HostIp ?? "");

        public override string ToString()
        {
            var addr = string.IsNullOrEmpty(Address) ? "（地址待定）" : Address;
            return Name + "  ·  " + RoleText + "  ·  " + addr;
        }

        // 存储格式：name|role|network|secret|hostIp|port|peers（peers 用 ; 分隔）
        public string ToLine() => string.Join("|", new[]
        {
            Clean(Name), ((int)Role).ToString(), Clean(Network), Clean(Secret),
            Clean(HostIp), Port.ToString(), Clean(Peers).Replace("\n", ";")
        });

        public static CoopRoom Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            var p = line.Split('|');

            // 兼容旧格式：name|host|port
            if (p.Length == 3)
            {
                int oldPort;
                int.TryParse(p[2].Trim(), out oldPort);
                var oldHost = p[1].Trim();
                if (oldHost.Length == 0) return null;
                return new CoopRoom
                {
                    Name = p[0].Trim().Length == 0 ? oldHost : p[0].Trim(),
                    Role = CoopRole.Guest,
                    HostIp = oldHost,
                    Port = oldPort,
                };
            }

            if (p.Length < 7) return null;
            int role, port;
            int.TryParse(p[1].Trim(), out role);
            int.TryParse(p[5].Trim(), out port);
            var host = p[4].Trim();
            var name = p[0].Trim();
            return new CoopRoom
            {
                Name = name.Length == 0 ? "未命名房间" : name,
                Role = role == 0 ? CoopRole.Host : CoopRole.Guest,
                Network = p[2].Trim(),
                Secret = p[3].Trim(),
                HostIp = host,
                Port = port,
                Peers = p[6].Trim().Replace(";", "\n"),
            };
        }

        static string Clean(string s) => (s ?? "").Replace('|', '/').Replace("\r", " ").Trim();
    }

    /// <summary>
    /// 联机：房间的存取、邀请信息的生成与解析、从游戏日志里读端口。
    /// 隧道（EasyTier）的启停不在这里，房间只负责"把参数准备齐"。
    /// </summary>
    public static class Coop
    {
        const string Key = "coop.rooms";
        const string ActiveKey = "coop.active";

        // 1.13+ 开局域网：Local game hosted on port 51234
        // 1.12 及更早：  Started serving on 51234
        static readonly Regex PortPattern = new Regex(
            @"Local game hosted on port (\d+)|Started serving on (\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>当前正在使用的房间（运行时状态，null 表示没开）。</summary>
        public static CoopRoom Active;

        public static List<CoopRoom> Rooms()
        {
            var list = new List<CoopRoom>();
            foreach (var line in Settings.ExtraList(Key))
            {
                var r = CoopRoom.Parse(line);
                if (r != null) list.Add(r);
            }
            return list;
        }

        public static void Save(IEnumerable<CoopRoom> rooms)
            => Settings.SetList(Key, rooms.Select(r => r.ToLine()).ToList());

        public static void AddOrUpdate(CoopRoom room)
        {
            var rooms = Rooms();
            var i = rooms.FindIndex(r => string.Equals(r.Name, room.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) rooms[i] = room; else rooms.Add(room);
            Save(rooms);
        }

        public static void Remove(string name)
        {
            var rooms = Rooms().Where(r => !string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            Save(rooms);
        }

        public static string ActiveName
        {
            get => Settings.Get(ActiveKey, "");
            set => Settings.Set(ActiveKey, value);
        }

        /// <summary>把房间的隧道参数写进 Tunnel 配置（启动隧道前调用）。</summary>
        public static void ApplyToTunnel(CoopRoom room)
        {
            if (room == null) return;
            if (!string.IsNullOrWhiteSpace(room.Network)) Tunnel.NetworkName = room.Network.Trim();
            if (!string.IsNullOrWhiteSpace(room.Secret)) Tunnel.NetworkSecret = room.Secret.Trim();
            if (!string.IsNullOrWhiteSpace(room.Peers)) Tunnel.PeersText = room.Peers.Trim();
        }

        /// <summary>把 Tunnel 当前的配置回填到房间（改完配置再保存时用）。</summary>
        public static void ReadFromTunnel(CoopRoom room)
        {
            if (room == null) return;
            room.Network = Tunnel.NetworkName;
            room.Secret = Tunnel.NetworkSecret;
            room.Peers = Tunnel.PeersText;
        }

        /// <summary>发给朋友的邀请信息：一段文本，对方粘进去就能进同一个房间。</summary>
        public static string InviteText(CoopRoom room)
        {
            if (room == null) return "";
            var sb = new StringBuilder();
            sb.AppendLine("LunoLauncher 联机邀请");
            sb.AppendLine("房间: " + room.Name);
            sb.AppendLine("网络: " + room.Network);
            sb.AppendLine("密钥: " + room.Secret);
            sb.AppendLine("节点:");
            foreach (var p in SplitPeers(room.Peers)) sb.AppendLine("  " + p);
            sb.AppendLine("房主地址: " + (string.IsNullOrEmpty(room.Address) ? "（房主启动后填入自己的虚拟IP:端口）" : room.Address));
            return sb.ToString().TrimEnd();
        }

        /// <summary>解析邀请信息（对方粘过来的那段文本）。</summary>
        public static CoopRoom ParseInvite(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var room = new CoopRoom { Role = CoopRole.Guest };
            var peers = new List<string>();
            bool inPeers = false;

            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                int c = line.IndexOf(':');
                var key = c > 0 ? line.Substring(0, c).Trim() : line;
                var val = c > 0 ? line.Substring(c + 1).Trim() : "";

                inPeers = false;
                if (key == "房间" || key.Equals("room", StringComparison.OrdinalIgnoreCase)) room.Name = val;
                else if (key == "网络" || key.Equals("network", StringComparison.OrdinalIgnoreCase)) room.Network = val;
                else if (key == "密钥" || key.Equals("secret", StringComparison.OrdinalIgnoreCase)) room.Secret = val;
                else if (key == "房主地址" || key.Equals("host", StringComparison.OrdinalIgnoreCase)) room.HostIp = val;
                else if (key == "节点" || key.Equals("peers", StringComparison.OrdinalIgnoreCase)) inPeers = true;
                else if (inPeers || line.StartsWith("tcp://") || line.StartsWith("udp://") || line.StartsWith("ws://")
                         || line.StartsWith("wss://") || line.StartsWith("txt://") || line.StartsWith("ring://"))
                    peers.Add(line);
            }

            if (peers.Count > 0) room.Peers = string.Join("\n", peers);
            if (string.IsNullOrWhiteSpace(room.Name)) room.Name = "朋友的房间";

            // 房主地址可能带端口
            string host; int port;
            if (TryParseAddress(room.HostIp, out host, out port)) { room.HostIp = host; room.Port = port; }

            // 什么都没解析出来 -> 当作纯地址
            if (string.IsNullOrWhiteSpace(room.Network) && string.IsNullOrWhiteSpace(room.HostIp) && peers.Count == 0)
            {
                if (TryParseAddress(room.HostIp ?? text.Trim(), out host, out port))
                { room.HostIp = host; room.Port = port; }
                else return null;
            }
            return room;
        }

        public static IEnumerable<string> SplitPeers(string text)
            => (text ?? "").Replace("\r", "").Split(new[] { '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(s => s.Trim()).Where(s => s.Length > 0);

        /// <summary>接受 "10.1.2.3:51234"、"10.1.2.3"。</summary>
        public static bool TryParseAddress(string text, out string host, out int port)
        {
            host = null; port = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            int i = text.LastIndexOf(':');
            if (i > 0 && i < text.Length - 1 && int.TryParse(text.Substring(i + 1), out port) && port > 0 && port <= 65535)
            {
                host = text.Substring(0, i).Trim();
                return host.Length > 0;
            }

            host = text;
            port = 0;
            return true;
        }

        /// <summary>
        /// 从实例最新的 logs/latest.log 里找最近一次"对局域网开放"的端口。找不到返回 0。
        /// </summary>
        public static int DetectLanPort(Instance inst, out DateTime writtenAt)
        {
            writtenAt = DateTime.MinValue;
            if (inst == null) return 0;

            var log = Path.Combine(inst.GameDir, "logs", "latest.log");
            try
            {
                if (!File.Exists(log)) return 0;
                writtenAt = File.GetLastWriteTime(log);

                string text;
                using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - 256 * 1024);
                    if (start > 0) fs.Seek(start, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                }

                int port = 0;
                foreach (Match m in PortPattern.Matches(text))
                {
                    var g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    int.TryParse(g, out port);
                }
                return port;
            }
            catch { return 0; }
        }

        /// <summary>新房间的默认网络名，带随机后缀，避免和别人撞。</summary>
        public static string NewNetworkName()
        {
            var rnd = new Random();
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            var sb = new StringBuilder("luno-");
            for (int i = 0; i < 4; i++) sb.Append(chars[rnd.Next(chars.Length)]);
            return sb.ToString();
        }

        public static string NewSecret()
        {
            var rnd = new Random();
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            var sb = new StringBuilder();
            for (int i = 0; i < 6; i++) sb.Append(chars[rnd.Next(chars.Length)]);
            return sb.ToString();
        }
    }
}
