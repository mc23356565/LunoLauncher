using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenLauncher.Game;

namespace OpenLauncher.Core
{
    /// <summary>
    /// 一个"联机房间"：本质就是对方局域网世界的 <c>虚拟IP:端口</c>，存下来方便一键直连。
    /// 房间只记地址，不含任何账号或令牌。
    /// </summary>
    public class CoopRoom
    {
        public string Name = "好友房间";
        public string Host = "";
        public int Port;

        public string Address => Port > 0 ? $"{Host}:{Port}" : Host;

        public override string ToString() => Port > 0 ? $"{Name}  ({Address})" : Name;

        public string ToLine() => $"{Clean(Name)}|{Clean(Host)}|{Port}";

        public static CoopRoom Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            var parts = line.Split('|');
            if (parts.Length < 3) return null;
            int port;
            int.TryParse(parts[2], out port);
            var host = parts[1].Trim();
            if (host.Length == 0) return null;
            var name = parts[0].Trim();
            return new CoopRoom { Name = name.Length == 0 ? host : name, Host = host, Port = port };
        }

        static string Clean(string s) => (s ?? "").Replace('|', '/').Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>
    /// 局域网联机的轻量支持：房间列表 + 从游戏日志里读出"对局域网开放"的端口。
    ///
    /// 刻意不做的事：不建隧道、不装虚拟网卡、不提权、不打包任何第三方程序。
    /// 网络层交给 ZeroTier / Tailscale / EasyTier 这类虚拟局域网，这里只负责
    /// "把地址记住 + 把端口找出来 + 启动时自动进服"。
    /// </summary>
    public static class Coop
    {
        const string Key = "coop.rooms";

        // 1.13+ 开局域网：Local game hosted on port 51234
        // 1.12 及更早：  Started serving on 51234
        static readonly Regex PortPattern = new Regex(
            @"Local game hosted on port (\d+)|Started serving on (\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>已保存的房间，按保存顺序。</summary>
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

        /// <summary>接受 "10.1.2.3:51234"、"10.1.2.3"、"host:25565"。</summary>
        public static bool TryParseAddress(string text, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            int i = text.LastIndexOf(':');
            if (i > 0 && i < text.Length - 1 && int.TryParse(text.Substring(i + 1), out port) && port > 0 && port <= 65535)
            {
                host = text.Substring(0, i).Trim();
                return host.Length > 0;
            }

            host = text;
            port = 25565;
            return true;
        }

        /// <summary>
        /// 从实例最新的 <c>logs/latest.log</c> 里找最近一次"对局域网开放"的端口。
        /// 找不到返回 0。只读文件尾 256 KB，日志再大也很快。
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
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        text = sr.ReadToEnd();
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
    }
}
