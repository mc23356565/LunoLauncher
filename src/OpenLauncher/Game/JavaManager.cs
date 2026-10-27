using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    /// <summary>一个可用的 Java 运行时</summary>
    public class JavaInfo
    {
        /// <summary>bin\javaw.exe 的完整路径（没有 javaw 时为 java.exe）</summary>
        public string JavawPath;
        /// <summary>JAVA_HOME 目录</summary>
        public string Home;
        /// <summary>完整版本号，如 1.8.0_402 / 17.0.8 / 21.0.7</summary>
        public string Version;
        /// <summary>主版本号：8 / 17 / 21 / 25 …</summary>
        public int Major;
        /// <summary>x64 / x86 / arm64</summary>
        public string Arch;
        public bool Is64Bit => Arch != "x86";
        /// <summary>发行方（release 文件里的 IMPLEMENTOR）</summary>
        public string Vendor;
        /// <summary>注册表 / 环境变量 / 常见目录 / 官方启动器 / 其他启动器 / 本启动器下载 / 手动添加</summary>
        public string Source;
        public bool IsJdk;

        /// <summary>控制台版 java.exe（探测版本、导出启动脚本时用）；没有时退回 javaw</summary>
        public string JavaExePath
        {
            get
            {
                var p = Home == null ? null : Path.Combine(Home, "bin", "java.exe");
                return p != null && File.Exists(p) ? p : JavawPath;
            }
        }

        public string ArchText => Arch switch
        {
            "x64" => "64 位",
            "x86" => "32 位",
            "arm64" => "ARM64",
            _ => "未知架构"
        };

        /// <summary>界面显示用，如 "Java 17.0.8 (64 位) · Eclipse Adoptium"</summary>
        public string Display => $"Java {Version} ({ArchText})" + (string.IsNullOrEmpty(Vendor) ? "" : " · " + Vendor);

        public override string ToString() => Display;

        internal JNode ToJson() => JNode.Obj()
            .Set("home", Home).Set("javaw", JavawPath).Set("version", Version).Set("major", Major)
            .Set("arch", Arch).Set("vendor", Vendor).Set("jdk", IsJdk);

        internal static JavaInfo FromJson(JNode n) => new JavaInfo
        {
            Home = n["home"].Str,
            JavawPath = n["javaw"].Str,
            Version = n["version"].Str,
            Major = n["major"].Int,
            Arch = n["arch"].Str,
            Vendor = n["vendor"].Str,
            IsJdk = n["jdk"].Bool,
        };

        internal JavaInfo Copy() => (JavaInfo)MemberwiseClone();
    }

    /// <summary>
    /// Java 的发现、挑选与自动下载。
    /// 扫描结果按 "路径 + 修改时间" 缓存在 cache\java.json 里，第二次启动不用再跑 java.exe。
    /// </summary>
    public static class JavaManager
    {
        public const string SrcRegistry = "注册表";
        public const string SrcEnv = "环境变量";
        public const string SrcCommon = "常见目录";
        public const string SrcOfficial = "官方启动器";
        public const string SrcOtherLauncher = "其他启动器";
        public const string SrcDownloaded = "本启动器下载";
        public const string SrcManual = "手动添加";

        /// <summary>Mojang 官方 Java 运行时索引</summary>
        public const string RuntimeIndexUrl = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
        /// <summary>下载完成后写入运行时目录的标记文件；没有它的目录视为下载未完成</summary>
        public const string MarkerFile = "openlauncher-runtime.json";
        const string ExtraKey = "java.extra";

        static IReadOnlyList<JavaInfo> _known = new List<JavaInfo>().AsReadOnly();
        static bool _scanned;
        static readonly SemaphoreSlim ScanLock = new SemaphoreSlim(1, 1);
        static readonly SemaphoreSlim DownloadLock = new SemaphoreSlim(1, 1);

        static readonly object CacheLock = new object();
        static Dictionary<string, JNode> _cache;
        static bool _cacheDirty;

        /// <summary>最近一次扫描的结果（含手动添加的）</summary>
        public static IReadOnlyList<JavaInfo> Known => _known;

        // =====================================================================
        // 扫描
        // =====================================================================

        /// <summary>扫描本机所有 Java。force=false 时本次运行内只扫一次，之后直接返回结果。</summary>
        public static async Task<List<JavaInfo>> ScanAsync(bool force = false)
        {
            await ScanLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_scanned && !force) return _known.ToList();
                var sw = Stopwatch.StartNew();
                var list = await Task.Run(() => ScanCore(force)).ConfigureAwait(false);
                _known = list.AsReadOnly();
                _scanned = true;
                Log.Info($"Java 扫描完成：找到 {list.Count} 个，用时 {sw.ElapsedMilliseconds} ms" +
                         (list.Count > 0 ? "\n  " + string.Join("\n  ", list.Select(j => $"{j.Display} [{j.Source}] {j.Home}")) : ""));
                return list.ToList();
            }
            finally { ScanLock.Release(); }
        }

        static List<JavaInfo> ScanCore(bool force)
        {
            var found = Discover();
            // 统一成规范化的 JAVA_HOME 去重；先发现的来源优先
            var homes = new List<string>();
            var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, src) in found)
            {
                var home = ResolveHome(path);
                if (home == null || source.ContainsKey(home)) continue;
                source[home] = src;
                homes.Add(home);
            }

            LoadCache();
            var results = new JavaInfo[homes.Count];
            Parallel.For(0, homes.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                try
                {
                    var info = InspectHome(homes[i], !force);
                    if (info != null) { info.Source = source[homes[i]]; results[i] = info; }
                }
                catch (Exception ex) { Log.Warn($"检查 Java 失败 {homes[i]}：{ex.Message}"); }
            });
            SaveCache();

            var list = results.Where(j => j != null).ToList();
            Sort(list);
            return list;
        }

        static void Sort(List<JavaInfo> list)
        {
            list.Sort((a, b) =>
            {
                int c = b.Major.CompareTo(a.Major);
                if (c == 0) c = Util.CompareVersion(b.Version, a.Version);
                if (c == 0) c = b.Is64Bit.CompareTo(a.Is64Bit);
                if (c == 0) c = string.Compare(a.Home, b.Home, StringComparison.OrdinalIgnoreCase);
                return c;
            });
        }

        /// <summary>收集所有可能是 Java 的路径（还没验证）</summary>
        static List<(string path, string src)> Discover()
        {
            var c = new List<(string, string)>();
            void safe(string what, Action a)
            {
                try { a(); }
                catch (Exception ex) { Log.Warn($"扫描 Java（{what}）出错：{ex.Message}"); }
            }

            safe(SrcDownloaded, () =>
            {
                foreach (var d in Directory.GetDirectories(Paths.JavaRuntimes))
                    if (File.Exists(Path.Combine(d, MarkerFile))) c.Add((d, SrcDownloaded));
            });
            safe(SrcRegistry, () => ScanRegistry(c));
            safe(SrcEnv, () => ScanEnvironment(c));
            safe(SrcCommon, () => ScanCommonDirs(c));
            safe(SrcOfficial, () => ScanOfficial(c));
            safe(SrcOtherLauncher, () =>
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                ScanTree(Path.Combine(appData, ".hmcl", "java"), 2, SrcOtherLauncher, c);
            });
            safe("游戏目录", () =>
            {
                foreach (var f in Settings.GameFolders)
                    if (!string.IsNullOrEmpty(f.Path)) ScanTree(Path.Combine(f.Path, "runtime"), 3, SrcOfficial, c);
            });
            // 手动添加的放最后：能被自动发现的就按自动来源显示
            safe(SrcManual, () => { foreach (var p in Settings.ExtraList(ExtraKey)) c.Add((p, SrcManual)); });
            return c;
        }

        static readonly string[] RegRoots =
        {
            @"SOFTWARE\JavaSoft",
            @"SOFTWARE\Eclipse Adoptium",
            @"SOFTWARE\Eclipse Foundation",
            @"SOFTWARE\AdoptOpenJDK",
            @"SOFTWARE\Azul Systems\Zulu",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\BellSoft\Liberica",
            @"SOFTWARE\Semeru",
            @"SOFTWARE\IBM\Semeru",
            @"SOFTWARE\Amazon Corretto",
        };
        static readonly string[] RegValueNames = { "JavaHome", "Path", "InstallationPath", "InstallPath" };
        static readonly HashSet<string> RegSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Prefs", "Java Update", "Java Plug-in", "Java Web Start", "Auto Update", "Policy" };

        static void ScanRegistry(List<(string, string)> c)
        {
            var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            var views = Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Default };
            foreach (var hive in hives)
                foreach (var view in views)
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                            foreach (var root in RegRoots)
                                using (var k = baseKey.OpenSubKey(root))
                                    if (k != null) { int budget = 2000; WalkRegistry(k, 0, c, ref budget); }
                    }
                    catch (Exception ex) { Log.Warn($"读取注册表 {hive}/{view} 失败：{ex.Message}"); }
                }
        }

        static void WalkRegistry(RegistryKey k, int depth, List<(string, string)> c, ref int budget)
        {
            if (--budget < 0) return;
            foreach (var name in RegValueNames)
            {
                try
                {
                    if (k.GetValue(name) is string v && !string.IsNullOrWhiteSpace(v)) c.Add((v, SrcRegistry));
                }
                catch { }
            }
            if (depth >= 4) return;
            string[] subs;
            try { subs = k.GetSubKeyNames(); } catch { return; }
            foreach (var s in subs)
            {
                if (RegSkip.Contains(s)) continue;
                try
                {
                    using (var sk = k.OpenSubKey(s))
                        if (sk != null) WalkRegistry(sk, depth + 1, c, ref budget);
                }
                catch { }
            }
        }

        static void ScanEnvironment(List<(string, string)> c)
        {
            var targets = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine };
            foreach (var name in new[] { "JAVA_HOME", "JDK_HOME", "JRE_HOME" })
                foreach (var t in targets)
                {
                    string v = null;
                    try { v = Environment.GetEnvironmentVariable(name, t); } catch { }
                    if (!string.IsNullOrWhiteSpace(v)) c.Add((v, SrcEnv));
                }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in targets)
            {
                string path = null;
                try { path = Environment.GetEnvironmentVariable("PATH", t); } catch { }
                if (string.IsNullOrEmpty(path)) continue;
                foreach (var raw in path.Split(';'))
                {
                    var e = raw.Trim().Trim('"');
                    if (e.Length == 0) continue;
                    try { e = Environment.ExpandEnvironmentVariables(e); } catch { }
                    if (!seen.Add(e)) continue;
                    try
                    {
                        var w = Path.Combine(e, "javaw.exe");
                        if (File.Exists(w)) c.Add((w, SrcEnv));
                        else
                        {
                            var j = Path.Combine(e, "java.exe");
                            if (File.Exists(j)) c.Add((j, SrcEnv));
                        }
                    }
                    catch { }
                }
            }
        }

        static readonly string[] VendorDirs =
        {
            "Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Zulu", "Azul Systems", "BellSoft",
            "Amazon Corretto", "Semeru", "IBM", "Huawei", "BiSheng", "Tencent", "Tencent Kona", "Kona", "Alibaba",
            "Dragonwell", "OpenJDK", "RedHat", "Red Hat", "ojdkbuild", "GraalVM", "Liberica",
        };

        static void ScanCommonDirs(List<(string, string)> c)
        {
            var roots = new List<string>();
            void addRoot(string r)
            {
                if (string.IsNullOrEmpty(r)) return;
                if (!roots.Any(x => string.Equals(x.TrimEnd('\\'), r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) roots.Add(r);
            }
            addRoot(Environment.GetEnvironmentVariable("ProgramW6432"));
            addRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            addRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            addRoot(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) addRoot(Path.Combine(local, "Programs"));

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var v in VendorDirs) ScanTree(Path.Combine(root, v), 2, SrcCommon, c);
                // Microsoft 文件夹很大，只看 jdk-* / jre-* 这一层
                ScanTree(Path.Combine(root, "Microsoft"), 1, SrcCommon, c,
                    n => n.StartsWith("jdk", StringComparison.OrdinalIgnoreCase) || n.StartsWith("jre", StringComparison.OrdinalIgnoreCase) || n.StartsWith("openjdk", StringComparison.OrdinalIgnoreCase));
            }
            // IntelliJ IDEA 下载的 JDK
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(user)) ScanTree(Path.Combine(user, ".jdks"), 1, SrcCommon, c);
        }

        static void ScanOfficial(List<(string, string)> c)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // runtime\<组件>\<平台>\<组件>
            ScanTree(Path.Combine(appData, ".minecraft", "runtime"), 3, SrcOfficial, c);
            // 微软商店 / Xbox 版启动器
            ScanTree(Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"), 3, SrcOfficial, c);
            // 老版独立安装的官方启动器
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                ScanTree(Path.Combine(pf, "Minecraft Launcher", "runtime"), 3, SrcOfficial, c);
                ScanTree(Path.Combine(pf, "Minecraft", "runtime"), 3, SrcOfficial, c);
            }
        }

        /// <summary>在 dir 下找 Java 目录（最多 depth 层），找到就不再往里钻</summary>
        static void ScanTree(string dir, int depth, string src, List<(string, string)> c, Func<string, bool> filter = null)
        {
            if (depth <= 0 || string.IsNullOrEmpty(dir)) return;
            string[] subs;
            try
            {
                if (!Directory.Exists(dir)) return;
                subs = Directory.GetDirectories(dir);
            }
            catch { return; }
            foreach (var s in subs)
            {
                if (filter != null && !filter(Path.GetFileName(s))) continue;
                if (LooksLikeHome(s)) c.Add((s, src));
                else if (depth > 1) ScanTree(s, depth - 1, src, c);
            }
        }

        static bool LooksLikeHome(string d)
        {
            try { return File.Exists(Path.Combine(d, "bin", "javaw.exe")) || File.Exists(Path.Combine(d, "bin", "java.exe")); }
            catch { return false; }
        }

        // =====================================================================
        // 检查单个 Java
        // =====================================================================

        /// <summary>检查一个 Java（javaw.exe / java.exe / bin 目录 / JAVA_HOME 都可以）。无效时返回 null。</summary>
        public static JavaInfo Inspect(string pathToJavawOrHome)
        {
            var home = ResolveHome(pathToJavawOrHome);
            if (home == null) return null;
            LoadCache();
            JavaInfo info;
            try { info = InspectHome(home, true); }
            catch (Exception ex) { Log.Warn($"检查 Java 失败 {home}：{ex.Message}"); return null; }
            SaveCache();
            if (info == null) return null;
            var known = _known.FirstOrDefault(j => SameHome(j.Home, home));
            info.Source = known?.Source ?? (IsUnder(home, Paths.JavaRuntimes) ? SrcDownloaded : SrcManual);
            return info;
        }

        /// <summary>把各种形式的路径统一成真实的 JAVA_HOME；不像 Java 时返回 null</summary>
        static string ResolveHome(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
                if (path.Length == 0) return null;
                path = Path.GetFullPath(path);
                string exe;
                if (File.Exists(path))
                {
                    var name = Path.GetFileName(path);
                    if (!name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("java.exe", StringComparison.OrdinalIgnoreCase)) return null;
                    exe = path;
                }
                else if (Directory.Exists(path))
                {
                    exe = new[]
                    {
                        Path.Combine(path, "bin", "javaw.exe"), Path.Combine(path, "bin", "java.exe"),
                        Path.Combine(path, "javaw.exe"), Path.Combine(path, "java.exe"),
                    }.FirstOrDefault(File.Exists);
                }
                else return null;
                if (exe == null) return null;

                // 解开符号链接（Oracle 的 javapath、"latest" 之类的目录联接）
                exe = FinalPath(exe) ?? exe;
                var bin = Path.GetDirectoryName(exe);
                if (bin == null || !Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase)) return null;
                var home = Path.GetDirectoryName(bin);
                if (string.IsNullOrEmpty(home)) return null;
                // 排除 System32 / javapath 里的转发器：真正的 Java 一定有 lib 目录
                if (!Directory.Exists(Path.Combine(home, "lib"))) return null;
                return home.TrimEnd('\\');
            }
            catch { return null; }
        }

        static JavaInfo InspectHome(string home, bool useCache)
        {
            var javaw = Path.Combine(home, "bin", "javaw.exe");
            if (!File.Exists(javaw)) javaw = Path.Combine(home, "bin", "java.exe");
            if (!File.Exists(javaw)) return null;
            var stamp = Stamp(home, javaw);

            if (useCache)
            {
                lock (CacheLock)
                {
                    if (_cache != null && _cache.TryGetValue(home, out var e) && e["stamp"].Str == stamp)
                    {
                        if (e["invalid"].Bool) return null;
                        var cached = JavaInfo.FromJson(e);
                        cached.Home = home;
                        cached.JavawPath = javaw;
                        if (cached.Major > 0 && !string.IsNullOrEmpty(cached.Version)) return cached;
                    }
                }
            }

            var info = InspectCore(home, javaw);
            lock (CacheLock)
            {
                if (_cache == null) _cache = new Dictionary<string, JNode>(StringComparer.OrdinalIgnoreCase);
                var entry = info != null ? info.ToJson() : JNode.Obj().Set("home", home).Set("invalid", true);
                entry.Set("stamp", stamp);
                _cache[home] = entry;
                _cacheDirty = true;
            }
            return info;
        }

        static string Stamp(string home, string javaw)
        {
            var fi = new FileInfo(javaw);
            var rel = new FileInfo(Path.Combine(home, "release"));
            return $"{fi.LastWriteTimeUtc.Ticks}-{fi.Length}-{(rel.Exists ? rel.LastWriteTimeUtc.Ticks + "-" + rel.Length : "0")}";
        }

        static JavaInfo InspectCore(string home, string javaw)
        {
            var info = new JavaInfo { Home = home, JavawPath = javaw };
            var rel = ReadRelease(home);
            rel.TryGetValue("JAVA_VERSION", out info.Version);
            if (rel.TryGetValue("OS_ARCH", out var a)) info.Arch = NormArch(a);
            if (rel.TryGetValue("IMPLEMENTOR", out var impl)) info.Vendor = impl;
            // Oracle 的 Java 8（包括 Mojang 的 jre-legacy）release 里没有 IMPLEMENTOR，只有 BUILD_TYPE="commercial"
            else if (rel.TryGetValue("BUILD_TYPE", out var bt) && bt.Equals("commercial", StringComparison.OrdinalIgnoreCase)) info.Vendor = "Oracle";

            if (string.IsNullOrEmpty(info.Version) || info.Arch == null)
            {
                var props = RunForProperties(info.JavaExePath);
                if (props != null)
                {
                    if (string.IsNullOrEmpty(info.Version)) props.TryGetValue("java.version", out info.Version);
                    if (info.Arch == null && props.TryGetValue("os.arch", out var pa)) info.Arch = NormArch(pa);
                    if (string.IsNullOrEmpty(info.Vendor))
                    {
                        props.TryGetValue("java.vendor", out var v);
                        if (string.IsNullOrEmpty(v)) props.TryGetValue("java.vm.vendor", out v);
                        info.Vendor = v;
                    }
                }
            }
            if (info.Arch == null) info.Arch = PeArch(javaw);
            if (string.IsNullOrEmpty(info.Version)) return null;
            info.Major = ParseMajor(info.Version);
            if (info.Major <= 0) return null;

            info.IsJdk = rel.TryGetValue("IMAGE_TYPE", out var img)
                ? img.Equals("JDK", StringComparison.OrdinalIgnoreCase)
                : File.Exists(Path.Combine(home, "bin", "javac.exe"));
            info.Vendor = NiceVendor(info.Vendor);
            return info;
        }

        static Dictionary<string, string> ReadRelease(string home)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var f = Path.Combine(home, "release");
            try
            {
                if (!File.Exists(f)) return d;
                foreach (var line in File.ReadAllLines(f))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim().Trim('"').Trim();
                    if (k.Length > 0 && v.Length > 0 && !d.ContainsKey(k)) d[k] = v;
                }
            }
            catch { }
            return d;
        }

        static readonly Regex PropLine = new Regex(@"^\s*([\w.]+) = (.*)$", RegexOptions.Compiled);
        static readonly Regex VersionLine = new Regex(@"version ""([^""]+)""", RegexOptions.Compiled);

        /// <summary>运行 java -XshowSettings:properties -version（隐藏窗口，5 秒超时）</summary>
        static Dictionary<string, string> RunForProperties(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, "-XshowSettings:properties -version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                foreach (var v in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS" })
                    psi.EnvironmentVariables.Remove(v);

                var sb = new StringBuilder();
                using (var p = new Process { StartInfo = psi })
                {
                    DataReceivedEventHandler h = (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += h;
                    p.OutputDataReceived += h;
                    p.Start();
                    p.BeginErrorReadLine();
                    p.BeginOutputReadLine();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        Log.Warn($"运行 {exe} 超时（5 秒）");
                        return null;
                    }
                    p.WaitForExit(); // 等异步读取把输出读完
                }

                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string text;
                lock (sb) text = sb.ToString();
                foreach (var line in text.Split('\n'))
                {
                    var m = PropLine.Match(line.TrimEnd('\r'));
                    if (m.Success && !d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                }
                if (!d.ContainsKey("java.version"))
                {
                    var m = VersionLine.Match(text);
                    if (m.Success) d["java.version"] = m.Groups[1].Value;
                }
                return d.Count > 0 ? d : null;
            }
            catch (Exception ex)
            {
                Log.Warn($"运行 {exe} 失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>"1.8.0_402" → 8，"17.0.8" → 17，"8u51" → 8，"25-ea" → 25</summary>
        public static int ParseMajor(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return 0;
            var v = version.Trim();
            if (v.StartsWith("1.") && v.Length > 2) v = v.Substring(2);
            int n = 0, i = 0;
            while (i < v.Length && char.IsDigit(v[i]) && i < 4) n = n * 10 + (v[i++] - '0');
            return n;
        }

        static string NormArch(string a)
        {
            if (string.IsNullOrWhiteSpace(a)) return null;
            switch (a.Trim().ToLowerInvariant())
            {
                case "amd64": case "x86_64": case "x64": case "x86-64": return "x64";
                case "aarch64": case "arm64": return "arm64";
                case "x86": case "i386": case "i486": case "i586": case "i686": case "x32": return "x86";
                default: return null;
            }
        }

        /// <summary>读 PE 头里的机器类型判断 exe 的架构</summary>
        public static string PeArch(string exe)
        {
            try
            {
                using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[4096];
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n < 0x40 || buf[0] != 'M' || buf[1] != 'Z') return null;
                    int pe = BitConverter.ToInt32(buf, 0x3C);
                    if (pe <= 0 || pe + 6 > n) return null;
                    if (buf[pe] != 'P' || buf[pe + 1] != 'E' || buf[pe + 2] != 0 || buf[pe + 3] != 0) return null;
                    switch (BitConverter.ToUInt16(buf, pe + 4))
                    {
                        case 0x8664: return "x64";
                        case 0x014C: return "x86";
                        case 0xAA64: return "arm64";
                        default: return null;
                    }
                }
            }
            catch { return null; }
        }

        static string NiceVendor(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            if (v.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return null;
            var l = v.ToLowerInvariant();
            if (l.StartsWith("azul")) return "Azul Zulu";
            if (l.StartsWith("amazon")) return "Amazon Corretto";
            if (l.StartsWith("oracle")) return "Oracle";
            if (l.StartsWith("bellsoft")) return "BellSoft Liberica";
            if (l.Contains("international business machines") || l.StartsWith("ibm")) return "IBM Semeru";
            if (l.StartsWith("alibaba")) return "Alibaba Dragonwell";
            if (l.StartsWith("tencent")) return "Tencent Kona";
            if (l.StartsWith("huawei")) return "Huawei BiSheng";
            if (l.StartsWith("red hat")) return "Red Hat";
            if (l.StartsWith("jetbrains")) return "JetBrains Runtime";
            if (l == "temurin") return "Eclipse Adoptium";
            return v;
        }

        // =====================================================================
        // 手动添加
        // =====================================================================

        /// <summary>手动添加一个 Java（保存在设置 java.extra 里）。不是有效 Java 时返回 null，不保存。</summary>
        public static JavaInfo AddManual(string path)
        {
            var info = Inspect(path);
            if (info == null)
            {
                Log.Warn($"手动添加的 Java 无效：{path}");
                return null;
            }
            var list = Settings.ExtraList(ExtraKey);
            if (!list.Any(p => SameHome(ResolveHome(p) ?? p, info.Home)))
            {
                list.Add(info.Home);
                Settings.SetList(ExtraKey, list);
            }
            var known = _known.ToList();
            var existing = known.FirstOrDefault(j => SameHome(j.Home, info.Home));
            if (existing != null) return existing;
            info.Source = SrcManual;
            known.Add(info);
            Sort(known);
            _known = known.AsReadOnly();
            Log.Info($"手动添加 Java：{info.Display}（{info.Home}）");
            return info;
        }

        public static void RemoveManual(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var home = ResolveHome(path) ?? NormDir(path);
            var list = Settings.ExtraList(ExtraKey);
            int n = list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) || SameHome(ResolveHome(p) ?? NormDir(p), home));
            if (n > 0) Settings.SetList(ExtraKey, list);
            _known = _known.Where(j => !(j.Source == SrcManual && SameHome(j.Home, home))).ToList().AsReadOnly();
        }

        // =====================================================================
        // 挑选
        // =====================================================================

        /// <summary>
        /// 为游戏挑一个最合适的 Java。
        /// 需要 8：只接受 Java 8（老 Forge / LaunchWrapper 在 9+ 上必崩），完全没有 8 时才退而求其次（forgeOld 时不退）；
        /// 需要 16/17：接受 17–21；需要 21 及以上：接受 ≥ 需要的版本，越接近越好。
        /// 同等条件下：64 位优先、与系统架构一致优先、本启动器下载的优先、小版本号高的优先。
        /// </summary>
        public static JavaInfo Pick(int requiredMajor, bool forgeOld = false, IEnumerable<JavaInfo> candidates = null)
        {
            int req = forgeOld ? 8 : NormalizeRequired(requiredMajor);
            return PickCore(req, candidates ?? Known, allowFallback: !forgeOld);
        }

        static int NormalizeRequired(int m) => m <= 8 ? 8 : m;

        /// <summary>这个 Java 能不能按规则用于需要 req 的游戏（不含兜底）</summary>
        public static bool IsAcceptable(JavaInfo j, int requiredMajor, bool forgeOld = false)
        {
            if (j == null) return false;
            int req = forgeOld ? 8 : NormalizeRequired(requiredMajor);
            if (req <= 8) return j.Major == 8;
            if (req <= 17) return j.Major >= req && j.Major <= 21;
            return j.Major >= req;
        }

        static JavaInfo PickCore(int req, IEnumerable<JavaInfo> candidates, bool allowFallback)
        {
            var os = Util.OsArch;
            var list = candidates.Where(j => j != null && j.Major > 0 && CanRun(j.Arch, os)).ToList();
            var ok = list.Where(j => IsAcceptable(j, req)).ToList();
            if (ok.Count == 0 && allowFallback) ok = list.Where(j => j.Major >= req).ToList();
            if (ok.Count == 0) return null;

            // ARM 设备上 1.20.5 之前的版本大多没有 arm64 原生库，用 x64 Java（模拟运行）更稳
            var preferArch = os == "arm64" && req < 21 ? "x64" : os;
            return ok
                .OrderBy(j => os != "x86" && j.Arch == "x86" ? 1 : 0)
                .ThenBy(j => j.Major == req ? 0 : 1)
                .ThenBy(j => j.Arch == preferArch || j.Arch == null ? 0 : 1)
                .ThenBy(j => Math.Abs(j.Major - req))
                .ThenBy(j => j.Source == SrcDownloaded ? 0 : 1)
                .ThenByDescending(j => j.Version ?? "", Comparer<string>.Create(Util.CompareVersion))
                .First();
        }

        /// <summary>这台电脑能不能运行这个架构的 Java</summary>
        static bool CanRun(string arch, string os)
        {
            if (arch == null) return true;
            switch (os)
            {
                case "x86": return arch == "x86";
                case "x64": return arch != "arm64";
                default: return true; // arm64 的 Windows 11 能模拟 x86 / x64
            }
        }

        // =====================================================================
        // 确保有可用的 Java
        // =====================================================================

        /// <summary>
        /// 找到或下载一个合适的 Java：设置里强制指定的（有效就用，不匹配只警告）→ 本机已有的 → 自动下载 Mojang 官方运行时。
        /// </summary>
        public static async Task<JavaInfo> EnsureAsync(int requiredMajor, string mojangComponent, bool forgeOld, LauncherTask task, double from, double to)
        {
            int req = forgeOld ? 8 : NormalizeRequired(requiredMajor);

            var forced = Settings.JavaPath;
            if (!IsAuto(forced))
            {
                var j = Inspect(forced);
                if (j != null)
                {
                    if (!IsAcceptable(j, req))
                        Log.Warn($"设置中指定的 {j.Display} 与游戏需要的 Java {req} 不匹配，仍按设置使用：{j.JavawPath}");
                    task?.Report(to);
                    return j;
                }
                Log.Warn($"设置中指定的 Java 无效（{forced}），改为自动选择");
            }

            task?.Report(from, $"查找 Java {req}…");
            var all = await ScanAsync().ConfigureAwait(false);
            var pick = PickCore(req, all, false);
            if (pick != null && !File.Exists(pick.JavawPath))
            {
                // 上次扫描之后被删掉了，重新扫一遍
                all = await ScanAsync(true).ConfigureAwait(false);
                pick = PickCore(req, all, false);
            }
            if (pick != null)
            {
                task?.Report(to);
                return pick;
            }

            var component = forgeOld ? "jre-legacy" : mojangComponent;
            try
            {
                Log.Info($"本机没有合适的 Java {req}，开始自动下载");
                return await DownloadAsync(req, component, task, from, to).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"自动下载 Java {req} 失败", ex);
                var fallback = forgeOld ? null : PickCore(req, all, true);
                if (fallback != null)
                {
                    Log.Warn($"改用不完全匹配的 {fallback.Display}（{fallback.JavawPath}）");
                    task?.Report(to);
                    return fallback;
                }
                var why = forgeOld ? $"这个版本的 Forge 只能用 Java 8 运行" : $"这个版本需要 Java {req}";
                throw new Exception($"{why}，本机没有找到，自动下载也失败了：{TaskManager.Flatten(ex)}\n请检查网络后重试，或自行安装 Java {req} 后在设置中指定。", ex);
            }
        }

        static bool IsAuto(string s) => string.IsNullOrWhiteSpace(s) || s.Equals("auto", StringComparison.OrdinalIgnoreCase) || s == "自动";

        // =====================================================================
        // 下载 Mojang 官方 Java 运行时
        // =====================================================================

        /// <summary>
        /// 下载 Mojang 官方 Java 运行时到 java\&lt;组件名&gt;\。component 为空或未知时按主版本号匹配。
        /// ARM 设备上 Mojang 没有对应组件时退回 x64 版（系统模拟运行）。
        /// </summary>
        public static async Task<JavaInfo> DownloadAsync(int major, string component, LauncherTask task, double from, double to)
        {
            var ct = task?.Token ?? CancellationToken.None;
            await DownloadLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                double span = to - from;
                task?.Report(from, $"获取 Java {major} 下载信息…");
                var index = await Http.GetJsonAsync(RuntimeIndexUrl, ct).ConfigureAwait(false);

                var os = Util.OsArch;
                var platforms = os == "arm64" ? new[] { "windows-arm64", "windows-x64" }
                              : os == "x86" ? new[] { "windows-x86" }
                              : new[] { "windows-x64" };
                var (platform, comp, entry) = FindRuntime(index, platforms, component, major);
                if (entry == null)
                    throw new Exception($"Mojang 没有提供适用于本机（{os}）的 Java {major}" + (string.IsNullOrEmpty(component) ? "" : $"（{component}）"));
                if (platform != platforms[0])
                    Log.Warn($"Mojang 没有 {platforms[0]} 版的 {component ?? "Java " + major}，改用 {platform} 版（由系统模拟运行）");

                var version = entry["version"]["name"].Str;
                var manifestUrl = entry["manifest"]["url"].Str;
                var manifestSha1 = entry["manifest"]["sha1"].Str;
                if (string.IsNullOrEmpty(manifestUrl)) throw new Exception("Java 运行时索引格式不正确");

                var home = Path.Combine(Paths.JavaRuntimes, Util.SafeFileName(comp));
                var marker = Path.Combine(home, MarkerFile);

                // 已经下载过同一份，直接用
                if (File.Exists(marker))
                {
                    JNode m = null;
                    try { m = JNode.ParseFile(marker); } catch { }
                    if (m != null && m["manifest"].Str == manifestSha1 && m["platform"].Str == platform)
                    {
                        var ready = InspectAndRegister(home);
                        if (ready != null)
                        {
                            task?.Report(to);
                            return ready;
                        }
                    }
                    // 平台变了（比如程序拷到了另一台电脑）就整个重下，避免残留另一种架构的文件
                    if (m != null && m["platform"].Str != platform) Util.TryDeleteDir(home);
                }
                Util.TryDelete(marker);

                Log.Info($"下载 Java {version}（{comp} / {platform}）到 {home}");
                var manifest = await Http.GetJsonAsync(manifestUrl, ct).ConfigureAwait(false);
                var files = manifest["files"];
                if (!files.IsObj || files.Count == 0) throw new Exception("Java 运行时文件清单为空");

                Directory.CreateDirectory(home);
                var homeFull = Path.GetFullPath(home).TrimEnd('\\') + "\\";
                var items = new List<DownloadItem>();
                var links = new List<(string link, string target)>();
                foreach (var kv in files.Props)
                {
                    var target = Path.GetFullPath(Path.Combine(home, kv.Key.Replace('/', '\\')));
                    if (!target.StartsWith(homeFull, StringComparison.OrdinalIgnoreCase)) continue; // 防目录穿越
                    switch (kv.Value["type"].Str)
                    {
                        case "directory":
                            Directory.CreateDirectory(target);
                            break;
                        case "file":
                            var raw = kv.Value["downloads"]["raw"];
                            if (raw["url"].Str == null) continue;
                            items.Add(new DownloadItem(raw["url"].Str, target, raw["sha1"].Str, raw["size"].Long));
                            break;
                        case "link":
                            if (kv.Value["target"].Str != null) links.Add((target, kv.Value["target"].Str));
                            break;
                    }
                }
                if (!items.Any(i => i.Path.EndsWith("\\bin\\javaw.exe", StringComparison.OrdinalIgnoreCase) || i.Path.EndsWith("\\bin\\java.exe", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("Java 运行时文件清单里没有 java.exe");

                await Downloader.RunAsync(items, task, from + span * 0.03, to - span * 0.02, $"下载 Java {major}").ConfigureAwait(false);

                // Windows 包里基本没有链接，有的话直接复制一份
                foreach (var (link, t) in links)
                {
                    try
                    {
                        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link), t.Replace('/', '\\')));
                        if (src.StartsWith(homeFull, StringComparison.OrdinalIgnoreCase) && File.Exists(src))
                        {
                            Util.EnsureParent(link);
                            File.Copy(src, link, true);
                        }
                    }
                    catch (Exception ex) { Log.Warn($"创建 Java 文件链接失败 {link}：{ex.Message}"); }
                }

                JNode.Obj()
                    .Set("component", comp)
                    .Set("platform", platform)
                    .Set("version", version)
                    .Set("major", major)
                    .Set("manifest", manifestSha1)
                    .Set("installed", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                    .SaveFile(marker);

                var info = InspectAndRegister(home);
                if (info == null) throw new Exception($"下载的 Java {version} 无法运行");
                Log.Info($"Java 下载完成：{info.Display}（{info.Home}）");
                task?.Report(to, $"Java {major} 已就绪");
                return info;
            }
            finally { DownloadLock.Release(); }
        }

        /// <summary>在索引里找运行时：每个平台先找指定组件，再按主版本号找；平台按优先级依次尝试</summary>
        static (string platform, string component, JNode entry) FindRuntime(JNode index, string[] platforms, string component, int major)
        {
            foreach (var p in platforms)
            {
                var plat = index[p];
                if (!plat.IsObj) continue;
                if (!string.IsNullOrEmpty(component))
                {
                    var e = plat[component][0];
                    if (e.IsObj && e["manifest"]["url"].Str != null) return (p, component, e);
                }
                (string, JNode) best = (null, null);
                foreach (var kv in plat.Props)
                {
                    // minecraft-java-exe 之类的不是 Java 运行时
                    if (!kv.Key.StartsWith("java-runtime", StringComparison.OrdinalIgnoreCase) && !kv.Key.StartsWith("jre", StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Key.EndsWith("-snapshot", StringComparison.OrdinalIgnoreCase)) continue;
                    var e = kv.Value[0];
                    if (!e.IsObj || e["manifest"]["url"].Str == null) continue;
                    if (ParseMajor(e["version"]["name"].Str) != major) continue;
                    if (best.Item2 == null || Util.CompareVersion(e["version"]["name"].Str, best.Item2["version"]["name"].Str) > 0)
                        best = (kv.Key, e);
                }
                if (best.Item2 != null) return (p, best.Item1, best.Item2);
            }
            return (null, null, null);
        }

        /// <summary>检查刚下载的运行时，并放进 Known 和缓存</summary>
        static JavaInfo InspectAndRegister(string home)
        {
            var h = ResolveHome(home);
            if (h == null) return null;
            LoadCache();
            var info = InspectHome(h, false);
            SaveCache();
            if (info == null) return null;
            info.Source = SrcDownloaded;
            var known = _known.Where(j => !SameHome(j.Home, h)).ToList();
            known.Add(info);
            Sort(known);
            _known = known.AsReadOnly();
            return info;
        }

        // =====================================================================
        // 缓存与工具
        // =====================================================================

        static string CacheFile => Path.Combine(Paths.Cache, "java.json");

        static void LoadCache()
        {
            lock (CacheLock)
            {
                if (_cache != null) return;
                _cache = new Dictionary<string, JNode>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (!File.Exists(CacheFile)) return;
                    var j = JNode.ParseFile(CacheFile);
                    if (j["format"].Int != 1) return;
                    foreach (var e in j["entries"])
                        if (e["home"].Str != null && e["stamp"].Str != null) _cache[e["home"].Str] = e;
                }
                catch (Exception ex) { Log.Warn("Java 缓存读取失败：" + ex.Message); }
            }
        }

        static void SaveCache()
        {
            lock (CacheLock)
            {
                if (_cache == null || !_cacheDirty) return;
                try
                {
                    var arr = JNode.Arr();
                    foreach (var kv in _cache)
                        if (Directory.Exists(kv.Key)) arr.Add(kv.Value);
                    JNode.Obj().Set("format", 1).Set("entries", arr).SaveFile(CacheFile);
                    _cacheDirty = false;
                }
                catch (Exception ex) { Log.Warn("Java 缓存保存失败：" + ex.Message); }
            }
        }

        static bool SameHome(string a, string b) =>
            a != null && b != null && string.Equals(NormDir(a), NormDir(b), StringComparison.OrdinalIgnoreCase);

        static string NormDir(string p)
        {
            try { return Path.GetFullPath(p.Trim().Trim('"')).TrimEnd('\\'); }
            catch { return p; }
        }

        static bool IsUnder(string path, string dir)
        {
            var d = NormDir(dir) + "\\";
            return NormDir(path).StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        /// <summary>解开符号链接 / 目录联接后的真实路径</summary>
        static string FinalPath(string path)
        {
            try
            {
                // 0 = 只查询属性；7 = 允许读/写/删共享；3 = OPEN_EXISTING；0x02000000 = FILE_FLAG_BACKUP_SEMANTICS
                using (var h = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    var sb = new StringBuilder(1024);
                    var n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0 || n >= sb.Capacity) return null;
                    var s = sb.ToString();
                    if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s.Substring(8);
                    if (s.StartsWith(@"\\?\")) return s.Substring(4);
                    return s;
                }
            }
            catch { return null; }
        }
    }
}
