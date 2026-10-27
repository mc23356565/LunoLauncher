using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LunoLauncher.Core
{
    /// <summary>
    /// EasyTier 联机隧道。两种模式，界面上自己选：
    ///
    /// 1) **临时模式（默认）**：点「启动隧道」→ 一次 UAC → easytier-core 跑在独立控制台里。
    ///    关掉窗口或点「停止」即结束，不常驻、不占后台。
    /// 2) **服务模式**：`easytier-cli service install --core-args …` 装一次（一次 UAC），
    ///    之后由 Windows 服务管理器开机常驻；启动器只读状态、"启动/停止"也只是一次服务调用。
    ///    代价是常驻内存与开机自启——不想要就点「卸载服务」。
    ///
    /// 两种模式都不做「启动器提权后托管子进程」：提权进程无法被非提权父进程杀死，
    /// 用户直接关启动器就会留下残留进程和虚拟网卡。服务交给 Windows 管，临时模式交给那个控制台窗口。
    /// </summary>
    public static class Tunnel
    {
        public const string CoreExe = "easytier-core.exe";
        public const string CliExe = "easytier-cli.exe";
        public const string ProcessName = "easytier-core";
        public const string ServiceName = "easytier-core";

        /// <summary>默认公共共享节点（社区提供，2026-10 实测前四个可连）。</summary>
        public static readonly string[] FallbackPeers =
        {
            "tcp://225284.xyz:11010",              // 上海电信，可中转
            "tcp://183.230.36.171:11010",          // 重庆移动跨网专线，可中转
            "tcp://easytier.weiai.org.cn:11010",   // 厦门电信，可中转
            "tcp://et.sumnight.top:11010",         // 上海 BGP，中转约 500KB/s
        };

        /// <summary>首选项节点（给人看的那一个）。</summary>
        public const string OfficialNode = "tcp://225284.xyz:11010";

        // ---------- 配置 ----------

        static string S(string key, string def = "") => Settings.Get("tunnel." + key, def);
        static void W(string key, string value) => Settings.Set("tunnel." + key, value ?? "");
        static bool B(string key, bool def) => S(key, def ? "1" : "0") == "1";

        public static string EasyTierDir { get => S("dir"); set => W("dir", value); }
        public static string NetworkName { get => S("name", "dawn-home"); set => W("name", value); }
        public static string NetworkSecret { get => S("secret"); set => W("secret", value); }
        public static bool Dhcp { get => B("dhcp", true); set => W("dhcp", value ? "1" : "0"); }
        public static string Ipv4 { get => S("ipv4"); set => W("ipv4", value); }
        public static string PeersText { get => S("peers", DefaultPeersText); set => W("peers", value); }

        static string DefaultPeersText => string.Join(Environment.NewLine, FallbackPeers);

        /// <summary>官方公共节点已下线（域名 NXDOMAIN），配置里残留的跳过，免得每次重连都报解析失败。</summary>
        static bool IsDeadPeer(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            var u = url.Trim().ToLowerInvariant();
            return u.Contains("public.easytier.top") || u.Contains("public2.easytier.top");
        }
        public static bool LatencyFirst { get => B("latency", true); set => W("latency", value ? "1" : "0"); }
        public static bool BroadcastRelay { get => B("broadcast", false); set => W("broadcast", value ? "1" : "0"); }
        public static string ListenPort { get => S("listen"); set => W("listen", value); }

        /// <summary>一行一个对端，支持 `tcp://1.2.3.4:11010`。</summary>
        public static List<string> Peers()
        {
            var list = new List<string>();
            foreach (var line in (PeersText ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = line.Trim();
                if (s.Length == 0 || list.Contains(s) || IsDeadPeer(s)) continue;
                list.Add(s);
            }
            return list;
        }

        // ---------- 路径 ----------

        public static string TunnelDir
        {
            get
            {
                var d = Path.Combine(Paths.DataDir, "tunnel");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string LogDir
        {
            get
            {
                var d = Path.Combine(TunnelDir, "logs");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string ScriptPath => Path.Combine(TunnelDir, "start-tunnel.cmd");

        // ---------- 找 exe ----------

        public static string Find(string exeName)
        {
            foreach (var dir in CandidateDirs())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var p = Path.Combine(dir, exeName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        static IEnumerable<string> CandidateDirs()
        {
            yield return EasyTierDir;
            // 内置副本：启动器自带的 EasyTier，第一次用到时解压出来
            var builtin = TunnelPayload.Ensure();
            if (!string.IsNullOrWhiteSpace(builtin)) yield return builtin;
            yield return Path.Combine(TunnelDir, "easytier");
            yield return Paths.ExeDir;
            yield return Path.Combine(Paths.ExeDir, "easytier");

            // exe 的上一级目录里的 easytier\（例如启动器在 dist\，二进制在仓库根 easytier\）
            var parent = Path.GetDirectoryName(Paths.ExeDir);
            if (!string.IsNullOrWhiteSpace(parent)) yield return Path.Combine(parent, "easytier");

            // 官方安装器默认位置
            yield return @"C:\Program Files\EasyTier";

            yield return Path.Combine(Paths.ExeDir, "tunnel");
        }

        public static bool Ready => Find(CoreExe) != null;

        // ---------- 命令行 ----------

        /// <summary>按当前配置生成 easytier-core 的参数（对应 EasyTier v2.6.x 的 --help）。</summary>
        public static List<string> Args()
        {
            var a = new List<string>();

            var name = (NetworkName ?? "").Trim();
            if (name.Length == 0) name = "dawn-home";
            a.Add("--network-name"); a.Add(name);

            var secret = (NetworkSecret ?? "").Trim();
            if (secret.Length > 0) { a.Add("--network-secret"); a.Add(secret); }

            if (Dhcp) a.Add("--dhcp");
            else if (!string.IsNullOrWhiteSpace(Ipv4)) { a.Add("--ipv4"); a.Add(Ipv4.Trim()); }

            var peers = Peers();
            if (peers.Count == 0) peers.AddRange(FallbackPeers);
            foreach (var p in peers) { a.Add("--peers"); a.Add(p); }

            if (!string.IsNullOrWhiteSpace(ListenPort)) { a.Add("--listeners"); a.Add(ListenPort.Trim()); }
            if (LatencyFirst) a.Add("--latency-first");
            if (BroadcastRelay) a.Add("--enable-udp-broadcast-relay");

            // EasyTier 自己写日志：启动器读日志就能显示状态，不必接管 stdout
            a.Add("--file-log-dir"); a.Add(LogDir);
            a.Add("--file-log-level"); a.Add("info");
            a.Add("--console-log-level"); a.Add("info");
            return a;
        }

        public static string CommandLine() => string.Join(" ", Args().Select(Util.QuoteArg));

        // ---------- 状态 ----------

        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName(ProcessName).Length > 0; }
            catch { return false; }
        }

        /// <summary>EasyTier 自己写的日志尾部。</summary>
        public static string Tail(int maxLines = 40)
        {
            try
            {
                var dir = new DirectoryInfo(LogDir);
                if (!dir.Exists) return "";
                var file = dir.GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (file == null) return "";

                using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - 96 * 1024);
                    if (start > 0) fs.Seek(start, SeekOrigin.Begin);
                    string text;
                    using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();

                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    if (lines.Length > maxLines) lines = lines.Skip(lines.Length - maxLines).ToArray();
                    return string.Join("\n", lines).Trim();
                }
            }
            catch { return ""; }
        }

        static readonly Regex IpPattern = new Regex(@"\b10\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled);

        public static string VirtualIp()
        {
            var m = IpPattern.Match(Tail(200));
            return m.Success ? m.Value : "";
        }

        /// <summary>服务是否已安装（读 easytier-cli 的只读查询，不需要管理员）。</summary>
        public static bool ServiceInstalled()
        {
            var s = ServiceStatus();
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return s.IndexOf("Service is", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string ServiceStatus()
        {
            var cli = Find(CliExe);
            if (cli == null) return "(没有 easytier-cli.exe，无法查询服务状态)";
            return Run(cli, "service status");
        }

        public static string Mode() => ModeFast();

        /// <summary>给 UI 用的一句话状态。只做进程检查与日志读取，不调用任何外部程序（避免卡界面）。</summary>
        public static string Summary()
        {
            if (!Ready) return "隧道：未配置（找不到 EasyTier，点「诊断」看查找路径）";
            if (!IsRunning()) return "隧道：未运行";

            var ip = VirtualIp();
            if (ip.Length > 0)
                return "隧道：运行中（本机虚拟 IP " + ip + "） · " + ModeFast();

            // 起来了但还没拿到 IP：从日志里判断是不是连不上节点
            var tail = Tail(120);
            bool nodeProblem =
                tail.IndexOf("resolve timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tail.IndexOf("dns lookup failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tail.IndexOf("connect to peer error", StringComparison.OrdinalIgnoreCase) >= 0;

            if (nodeProblem)
                return "隧道：已启动，但连不上共享节点 ⚠ 把「公共共享节点」换成可用的 IP（自建节点），或改用 IPv6 直连";

            return "隧道：已启动，还没读到虚拟 IP（稍等，或看「状态与日志」） · " + ModeFast();
        }

        /// <summary>不跑外部程序的模式判断。</summary>
        static string ModeFast() => IsElevated() ? "管理员" : "普通权限";

        // ---------- 动作 ----------

        /// <summary>当前进程是否以管理员权限运行。</summary>
        public static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        /// <summary>以管理员身份重新启动启动器本身（调用方负责随后退出当前实例）。</summary>
        public static void RestartElevated()
        {
            var psi = new ProcessStartInfo(Paths.ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Paths.ExeDir,
            };
            Process.Start(psi);
        }

        public static void InstallService()
        {
            var cli = Find(CliExe);
            var core = Find(CoreExe);
            if (cli == null || core == null)
                throw new Exception("没找到 easytier-core.exe / easytier-cli.exe，先在「EasyTier 目录」里指定解压目录。");

            var args = new List<string> { "service", "install", "--core-path", core, "--service-work-dir", Path.GetDirectoryName(core), "--core-args" };
            args.AddRange(Args());
            RunElevated(cli, string.Join(" ", args.Select(Util.QuoteArg)), Path.GetDirectoryName(cli));
        }

        public static void UninstallService()
        {
            var cli = Find(CliExe);
            if (cli == null) throw new Exception("没找到 easytier-cli.exe。");
            RunElevated(cli, "service uninstall", Path.GetDirectoryName(cli));
        }

        /// <summary>启动隧道：以管理员身份拉起 easytier-core（启动器已是管理员时不再提权）。</summary>
        public static string Start()
        {
            Log.Info($"[隧道] Start：Ready={Ready} 管理员={IsElevated()} 目录={(string.IsNullOrWhiteSpace(EasyTierDir) ? "(自动)" : EasyTierDir)}");

            if (!Ready)
            {
                Log.Warn("[隧道] 未找到 easytier-core.exe");
                throw new Exception("还没找到 easytier-core.exe。\n\n" +
                    "启动器自带一份 EasyTier，第一次用到时会自动解压到：\n" +
                    "  " + TunnelPayload.Dir + "\n" +
                    (string.IsNullOrEmpty(TunnelPayload.Error) ? "" : "这次没解压成功：" + TunnelPayload.Error + "\n") +
                    "\n也可以自己下载一份：在「联机 → 房间设置 → EasyTier 目录」里指定解压目录。\n" +
                    "自动查找的位置：数据目录\\tunnel\\bin、启动器目录及其 easytier 子目录、上一级目录\\easytier、C:\\Program Files\\EasyTier。\n\n" +
                    "启动器现在在：" + Paths.ExeDir);
            }

            if (IsRunning()) return "隧道已经在运行了。要换配置，先点「停止」。";

            var core = Find(CoreExe);
            var args = string.Join(" ", Args().Select(Util.QuoteArg));
            Log.Info($"[隧道] core={core}");
            Log.Info($"[隧道] args={args}");

            var workDir = Path.GetDirectoryName(core);

            // 已是管理员：直接 CreateProcess，不经过 ShellExecute。
            // 这样既不触发 UAC，也不会被 SmartScreen 拦（未签名 + 下载标记的 exe 常被它拦成 1223）。
            if (IsElevated())
            {
                var direct = new ProcessStartInfo(core, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = workDir,
                    CreateNoWindow = false,
                };
                try
                {
                    var p1 = Process.Start(direct);
                    Log.Info("[隧道] 直接启动成功 PID=" + (p1 == null ? "null" : p1.Id.ToString()));
                    return "已启动 easytier-core（独立控制台窗口，关掉它隧道就断）。\n\n" +
                           "等 5~10 秒，状态会显示本机虚拟 IP。\n" +
                           "对方也要开隧道，并且网络名、密钥、公共节点三样必须一致。";
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Log.Error("[隧道] 直接启动失败：" + ex.NativeErrorCode + " " + ex.Message, null);
                    return "启动 easytier-core 失败（错误码 " + ex.NativeErrorCode + "）：" + ex.Message + "\n\n" +
                           "启动器已经是管理员，所以不是权限问题。常见原因：\n" +
                           "· easytier-core.exe 被杀软/SmartScreen 拦截 → 在文件属性里勾「解除锁定」，或把 easytier 目录加入白名单\n" +
                           "· 缺少 wintun.dll 或 VC 运行库\n\n" +
                           "临时替代：点「生成启动脚本」，右键那个 .cmd 选「以管理员身份运行」。";
                }
            }

            // 非管理员：只能走 runas 提权
            var psi = new ProcessStartInfo(core, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workDir,
            };

            try
            {
                var p = Process.Start(psi);
                Log.Info("[隧道] 提权启动成功 PID=" + (p == null ? "null" : p.Id.ToString()));
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Error("[隧道] 提权启动失败：" + ex.NativeErrorCode + " " + ex.Message, null);
                return "启动 easytier-core 失败（错误码 " + ex.NativeErrorCode + "）：" + ex.Message + "\n\n" +
                       "启动器不是管理员，提权又被系统拒绝。点「以管理员身份重启」，或右键 exe 选「以管理员身份运行」。\n\n" +
                       "临时替代：点「生成启动脚本」，右键那个 .cmd 选「以管理员身份运行」。";
            }

            return "已启动 easytier-core（独立控制台窗口，关掉它隧道就断）。\n\n" +
                   "等 5~10 秒，状态会显示本机虚拟 IP。\n" +
                   "对方也要开隧道，并且网络名、密钥、公共节点三样必须一致。";
        }

        public static string Stop()
        {
            if (!IsRunning()) return "隧道没在运行。";

            if (ServiceInstalled())
            {
                try
                {
                    RunElevated(Find(CliExe), "service stop", Path.GetDirectoryName(Find(CliExe)));
                    return "已请求停止 EasyTier 服务。";
                }
                catch (Exception ex) { return "停止服务失败：" + ex.Message; }
            }

            var output = Run("taskkill", "/IM " + CoreExe + " /F");
            var ok = output.IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     output.IndexOf("成功", StringComparison.Ordinal) >= 0;
            if (ok) return "已结束 easytier-core，虚拟网卡随之释放。";

            return "没能直接结束 easytier-core（它是管理员权限，启动器不是）。\n\n" +
                   "最简单的办法：去关掉那个写着 LunoLauncher EasyTier 的控制台窗口。\n\n" + output.Trim();
        }

        public static string WriteScript()
        {
            var core = Find(CoreExe);
            if (core == null) throw new Exception("没找到 " + CoreExe + "。请先指定 EasyTier 目录。");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("title LunoLauncher EasyTier");
            sb.AppendLine("cd /d \"" + Path.GetDirectoryName(core) + "\"");
            sb.AppendLine("\"" + core + "\" " + CommandLine());
            sb.AppendLine("echo.");
            sb.AppendLine("echo 隧道已退出（错误码 %ERRORLEVEL%）。");
            sb.AppendLine("pause");
            File.WriteAllText(ScriptPath, sb.ToString(), new UTF8Encoding(false));
            return ScriptPath;
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("EasyTier 目录：" + (string.IsNullOrWhiteSpace(EasyTierDir) ? "(自动查找)" : EasyTierDir));
            sb.AppendLine("core：" + (Find(CoreExe) ?? "未找到"));
            sb.AppendLine("cli ：" + (Find(CliExe) ?? "未找到（只影响查状态/peer，不影响联机）"));
            sb.AppendLine("模式：" + Mode());
            sb.AppendLine("进程：" + (IsRunning() ? "运行中" : "未运行"));
            var ip = VirtualIp();
            sb.AppendLine("虚拟 IP：" + (ip.Length > 0 ? ip : "(未读到)"));
            sb.AppendLine("服务状态：" + ServiceStatus());
            sb.AppendLine("命令行：" + CommandLine());
            sb.AppendLine();
            sb.AppendLine("—— 日志尾部（" + LogDir + "）——");
            var tail = Tail(40);
            sb.AppendLine(string.IsNullOrWhiteSpace(tail) ? "(还没有日志：可能没启动过，或启动失败了)" : tail);
            return sb.ToString();
        }

        /// <summary>诊断报告：权限、UAC 策略、能否提权、二进制位置、进程与日志。</summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 启动器 ===");
            sb.AppendLine("路径：" + Paths.ExePath);
            sb.AppendLine("管理员权限：" + (IsElevated() ? "是" : "否"));
            sb.AppendLine("系统：" + Environment.OSVersion.VersionString + " / " + (Environment.Is64BitProcess ? "x64" : "x86"));
            sb.AppendLine("数据目录：" + Paths.DataDir);

            sb.AppendLine();
            sb.AppendLine("=== 提权探测（启动一个空的 cmd，看系统是否给授权）===");
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c exit")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi)) { p?.WaitForExit(3000); }
                sb.AppendLine("结果：成功，没有报错（说明系统允许提权）");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                sb.AppendLine("结果：失败，错误码 " + ex.NativeErrorCode + " — " + ex.Message);
            }
            catch (Exception ex) { sb.AppendLine("结果：异常 — " + ex.Message); }

            sb.AppendLine();
            sb.AppendLine("=== UAC 策略（HKLM\\...\\Policies\\System）===");
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    foreach (var name in new[] { "EnableLUA", "ConsentPromptBehaviorAdmin", "ConsentPromptBehaviorUser", "PromptOnSecureDesktop", "FilterAdministratorToken", "EnableInstallerDetection" })
                        sb.AppendLine(name + " = " + (k?.GetValue(name)?.ToString() ?? "(未设置)"));
                }
            }
            catch (Exception ex) { sb.AppendLine("读取失败：" + ex.Message); }

            sb.AppendLine();
            sb.AppendLine("=== EasyTier ===");
            sb.AppendLine("内置副本：EasyTier " + TunnelPayload.Version +
                (TunnelPayload.Packed ? "（随 exe 打包）" : "（这个构建没打包）") + " → " +
                (TunnelPayload.Ensure() ?? ("解压失败：" + (TunnelPayload.Error ?? "未知"))));
            sb.AppendLine("配置目录（设置值）：" + (string.IsNullOrWhiteSpace(EasyTierDir) ? "(空，自动查找)" : EasyTierDir));
            sb.AppendLine("easytier-core.exe：" + (Find(CoreExe) ?? "未找到"));
            sb.AppendLine("easytier-cli.exe ：" + (Find(CliExe) ?? "未找到"));
            sb.AppendLine("wintun.dll      ：" + (Find("wintun.dll") ?? "未找到"));
            sb.AppendLine("进程：" + (IsRunning() ? "运行中" : "未运行"));
            sb.AppendLine("日志目录：" + LogDir);
            sb.AppendLine("命令行：" + CommandLine());

            sb.AppendLine();
            sb.AppendLine("=== 网络身份（别外发）===");
            sb.AppendLine("网络名：" + NetworkName);
            sb.AppendLine("密钥长度：" + (NetworkSecret ?? "").Length + " 字符");
            var effPeers = Peers();
            var rawPeers = (PeersText ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
            sb.AppendLine("节点（实际使用）：" + string.Join("  ", effPeers.ToArray()) +
                          (rawPeers > effPeers.Count ? "（已跳过 " + (rawPeers - effPeers.Count) + " 个下线/重复节点）" : ""));
            return sb.ToString();
        }

        /// <summary>本机可用于别人连接的地址（虚拟IP之外的真实地址）。port 为监听端口。</summary>
        public static List<string> LocalAddresses(int port)
        {
            var v4 = new List<string>();
            var v6 = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        {
                            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) continue;
                            var text = "tcp://[" + a + "]:" + port;
                            if (!v6.Contains(text)) v6.Add(text);
                        }
                        else if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            if (System.Net.IPAddress.IsLoopback(a)) continue;
                            var text = "tcp://" + a + ":" + port;
                            if (!v4.Contains(text)) v4.Add(text);
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("[隧道] 枚举本机地址失败：" + ex.Message); }

            var all = new List<string>();
            all.AddRange(v6);
            all.AddRange(v4);
            return all;
        }

        /// <summary>给"当房主/当共享节点"用的说明文本。</summary>
        public static string MyAddressReport()
        {
            var port = 11010;
            if (!string.IsNullOrWhiteSpace(ListenPort) && int.TryParse(ListenPort.Trim(), out var p) && p > 0) port = p;

            var sb = new StringBuilder();
            sb.AppendLine("你的电脑要当共享节点，别人得能连到它。下面是本机可用地址（端口 " + port + "）：");
            sb.AppendLine();

            var list = LocalAddresses(port);
            if (list.Count == 0)
            {
                sb.AppendLine("(没有可用的地址)");
            }
            else
            {
                foreach (var a in list)
                {
                    var tag = a.Contains("[") ? "IPv6（公网，可跨网）" : "IPv4（多半只是局域网地址）";
                    sb.AppendLine("  " + a + "      ← " + tag);
                }
            }

            sb.AppendLine();
            sb.AppendLine("怎么用：");
            sb.AppendLine("1. 上面「监听端口」填 " + port + "（留空就不监听，别人连不上你）");
            sb.AppendLine("2. 点「启动隧道」，再点「放行 " + port + " 端口」让防火墙放行");
            sb.AppendLine("3. 把 IPv6 那条整行发给朋友，他填进「公共共享节点」");
            sb.AppendLine();
            sb.AppendLine("判断你的 IPv6 是不是公网地址：以 2 或 3 开头（例如 2408:、240e:）通常是公网；");
            sb.AppendLine("fe80: 开头是链路本地地址，不能给外面用；只有 IPv4 且 192.168./10./172.16-31. 开头，说明是内网，");
            sb.AppendLine("需要在路由器上做端口映射，或者用 IPv6。");
            return sb.ToString();
        }

        /// <summary>放行入站端口（需要管理员权限，启动器已是管理员时直接生效）。</summary>
        public static string OpenFirewall(int port)
        {
            var name = "LunoLauncher EasyTier " + port;
            var results = new List<string>();
            foreach (var proto in new[] { "TCP", "UDP" })
            {
                var output = Run("netsh", $"advfirewall firewall add rule name=\"{name} {proto}\" dir=in action=allow protocol={proto} localport={port}", 8000);
                results.Add(proto + "：" + (string.IsNullOrWhiteSpace(output) ? "(无输出)" : output.Trim()));
            }
            var text = string.Join("\n", results);
            var ok = text.IndexOf("确定", StringComparison.Ordinal) >= 0 || text.IndexOf("Ok", StringComparison.OrdinalIgnoreCase) >= 0;
            return (IsElevated()
                    ? ""
                    : "启动器不是管理员，放行大概率会失败。先「以管理员身份重启」。\n\n") +
                   (ok ? "已放行入站 " + port + "（TCP/UDP）。\n\n" : "") + text;
        }

        public static string PeersTable()
        {
            var cli = Find(CliExe);
            if (cli == null) return "(没有 easytier-cli.exe，跳过)";
            var text = Run(cli, "peer");
            if (string.IsNullOrWhiteSpace(text)) text = Run(cli, "-p 127.0.0.1:15888 peer");
            return string.IsNullOrWhiteSpace(text) ? "(没拿到 peer 列表；隧道没在跑时会这样)" : text;
        }

        // ---------- 房间成员 ----------

        static readonly Regex PeerLine = new Regex(@"^(\d{1,3}(?:\.\d{1,3}){3})\s+(\S+)(.*)$", RegexOptions.Compiled);

        /// <summary>
        /// 当前隧道里的成员（房间里有谁在线）。解析 <c>easytier-cli peer</c> 的表格输出；
        /// 拿不到就返回空列表——调用方要自己决定显示"暂无"还是"未知"。
        /// </summary>
        public static List<TunnelPeer> LivePeers()
        {
            var list = new List<TunnelPeer>();
            var cli = Find(CliExe);
            if (cli == null) return list;

            var text = Run(cli, "peer", 6000);
            if (string.IsNullOrWhiteSpace(text)) text = Run(cli, "-p 127.0.0.1:15888 peer", 6000);
            if (string.IsNullOrWhiteSpace(text)) return list;

            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var m = PeerLine.Match(raw.TrimEnd());
                if (!m.Success) continue;

                var peer = new TunnelPeer { Ipv4 = m.Groups[1].Value, Hostname = m.Groups[2].Value };
                var rest = m.Groups[3].Value;
                foreach (var tok in rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = tok.ToLowerInvariant();
                    if (t == "tcp" || t == "udp" || t == "quic" || t == "wg" || t == "ring" || t == "direct")
                        peer.Tunnel = t;
                    else if (peer.Latency.Length == 0 && Regex.IsMatch(t, @"^\d+(\.\d+)?(ms)?$"))
                        peer.Latency = t.EndsWith("ms") ? t : t + " ms";
                }
                list.Add(peer);
            }
            return list;
        }

        // ---------- 进程工具 ----------

        static void RunElevated(string exe, string args, string workDir)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workDir,
            };
            Process.Start(psi);
        }

        static string Run(string exe, string args, int timeoutMs = 4000)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "(超时 " + timeoutMs + " ms)";
                    }
                    var text = (stdout.Result ?? "") + (stderr.Result ?? "");
                    return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
                }
            }
            catch (Exception ex) { return "(执行失败：" + ex.Message + ")"; }
        }
    }
}
