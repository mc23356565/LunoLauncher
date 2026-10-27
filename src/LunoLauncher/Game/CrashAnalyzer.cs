using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LunoLauncher.Core;

namespace LunoLauncher.Game
{
    /// <summary>崩溃分析结果</summary>
    public class CrashResult
    {
        /// <summary>识别出的原因（大白话）</summary>
        public List<string> Reasons = new List<string>();
        /// <summary>具体怎么做</summary>
        public List<string> Suggestions = new List<string>();
        /// <summary>可疑的 Mod（名字 / ID / 文件名）</summary>
        public List<string> SuspectMods = new List<string>();
        /// <summary>日志里最关键的几行</summary>
        public string KeyLines;
        /// <summary>本次崩溃生成的崩溃报告</summary>
        public string CrashReportPath;
        /// <summary>本次崩溃生成的 JVM 崩溃日志（hs_err_pid*.log）</summary>
        public string HsErrPath;
        /// <summary>玩家自己按 F3+C 触发的崩溃</summary>
        public bool IsUserTriggered;

        public string Summary => Reasons.Count == 0 ? "没有识别出具体原因" : string.Join("；", Reasons);

        internal void Add(string reason, params string[] suggestions)
        {
            if (!string.IsNullOrEmpty(reason) && !Reasons.Contains(reason)) Reasons.Add(reason);
            foreach (var s in suggestions)
                if (!string.IsNullOrEmpty(s) && !Suggestions.Contains(s)) Suggestions.Add(s);
        }

        internal void Suspect(string mod)
        {
            if (string.IsNullOrWhiteSpace(mod)) return;
            mod = mod.Trim();
            if (!SuspectMods.Any(m => string.Equals(m, mod, StringComparison.OrdinalIgnoreCase))) SuspectMods.Add(mod);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("原因：");
            foreach (var r in Reasons) sb.AppendLine("  · " + r);
            if (Suggestions.Count > 0)
            {
                sb.AppendLine("建议：");
                foreach (var s in Suggestions) sb.AppendLine("  · " + s);
            }
            if (SuspectMods.Count > 0) sb.AppendLine("可疑的 Mod：" + string.Join("、", SuspectMods));
            if (IsUserTriggered) sb.AppendLine("（手动触发的崩溃）");
            if (!string.IsNullOrEmpty(CrashReportPath)) sb.AppendLine("崩溃报告：" + CrashReportPath);
            if (!string.IsNullOrEmpty(HsErrPath)) sb.AppendLine("JVM 崩溃日志：" + HsErrPath);
            if (!string.IsNullOrEmpty(KeyLines)) { sb.AppendLine(); sb.AppendLine("关键日志："); sb.AppendLine(KeyLines); }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 游戏崩溃分析：读游戏输出、latest.log、崩溃报告、hs_err，按规则给出原因与建议。
    /// </summary>
    public static class CrashAnalyzer
    {
        static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
        static Regex R(string pattern, RegexOptions o = RegexOptions.None) => new Regex(pattern, o | RegexOptions.CultureInvariant, RegexTimeout);

        /// <summary>分析时收集到的文本</summary>
        sealed class Ctx
        {
            public string Output = "", Latest = "", Crash = "", HsErr = "";
            public string All = "";
            public Instance Inst;
            public JavaInfo Java;
            public int ExitCode;
            public readonly List<string> Hits = new List<string>();

            public bool Has(string s) => All.IndexOf(s, StringComparison.Ordinal) >= 0;
            public bool HasI(string s) => All.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;

            /// <summary>把包含 s 的第一行记为关键行，返回是否找到</summary>
            public bool Hit(string s, bool ignoreCase = false)
            {
                var i = All.IndexOf(s, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (i < 0) return false;
                HitAt(i);
                return true;
            }

            public void HitAt(int index)
            {
                var line = LineAt(All, index).Trim();
                if (line.Length > 600) line = line.Substring(0, 600) + "…";
                if (line.Length > 0 && !Hits.Contains(line)) Hits.Add(line);
            }

            public IEnumerable<Match> Matches(Regex re)
            {
                MatchCollection mc;
                try { mc = re.Matches(All); }
                catch (RegexMatchTimeoutException) { yield break; }
                foreach (Match m in mc) yield return m;
            }

            public Match First(Regex re)
            {
                try { return re.Match(All); }
                catch (RegexMatchTimeoutException) { return Match.Empty; }
            }
        }

        static string LineAt(string text, int index)
        {
            if (index < 0 || index >= text.Length) return "";
            int s = text.LastIndexOf('\n', index);
            int e = text.IndexOf('\n', index);
            s = s < 0 ? 0 : s + 1;
            if (e < 0) e = text.Length;
            return text.Substring(s, e - s).TrimEnd('\r');
        }

        // =====================================================================
        // 分析
        // =====================================================================

        /// <summary>
        /// 分析一次崩溃。launchTime 用来找本次启动之后生成的崩溃报告 / hs_err / latest.log；
        /// 传 default(DateTime) 表示只分析 gameOutput 这段文本（比如用户手动选的日志文件），不读磁盘上的其他文件。
        /// </summary>
        public static CrashResult Analyze(Instance inst, string gameOutput, int exitCode, DateTime launchTime, JavaInfo java)
        {
            var r = new CrashResult();
            var c = new Ctx { Inst = inst, Java = java, ExitCode = exitCode, Output = gameOutput ?? "" };
            if (launchTime != default)
            {
                try { Collect(c, r, launchTime); }
                catch (Exception ex) { Log.Warn("收集崩溃日志失败：" + ex.Message); }
            }

            // 没找到崩溃报告文件，但文本里就有一份（游戏会把报告打印到输出里，或者分析的就是报告本身）
            bool crashInOutput = false;
            if (c.Crash.Length == 0)
            {
                var i = c.Output.IndexOf("---- Minecraft Crash Report ----", StringComparison.Ordinal);
                if (i >= 0) { c.Crash = c.Output.Substring(i); crashInOutput = true; }
            }

            c.All = StripColors(string.Join("\n", c.Output, c.Latest, crashInOutput ? "" : c.Crash, c.HsErr));

            foreach (var rule in Rules)
            {
                try
                {
                    if (rule(c, r)) break; // 返回 true 表示后面的规则不用再看了
                }
                catch (Exception ex) { Log.Warn($"崩溃分析规则出错：{ex.Message}"); }
            }

            if (r.Reasons.Count == 0)
            {
                r.Suggestions.Clear();
                r.Add("没有识别出具体原因", "点击“导出错误报告”发给懂的人看");
                r.KeyLines = ErrorLines(c, 40);
            }
            else
            {
                r.KeyLines = c.Hits.Count > 0 ? string.Join("\n", c.Hits.Take(40)) : ErrorLines(c, 40);
            }
            Log.Info($"崩溃分析（退出代码 {exitCode}）：{r.Summary}" + (r.SuspectMods.Count > 0 ? "；可疑 Mod：" + string.Join("、", r.SuspectMods) : ""));
            return r;
        }

        static void Collect(Ctx c, CrashResult r, DateTime launchTime)
        {
            var inst = c.Inst;
            if (inst == null) return;
            var since = launchTime > DateTime.MinValue.AddMinutes(1) ? launchTime.AddSeconds(-5) : DateTime.MinValue;
            string gameDir = null;
            try { gameDir = inst.GameDir; } catch { }
            if (gameDir == null) return;

            // 崩溃报告：优先用输出里写明的路径
            string crash = null;
            var m = Regex.Match(c.Output, @"(?:Crash report saved to|Report saved to):?\s*(?:#@!@#)?\s*([^\r\n]+?\.txt)");
            if (m.Success)
            {
                var p = m.Groups[1].Value.Trim();
                if (!Path.IsPathRooted(p)) p = Path.Combine(gameDir, p);
                if (File.Exists(p)) crash = p;
            }
            if (crash == null) crash = Newest(Path.Combine(gameDir, "crash-reports"), "crash-*.txt", since) ?? Newest(Path.Combine(gameDir, "crash-reports"), "*.txt", since);
            if (crash != null)
            {
                r.CrashReportPath = crash;
                c.Crash = ReadTail(crash, 2 << 20);
            }

            // JVM 崩溃日志
            string hs = null;
            m = Regex.Match(c.Output, @"An error report file with more information is saved as:\s*#?\s*([^\r\n]+?hs_err_pid\d+\.log)");
            if (m.Success)
            {
                var p = m.Groups[1].Value.Trim();
                if (!Path.IsPathRooted(p)) p = Path.Combine(gameDir, p);
                if (File.Exists(p)) hs = p;
            }
            if (hs == null) hs = Newest(gameDir, "hs_err_pid*.log", since);
            if (hs == null && !string.Equals(gameDir, inst.Dir, StringComparison.OrdinalIgnoreCase)) hs = Newest(inst.Dir, "hs_err_pid*.log", since);
            if (hs != null)
            {
                r.HsErrPath = hs;
                c.HsErr = ReadTail(hs, 1 << 20);
            }

            // latest.log（游戏输出通常已经包含它；但 Forge 的部分日志只写文件）
            var latest = Path.Combine(gameDir, "logs", "latest.log");
            if (IsAfter(latest, since)) c.Latest = ReadTail(latest, 4 << 20);
            else
            {
                var debug = Path.Combine(gameDir, "logs", "debug.log");
                if (IsAfter(debug, since)) c.Latest = ReadTail(debug, 4 << 20);
            }
        }

        delegate bool Rule(Ctx c, CrashResult r);

        /// <summary>按优先级排列的规则</summary>
        static readonly Rule[] Rules =
        {
            UserTriggered,
            JavaTooOld,
            ClassFileVersion,
            OldForgeNewJava,
            Memory,
            FabricDependencies,
            ForgeDependencies,
            LanguageProvider,
            DuplicateMods,
            Mixin,
            ModuleConflict,
            MissingLibraryMod,
            OptiFineIncompatible,
            GraphicsDriver,
            NativeLibrary,
            MissingMainClass,
            CorruptJar,
            BrokenConfig,
            BadJvmOption,
            FabricGeneric,
            NonAsciiPath,
            SuspectFromCrashReport,
            NativeCrash,
        };

        // ---------------------------------------------------------------------

        static bool UserTriggered(Ctx c, CrashResult r)
        {
            if (!c.Hit("Manually triggered debug crash")) return false;
            r.IsUserTriggered = true;
            r.Add("这是手动触发的调试崩溃（长按了 F3 + C）", "这不是故障，重新启动游戏即可；如果是误触，下次注意不要长按 F3 + C");
            return true;
        }

        static readonly Regex ReUnsupportedClass = R(@"UnsupportedClassVersionError:?\s*([^\s:]+)?[^\r\n]*?(?:class file version (\d+)(?:\.\d+)?|Unsupported major\.minor version (\d+))");
        static readonly Regex ReRequiresJava = R(@"(?:requires?|needs?)\s+(?:at least\s+)?Java\s*(\d{1,2})\b|\bJava\s*(\d{1,2}) (?:or (?:above|later|newer|higher) )?is required|requires version (\d{1,2})[\d.]*(?: or later)? of (?:mod )?'?java'?\b", RegexOptions.IgnoreCase);

        static bool JavaTooOld(Ctx c, CrashResult r)
        {
            int need = 0;
            string who = null;
            var m = c.First(ReUnsupportedClass);
            if (m.Success)
            {
                c.HitAt(m.Index);
                var v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (int.TryParse(v, out var cls)) need = cls - 44;
                var name = m.Groups[1].Success ? m.Groups[1].Value.Replace('/', '.') : null;
                if (name != null)
                    who = name.StartsWith("net.minecraft.") || name.StartsWith("com.mojang.") ? "Minecraft 本体" : $"某个 Mod（{name}）";
            }
            else
            {
                foreach (Match x in c.Matches(ReRequiresJava))
                {
                    var g = x.Groups[1].Success ? x.Groups[1] : x.Groups[2].Success ? x.Groups[2] : x.Groups[3];
                    if (!int.TryParse(g.Value, out var n) || n < 8 || n > 40) continue;
                    if (c.Java != null && c.Java.Major >= n) continue;
                    if (n > need) { need = n; c.HitAt(x.Index); }
                }
            }
            if (need < 8) return false;
            if (c.Java != null && c.Java.Major >= need && !m.Success) return false;

            var cur = c.Java != null ? $"，但当前使用的是 Java {c.Java.Major}" : "";
            r.Add($"Java 版本太旧：{who ?? "游戏或 Mod"}需要 Java {need} 或更高版本{cur}",
                $"在设置中把 Java 改为「自动选择」，启动器会自动下载合适的 Java {need}",
                $"或者自行安装 Java {need}（推荐 64 位）后在设置中选择它");
            return false;
        }

        static readonly Regex ReClassFileMajor = R(@"Unsupported class file major version (\d+)");

        static bool ClassFileVersion(Ctx c, CrashResult r)
        {
            var m = c.First(ReClassFileMajor);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var cls)) return false;
            c.HitAt(m.Index);
            int need = cls - 44;
            int rec = RecommendedJava(c);
            var java = c.Java;
            if (java != null && java.Major < need)
            {
                r.Add($"Java 版本太旧：有文件需要 Java {need}，但当前使用的是 Java {java.Major}",
                    $"在设置中把 Java 改为「自动选择」，或安装 Java {need} 及以上版本");
            }
            else if (java != null && rec > 0 && java.Major > rec)
            {
                r.Add($"Java 版本太新：这个版本的 Mod 加载器不支持 Java {java.Major}（它推荐 Java {rec}）",
                    $"在设置中把 Java 改为「自动选择」，或手动选择 Java {rec}");
            }
            else
            {
                r.Add($"Mod 加载器版本太旧，无法读取用 Java {need} 编译的 Mod",
                    "把 Forge / NeoForge / Fabric 更新到这个游戏版本的最新版",
                    "或者删除最近新加的、可能不适配当前游戏版本的 Mod");
            }
            return false;
        }

        static bool OldForgeNewJava(Ctx c, CrashResult r)
        {
            bool castFail = c.Has("ClassLoaders$AppClassLoader") && c.Has("URLClassLoader") && c.Hit("ClassLoaders$AppClassLoader");
            bool ucp = c.Hit("NoSuchFieldException: ucp");
            if (!castFail && !ucp) return false;
            var cur = c.Java != null ? $"，当前是 Java {c.Java.Major}" : "";
            r.Add($"这个版本的 Forge（LaunchWrapper）只能在 Java 8 上运行{cur}",
                "在设置中把 Java 改为「自动选择」（启动器会自动下载 Java 8），或手动选择一个 Java 8");
            return false;
        }

        static bool Memory(Ctx c, CrashResult r)
        {
            bool reserve = c.Hit("Could not reserve enough space");
            bool native = c.Hit("There is insufficient memory for the Java Runtime Environment") | c.Hit("Native memory allocation (malloc) failed") | c.Hit("Native memory allocation (mmap) failed");
            bool heap = c.Hit("java.lang.OutOfMemoryError") | c.Hit("Java heap space") | c.Hit("GC overhead limit exceeded");
            bool invalid = c.Hit("Invalid maximum heap size") | c.Hit("Invalid initial heap size");
            if (!(reserve || native || heap || invalid)) return false;

            var (total, avail) = Util.Memory();
            if (invalid)
            {
                r.Add("内存参数写错了（-Xmx / -Xms 无效）", "检查设置里的内存大小和自定义 JVM 参数，例如 -Xmx4G 而不是 -Xmx4");
                return false;
            }
            if (c.Java != null && !c.Java.Is64Bit)
            {
                if (Util.OsArch == "x86")
                    r.Add("使用的是 32 位系统和 32 位 Java，最多只能给游戏分配约 1 GB 内存",
                        "减少 Mod 数量、关闭光影、降低渲染距离；有条件的话换成 64 位系统");
                else
                    r.Add("使用的是 32 位 Java，最多只能给游戏分配约 1 GB 内存",
                        "在设置中把 Java 改为「自动选择」或选择一个 64 位 Java（启动器可自动下载）");
                return false;
            }
            if (reserve)
                r.Add("分配给游戏的内存超过了电脑当前能提供的内存",
                    $"在设置中调低游戏内存（电脑共 {total / 1024.0:0.#} GB 内存，当前可用约 {avail / 1024.0:0.#} GB）",
                    "关闭浏览器等占内存的程序，或调大 Windows 虚拟内存（页面文件）");
            if (native)
                r.Add("电脑可用内存不足，Java 申请不到内存",
                    "关闭其他占内存的程序，或调大 Windows 虚拟内存（页面文件）",
                    "游戏内存不要分配得过多，否则会挤占系统和显卡驱动需要的内存");
            if (heap && !reserve && !native)
                r.Add("游戏内存不足（分配给游戏的内存不够用）",
                    "在设置中调高游戏内存：原版 2–4 GB，大型整合包建议 6–8 GB",
                    total > 0 && total < 8192 ? $"电脑只有 {total / 1024.0:0.#} GB 内存，建议减少 Mod、关闭光影、降低渲染距离" : null);
            return false;
        }

        // ----- Fabric / Quilt 依赖 -----

        static readonly Regex ReFabricMissing = R(@"Mod '([^'\r\n]+)' \(([^)\r\n]+)\)[^\r\n]*? requires ([^\r\n]+?) of (?:mod )?([^\r\n]+?),? which is missing");
        static readonly Regex ReFabricWrong = R(@"Mod '([^'\r\n]+)' \(([^)\r\n]+)\)[^\r\n]*? requires ([^\r\n]+?) of (?:mod )?([^\r\n]+?), but only the wrong version is present: ([^\r\n!]+)");
        static readonly Regex ReFabricBreaks = R(@"Mod '([^'\r\n]+)' \(([^)\r\n]+)\)[^\r\n]*? is incompatible with ([^\r\n]+?) of (?:mod )?([^\r\n]+?), but a matching version is present: ([^\r\n!]+)");
        static readonly Regex ReFabricOld = R(@"Could not find required mod: (\S+) requires \{(\S+) @ \[([^\]]*)\]\}");
        static readonly Regex ReGenericMissing = R(@"(?:mod )?'?([\w\-.]+)'? requires (?:mod )?'?([\w\-.]+)'?[^\r\n]{0,80}? which is missing", RegexOptions.IgnoreCase);
        static readonly Regex ReNameId = R(@"^'([^']+)' \(([^)]+)\)");

        static bool FabricDependencies(Ctx c, CrashResult r)
        {
            bool header = c.Hit("Incompatible mods found!") | c.Hit("Mod resolution encountered an incompatible mod set") |
                          c.Hit("Some of your mods are incompatible") | c.Hit("Mod resolution failed") | c.Hit("Could not find required mod:");
            var tips = new List<string>();

            foreach (Match m in c.Matches(ReFabricMissing))
            {
                c.HitAt(m.Index);
                var mod = m.Groups[1].Value; var dep = DepName(m.Groups[4].Value); var ver = Ver(m.Groups[3].Value);
                r.Suspect(mod);
                tips.Add(DepTip(mod, dep.name, dep.id, ver, null));
            }
            foreach (Match m in c.Matches(ReFabricWrong))
            {
                c.HitAt(m.Index);
                var mod = m.Groups[1].Value; var dep = DepName(m.Groups[4].Value); var ver = Ver(m.Groups[3].Value);
                r.Suspect(mod);
                tips.Add(DepTip(mod, dep.name, dep.id, ver, m.Groups[5].Value.Trim()));
            }
            foreach (Match m in c.Matches(ReFabricBreaks))
            {
                c.HitAt(m.Index);
                var mod = m.Groups[1].Value; var dep = DepName(m.Groups[4].Value);
                r.Suspect(mod); r.Suspect(dep.name);
                tips.Add($"「{mod}」与「{dep.name}」{m.Groups[5].Value.Trim()} 不兼容，请删除其中一个，或换成兼容的版本");
            }
            foreach (Match m in c.Matches(ReFabricOld))
            {
                c.HitAt(m.Index);
                r.Suspect(m.Groups[1].Value);
                tips.Add(DepTip(m.Groups[1].Value, m.Groups[2].Value, m.Groups[2].Value, string.IsNullOrEmpty(m.Groups[3].Value) || m.Groups[3].Value == "*" ? "任意版本" : m.Groups[3].Value, null));
            }
            if (tips.Count == 0 && (header || c.Has("which is missing")))
            {
                foreach (Match m in c.Matches(ReGenericMissing))
                {
                    if (m.Value.Contains("Mod ID:")) continue;
                    c.HitAt(m.Index);
                    r.Suspect(m.Groups[1].Value);
                    tips.Add($"「{m.Groups[1].Value}」缺少前置 Mod「{m.Groups[2].Value}」，请下载安装");
                }
            }
            if (tips.Count == 0 && !header) return false;

            r.Add("Mod 依赖关系不满足：缺少前置 Mod，或 Mod 之间版本不兼容", tips.Distinct().Take(12).ToArray());
            if (tips.Count == 0) r.Add(null, "查看下方关键日志，按照提示安装缺少的前置 Mod 或删除不兼容的 Mod");
            return false;
        }

        static (string name, string id) DepName(string raw)
        {
            raw = raw.Trim().TrimEnd(',', '!');
            var m = ReNameId.Match(raw);
            if (m.Success) return (m.Groups[1].Value, m.Groups[2].Value);
            raw = raw.Trim('\'');
            return (raw, raw);
        }

        static string Ver(string s)
        {
            s = s.Trim();
            if (s.Equals("any version", StringComparison.OrdinalIgnoreCase)) return "任意版本";
            var m = Regex.Match(s, @"^version (.+?) or later$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + " 及以上版本";
            m = Regex.Match(s, @"^version (.+?) or earlier$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + " 及以下版本";
            m = Regex.Match(s, @"^(?:a )?version (.+)$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + " 版本";
            return s;
        }

        static readonly HashSet<string> LoaderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "fabricloader", "fabric-loader", "quilt_loader", "quilt-loader", "forge", "neoforge", "javafml" };

        static string DepTip(string mod, string depName, string depId, string ver, string present)
        {
            if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
                return $"「{mod}」需要 Minecraft {ver}" + (present != null ? $"，当前是 {present}" : "") + "：它不适用于这个游戏版本，请换成对应版本的 Mod 或删除它";
            if (depId.Equals("java", StringComparison.OrdinalIgnoreCase))
                return $"「{mod}」需要 Java {ver}" + (present != null ? $"，当前是 {present}" : "") + "：在设置中把 Java 改为「自动选择」";
            if (LoaderIds.Contains(depId))
                return $"「{mod}」需要 {depName} {ver}" + (present != null ? $"，当前是 {present}" : "") + "：请更新 Mod 加载器，或换用旧一点的 Mod 版本";
            if (present == null)
                return $"「{mod}」缺少前置 Mod「{depName}」（需要{(ver.StartsWith("任意") ? "" : " ")}{ver}），请下载安装";
            return $"「{mod}」需要「{depName}」{ver}，但安装的是 {present}，请更新或更换「{depName}」";
        }

        // ----- Forge / NeoForge 依赖 -----

        static readonly Regex ReForgeDep = R(@"Mod ID: '([^']+)', Requested by: '([^']+)', Expected range: '([^']*)', Actual version: '([^']*)'");
        static readonly Regex ReForgeOldDep = R(@"Mod (\S+) requires (\S+) ([^\r\n]+?)\s*(?:\r?\n\s*)?Currently, \S+ is ([^\r\n]+)");
        static readonly Regex ReForge112Dep = R(@"MissingModsException: Mod (\S+) \(([^)]+)\) requires \[([^\]]+)\]");

        static bool ForgeDependencies(Ctx c, CrashResult r)
        {
            bool header = c.Hit("Missing or unsupported mandatory dependencies") | c.Hit("MissingModsException");
            var tips = new List<string>();
            foreach (Match m in c.Matches(ReForgeDep))
            {
                c.HitAt(m.Index);
                string id = m.Groups[1].Value, by = m.Groups[2].Value, range = Range(m.Groups[3].Value), actual = m.Groups[4].Value;
                r.Suspect(by);
                bool missing = actual.IndexOf("MISSING", StringComparison.OrdinalIgnoreCase) >= 0 || actual.Length == 0;
                tips.Add(DepTip(by, id, id, range, missing ? null : actual));
            }
            foreach (Match m in c.Matches(ReForgeOldDep))
            {
                c.HitAt(m.Index);
                string by = m.Groups[1].Value, id = m.Groups[2].Value, range = m.Groups[3].Value.Trim().TrimEnd('.'), state = m.Groups[4].Value.Trim().TrimEnd('.');
                r.Suspect(by);
                bool missing = state.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0;
                tips.Add(DepTip(by, id, id, range.Replace(" or above", " 及以上版本"), missing ? null : state));
            }
            foreach (Match m in c.Matches(ReForge112Dep))
            {
                c.HitAt(m.Index);
                var by = m.Groups[2].Value;
                r.Suspect(by);
                foreach (var dep in m.Groups[3].Value.Split(','))
                {
                    var p = dep.Trim().Split(new[] { '@' }, 2);
                    if (p[0].Length == 0) continue;
                    tips.Add(DepTip(by, p[0], p[0], p.Length > 1 ? Range(p[1]) : "任意版本", null));
                }
            }
            if (tips.Count == 0 && !header) return false;
            r.Add("缺少前置 Mod，或 Mod 与游戏 / 加载器的版本不匹配", tips.Distinct().Take(12).ToArray());
            if (tips.Count == 0) r.Add(null, "查看下方关键日志，按照提示安装缺少的前置 Mod");
            return false;
        }

        /// <summary>Maven 版本范围转成大白话："[9.1.12,)" → "9.1.12 及以上版本"</summary>
        static string Range(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0 || s == "*" || s == "[*]" || s == "(,)") return "任意版本";
            var m = Regex.Match(s, @"^\[([^,\]]+),\s*\)$");
            if (m.Success) return m.Groups[1].Value + " 及以上版本";
            m = Regex.Match(s, @"^\[([^,\]]+),\s*([^\]\)]+)\)$");
            if (m.Success) return $"{m.Groups[1].Value} ~ {m.Groups[2].Value}（不含）版本";
            m = Regex.Match(s, @"^\[([^,\]]+),\s*([^\]\)]+)\]$");
            if (m.Success) return $"{m.Groups[1].Value} ~ {m.Groups[2].Value} 版本";
            m = Regex.Match(s, @"^\[([^,\]]+)\]$");
            if (m.Success) return m.Groups[1].Value + " 版本";
            return s;
        }

        static readonly Regex ReLanguageProvider = R(@"Mod File: ([^\r\n]+?) needs language provider (\S+?):(\S+) or above to load");
        static readonly Regex ReFabricOnForge = R(@"([^\s\\/'""]+\.jar)[^\r\n]{0,80}?(?:is a Fabric mod|Fabric mod)[^\r\n]{0,40}?(?:cannot be loaded|is not supported|not supported)", RegexOptions.IgnoreCase);

        static bool LanguageProvider(Ctx c, CrashResult r)
        {
            foreach (Match m in c.Matches(ReLanguageProvider))
            {
                c.HitAt(m.Index);
                var file = FileNameOf(m.Groups[1].Value);
                r.Suspect(file);
                r.Add("有 Mod 需要更新版本的 Forge / NeoForge（多半是装错了游戏版本的 Mod）",
                    $"「{file}」需要 {m.Groups[2].Value} {m.Groups[3].Value} 及以上：请换成适配当前游戏版本的 Mod，或更新加载器");
            }
            foreach (Match m in c.Matches(ReFabricOnForge))
            {
                c.HitAt(m.Index);
                r.Suspect(m.Groups[1].Value);
                r.Add("把 Fabric 的 Mod 装到了 Forge / NeoForge 上", $"删除「{m.Groups[1].Value}」，或换成它的 Forge / NeoForge 版本");
            }
            return false;
        }

        // ----- 重复 Mod -----

        static readonly Regex ReDupForge = R(@"Mod ID: '([^']+)' from mod files: ([^\r\n]+)");
        static readonly Regex ReDup112 = R(@"Found a duplicate mod (\S+) at \[([^\]]+)\]");
        static readonly Regex ReDupFabric = R(@"Duplicate mandatory mods found for '([^'\r\n]+)'|Mod '[^'\r\n]+' \(([^)\r\n]+)\)[^\r\n]*? (?:is present more than once|is duplicated)");

        static bool DuplicateMods(Ctx c, CrashResult r)
        {
            bool header = c.Hit("DuplicateModsFoundException") | c.Hit("Found a duplicate mod") | c.Hit("Duplicate mods", true) |
                          c.Hit("Found duplicate mods", true) | c.Hit("Duplicate mandatory mods") | c.Hit("is present more than once");
            if (!header) return false;
            var tips = new List<string>();
            foreach (Match m in c.Matches(ReDupForge))
            {
                c.HitAt(m.Index);
                r.Suspect(m.Groups[1].Value);
                tips.Add($"「{m.Groups[1].Value}」装了不止一个：{m.Groups[2].Value.Trim()}，只保留一个");
            }
            foreach (Match m in c.Matches(ReDup112))
            {
                c.HitAt(m.Index);
                r.Suspect(m.Groups[1].Value);
                var files = string.Join("、", m.Groups[2].Value.Split(',').Select(FileNameOf));
                tips.Add($"「{m.Groups[1].Value}」装了不止一个：{files}，只保留一个");
            }
            if (tips.Count == 0)
                foreach (Match m in c.Matches(ReDupFabric))
                {
                    var id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (string.IsNullOrEmpty(id)) continue;
                    c.HitAt(m.Index);
                    r.Suspect(id);
                    tips.Add($"「{id}」装了不止一个，只保留一个版本");
                }
            tips.Add("打开 mods 文件夹，删除重复的 Mod（同一个 Mod 只保留一个版本）");
            r.Add("安装了重复的 Mod", tips.Distinct().Take(10).ToArray());
            return false;
        }

        // ----- Mixin -----

        static readonly string[] MixinKeys = { "Mixin apply failed", "MixinApplyError", "InvalidInjectionException", "mixin.injection.throwables", "Mixin apply for mod", "MixinTransformerError", "Critical injection failure", "InvalidMixinException", "MixinPrepareError" };
        static readonly Regex ReMixinMod = R(@"Mixin apply for mod ([\w\-.]+) failed|from mod ([\w\-.]+)");
        static readonly Regex ReMixinCfg = R(@"(?<![\w\-.])(?:mixins\.([\w\-]+)\.json|([\w\-.]+?)\.mixins?\.json)");

        static bool Mixin(Ctx c, CrashResult r)
        {
            if (!MixinKeys.Any(c.Has)) return false;
            var ids = new List<string>();
            foreach (var key in MixinKeys)
            {
                int from = 0, count = 0;
                while (count++ < 20)
                {
                    var i = c.All.IndexOf(key, from, StringComparison.Ordinal);
                    if (i < 0) break;
                    from = i + key.Length;
                    c.HitAt(i);
                    var line = LineAt(c.All, i);
                    foreach (Match m in ReMixinMod.Matches(line))
                        AddMixinId(ids, m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                    foreach (Match m in ReMixinCfg.Matches(line))
                        AddMixinId(ids, m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                }
            }
            foreach (var id in ids) r.Suspect(id);
            var mods = "「" + string.Join("」「", ids.Take(5)) + "」";
            r.Add((ids.Count > 0 ? $"Mod{mods}" : "某个 Mod ") + "的 Mixin 注入失败（通常是 Mod 与当前游戏版本、加载器或其他 Mod 不兼容）",
                ids.Count > 0 ? $"更新或删除{mods}，并确认下载的是对应 Minecraft {SafeMc(c)} 和当前加载器的版本" : "查看下方关键日志找出出错的 Mod，更新或删除它",
                "如果是刚装或刚更新的 Mod，先把它移出 mods 文件夹再试");
            return false;
        }

        static void AddMixinId(List<string> ids, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var id = raw.Trim('.', '-', '_');
            id = Regex.Replace(id, @"([.\-_](common|client|server|forge|fabric|neoforge|quilt|compat|core|main|init))+$", "", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(id, @"^fabric-[\w\-]+-v\d+$", RegexOptions.IgnoreCase)) id = "fabric-api";
            if (id.Length < 2 || id.Equals("mixins", StringComparison.OrdinalIgnoreCase) || id.Equals("minecraft", StringComparison.OrdinalIgnoreCase) || id.Equals("forge", StringComparison.OrdinalIgnoreCase)) return;
            if (!ids.Any(x => x.Equals(id, StringComparison.OrdinalIgnoreCase))) ids.Add(id);
        }

        // ----- 模块冲突 -----

        static readonly Regex ReModExport = R(@"Modules? (\S+) and (\S+) export package (\S+) to module (\S+)");
        static readonly Regex ReModContains = R(@"Module (\S+) contains package (\S+), module (\S+) exports package");
        static readonly Regex ReModNotFound = R(@"Module (\S+) not found, required by (\S+)");

        static bool ModuleConflict(Ctx c, CrashResult r)
        {
            bool header = c.Hit("java.lang.module.ResolutionException") | c.Hit("LayerInstantiationException") | c.Hit("java.lang.module.FindException");
            bool any = false;
            foreach (Match m in c.Matches(ReModExport))
            {
                any = true; c.HitAt(m.Index);
                r.Suspect(m.Groups[1].Value); r.Suspect(m.Groups[2].Value);
                r.Add($"Mod「{m.Groups[1].Value}」和「{m.Groups[2].Value}」包含了相同的代码（模块冲突）",
                    "通常是同一个 Mod 或前置库装了两份（或不同版本），删除其中一个后再试");
            }
            foreach (Match m in c.Matches(ReModContains))
            {
                any = true; c.HitAt(m.Index);
                r.Suspect(m.Groups[1].Value); r.Suspect(m.Groups[3].Value);
                r.Add($"Mod「{m.Groups[1].Value}」和「{m.Groups[3].Value}」包含了相同的代码（模块冲突）",
                    "通常是同一个 Mod 或前置库装了两份（或不同版本），删除其中一个后再试");
            }
            foreach (Match m in c.Matches(ReModNotFound))
            {
                any = true; c.HitAt(m.Index);
                r.Suspect(m.Groups[2].Value);
                r.Add("缺少前置 Mod 或运行库", $"「{m.Groups[2].Value}」需要「{m.Groups[1].Value}」，请安装它；如果是游戏自带的库，请补全文件");
            }
            if (header && !any)
                r.Add("Mod 之间存在模块冲突", "检查 mods 文件夹里是否有重复或同一前置库的多个版本");
            return false;
        }

        // ----- 缺少常见前置库 -----

        static readonly (string prefix, string name)[] KnownLibs =
        {
            ("net.fabricmc.fabric.api", "Fabric API"),
            ("org.quiltmc.qsl", "Quilted Fabric API (QSL)"),
            ("dev.architectury", "Architectury API"),
            ("me.shedaniel.architectury", "Architectury API"),
            ("me.shedaniel.clothconfig2", "Cloth Config API"),
            ("me.shedaniel.autoconfig", "Cloth Config API"),
            ("software.bernie.geckolib", "GeckoLib"),
            ("com.terraformersmc.modmenu", "Mod Menu"),
            ("dev.isxander.yacl", "YetAnotherConfigLib"),
            ("mezz.jei", "JEI"),
            ("top.theillusivec4.curios", "Curios API"),
            ("dev.emi.trinkets", "Trinkets"),
            ("net.blay09.mods.balm", "Balm"),
            ("dev.latvian.mods.kubejs", "KubeJS"),
            ("dev.latvian.mods.rhino", "Rhino"),
            ("com.teamresourceful.resourcefullib", "Resourceful Lib"),
            ("fuzs.puzzleslib", "Puzzles Lib"),
            ("net.darkhax.bookshelf", "Bookshelf"),
            ("vazkii.patchouli", "Patchouli"),
            ("dev.ftb.mods.ftblibrary", "FTB Library"),
            ("io.github.fabricators_of_create.porting_lib", "Porting Lib"),
            ("com.llamalad7.mixinextras", "MixinExtras"),
            ("team.lodestar.lodestone", "Lodestone"),
            ("com.simibubi.create", "Create"),
            ("me.jellysquid.mods.sodium", "Sodium"),
            ("net.caffeinemc.mods.sodium", "Sodium"),
            ("org.embeddedt.embeddium", "Embeddium"),
            ("com.mrcrayfish.framework", "Framework"),
            ("com.cobblemon", "Cobblemon"),
            ("snownee.kiwi", "Kiwi"),
            ("com.blamejared.crafttweaker", "CraftTweaker"),
            ("org.spongepowered.asm", "MixinBootstrap（老版本 Forge 需要）"),
            ("codechicken.lib", "CodeChicken Lib"),
            ("cofh.core", "CoFH Core"),
            ("net.minecraftforge.fml", null), // 加载器自身，不算
        };
        static readonly Regex ReMissingClass = R(@"(NoClassDefFoundError|ClassNotFoundException):?\s+([\w$/.]+)");

        static bool MissingLibraryMod(Ctx c, CrashResult r)
        {
            // 依赖规则已经列出缺什么了，不再重复
            if (r.Reasons.Any(x => x.StartsWith("Mod 依赖关系") || x.StartsWith("缺少前置 Mod，"))) return false;
            foreach (Match m in c.Matches(ReMissingClass))
            {
                // ClassNotFoundException 经常出现在可选联动的调试日志里，只认崩溃报告中的
                if (m.Groups[1].Value == "ClassNotFoundException" && (c.Crash.Length == 0 || c.Crash.IndexOf(m.Value, StringComparison.Ordinal) < 0)) continue;
                var cls = m.Groups[2].Value.Replace('/', '.');
                foreach (var (prefix, name) in KnownLibs)
                {
                    if (!cls.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;
                    if (name == null) break;
                    c.HitAt(m.Index);
                    r.Add($"缺少前置 Mod「{name}」", $"下载并安装「{name}」（选择与当前 Minecraft {SafeMc(c)} 和加载器对应的版本）");
                    break;
                }
            }
            return false;
        }

        static bool OptiFineIncompatible(Ctx c, CrashResult r)
        {
            bool hasOf = c.Has("at net.optifine.") || (c.Has("net.optifine.") && SafeLoaders(c).Any(l => l.Type == LoaderType.OptiFine));
            if (!hasOf) return false;
            if (!(c.Has("NoSuchMethodError") || c.Has("NoSuchFieldError") || c.Has("IncompatibleClassChangeError"))) return false;
            c.Hit("NoSuchMethodError"); c.Hit("NoSuchFieldError");
            r.Suspect("OptiFine");
            r.Add("OptiFine 与当前的 Forge 或其他 Mod 不兼容",
                "换一个与当前 Forge 版本匹配的 OptiFine（OptiFine 下载页会注明适配的 Forge 版本），或先移除 OptiFine",
                "也可以改用 Embeddium / Sodium + Oculus / Iris 代替 OptiFine");
            return false;
        }

        // ----- 显卡驱动 -----

        static readonly Regex ReProblemFrame = R(@"Problematic frame:\s*\r?\n#\s*\w+\s+\[([^\]+]+)");
        static readonly Regex ReOpenGl32 = R(@"[^\r\n]*OpenGL 3\.2[^\r\n]*", RegexOptions.IgnoreCase);

        static bool GraphicsDriver(Ctx c, CrashResult r)
        {
            string vendor = null;
            bool hit = c.Hit("Pixel format not accelerated") | c.Hit("GLFW error 65542") | c.Hit("GLFW error 65543") |
                       c.Hit("WGL: The driver does not appear to support OpenGL") | c.Hit("Couldn't set pixel format");
            foreach (Match m in c.Matches(ReOpenGl32))
            {
                var l = m.Value.ToLowerInvariant();
                if (l.Contains("require") || l.Contains("support") || l.Contains("couldn't") || l.Contains("could not") || l.Contains("not available") || l.Contains("failed"))
                {
                    hit = true; c.HitAt(m.Index);
                }
            }

            var dll = ProblemDll(c, false);
            if (dll != null)
            {
                vendor = GpuVendor(dll);
                if (vendor != null) { hit = true; ProblemDll(c, true); }
            }
            if (!hit) return false;

            var who = vendor != null ? vendor + " " : "";
            r.Add($"{who}显卡驱动出错，或不支持游戏需要的 OpenGL 版本" + (dll != null && vendor != null ? $"（崩溃发生在驱动文件 {dll}）" : ""),
                vendor switch
                {
                    "Intel" => "到 Intel 官网下载安装最新的核显驱动（不要只依赖 Windows 更新）",
                    "AMD" => "到 AMD 官网下载安装最新的显卡驱动（不要只依赖 Windows 更新）",
                    "NVIDIA" => "到 NVIDIA 官网或用 GeForce Experience / NVIDIA App 更新显卡驱动",
                    _ => "更新显卡驱动到最新版本（到显卡厂商官网下载，不要只依赖 Windows 更新）"
                },
                "如果电脑有独立显卡：Windows 设置 → 系统 → 屏幕 → 显示卡，把 javaw.exe 设为「高性能」",
                "远程桌面、虚拟机里通常没有显卡加速，无法运行游戏");
            return false;
        }

        /// <summary>JVM 崩溃时出问题的模块（hs_err 和游戏输出里都有这段）</summary>
        static string ProblemDll(Ctx c, bool record)
        {
            var m = c.First(ReProblemFrame);
            if (!m.Success) return null;
            if (record) c.HitAt(c.All.IndexOf('\n', m.Index) + 1);
            return m.Groups[1].Value.Trim();
        }

        static string GpuVendor(string dll)
        {
            var d = dll.ToLowerInvariant();
            if (Regex.IsMatch(d, @"^ig\w*icd(32|64)\.dll$") || d.StartsWith("igxelp") || d.StartsWith("igdumd") || d.StartsWith("igd10") || d.StartsWith("ig75icd") || d.StartsWith("ig4icd")) return "Intel";
            if (d.StartsWith("atio6axx") || d.StartsWith("atioglxx") || d.StartsWith("atig6pxx") || d.StartsWith("atiumd") || d.StartsWith("amdxx") || d.StartsWith("amdvlk")) return "AMD";
            if (d.StartsWith("nvoglv") || d.StartsWith("nvd3dum") || d.StartsWith("nvwgf2um")) return "NVIDIA";
            return null;
        }

        // ----- 本地库 -----

        static bool NativeLibrary(Ctx c, CrashResult r)
        {
            bool mismatch = c.Hit("Can't load IA 32-bit .dll on a AMD 64-bit platform") | c.Hit("Can't load AMD 64-bit .dll on a IA 32-bit platform") | c.Hit("Can't load ARM 64-bit .dll");
            bool missing = c.Hit("java.lang.UnsatisfiedLinkError") | c.Hit("Failed to locate library: lwjgl") | c.Hit("no lwjgl in java.library.path") | c.Hit("Failed to load a library. Possible solutions");
            if (!mismatch && !missing) return false;
            if (mismatch)
                r.Add("Java 的位数与游戏的本地库（natives）不一致",
                    "在设置中把 Java 改为「自动选择」，或选择一个与系统位数相同的 64 位 Java",
                    "然后在版本设置里点击「补全文件」");
            else
                r.Add("游戏的本地库（natives）缺失或损坏",
                    "在版本设置里点击「补全文件」，或删除版本文件夹里的 *-natives 文件夹后重新启动",
                    Util.OsArch == "arm64" ? "ARM 电脑上运行 1.19 之前的版本时，请使用 x64 版 Java" : null);
            return false;
        }

        // ----- 文件不完整 -----

        static readonly string[] MainClasses =
        {
            "net.minecraft.client.main.Main", "net.minecraft.launchwrapper.Launch", "cpw.mods.modlauncher.Launcher",
            "cpw.mods.bootstraplauncher.BootstrapLauncher", "net.fabricmc.loader.impl.launch.knot.KnotClient",
            "net.fabricmc.loader.launch.knot.KnotClient", "org.quiltmc.loader.impl.launch.knot.KnotClient",
            "net.minecraftforge.bootstrap.ForgeBootstrap", "net.neoforged.fml.startup.Client", "net.minecraft.client.Minecraft",
        };

        static bool MissingMainClass(Ctx c, CrashResult r)
        {
            bool hit = c.Hit("Could not find or load main class") | c.Hit("找不到或无法加载主类");
            var main = SafeMainClass(c);
            foreach (var mc in MainClasses.Concat(main != null ? new[] { main } : new string[0]).Distinct())
            {
                if (c.Hit("ClassNotFoundException: " + mc) || c.Hit("NoClassDefFoundError: " + mc.Replace('.', '/')))
                    hit = true;
            }
            if (!hit) return false;
            r.Add("游戏文件不完整（找不到启动所需的主类）",
                "在版本设置里点击「补全文件」后再启动；仍然不行就删除并重新安装这个版本",
                "安装加载器时如果中途失败，也会出现这个问题，重新安装一次加载器即可");
            return false;
        }

        static readonly Regex ReZipJar = R(@"([^\s\\/'""\[\]()]+\.jar)");

        static bool CorruptJar(Ctx c, CrashResult r)
        {
            var keys = new[] { "java.util.zip.ZipException", "invalid LOC header", "zip END header not found", "error in opening zip file", "zip file is empty", "Invalid or corrupt jarfile" };
            var files = new List<string>();
            bool hit = false;
            foreach (var k in keys)
            {
                int from = 0, n = 0;
                while (n++ < 10)
                {
                    var i = c.All.IndexOf(k, from, StringComparison.Ordinal);
                    if (i < 0) break;
                    from = i + k.Length;
                    var line = LineAt(c.All, i);
                    // 资源包 / 光影包损坏不会让游戏崩溃，只看致命的那几种
                    bool fatal = k == "Invalid or corrupt jarfile" || k == "error in opening zip file" || line.Contains("Caused by") ||
                                 line.Contains("Exception in thread") || (c.Crash.Length > 0 && c.Crash.Contains(line.Trim()));
                    if (!fatal || line.IndexOf("pack", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    hit = true;
                    c.HitAt(i);
                    foreach (Match m in ReZipJar.Matches(line))
                        if (!files.Contains(m.Groups[1].Value)) files.Add(m.Groups[1].Value);
                }
            }
            if (!hit) return false;
            foreach (var f in files) r.Suspect(f);
            r.Add("有文件损坏（压缩包读取失败）" + (files.Count > 0 ? "：" + string.Join("、", files.Take(3)) : ""),
                files.Count > 0 ? $"重新下载 {string.Join("、", files.Take(3))}（如果是 Mod 就重新下载它，如果是游戏库就点击「补全文件」）" : "在版本设置里点击「补全文件」；如果刚下载过 Mod，重新下载它们");
            return false;
        }

        // ----- 配置文件损坏 -----

        static readonly Regex ReConfigFile = R(@"Failed (?:loading|to load) config(?:uration)? file:? '?([^\s']+?\.(?:toml|json5?|cfg|properties|yml|yaml|snbt|txt))", RegexOptions.IgnoreCase);
        static readonly Regex ReConfigPath = R(@"((?:[A-Za-z]:)?[^\s'""<>|*?]*?(?:config|defaultconfigs|serverconfig)[\\/][^\s'""<>|*?]+?\.(?:toml|json5?|cfg|properties|yml|yaml|snbt))", RegexOptions.IgnoreCase);

        static bool BrokenConfig(Ctx c, CrashResult r)
        {
            var files = new List<string>();
            foreach (Match m in c.Matches(ReConfigFile))
            {
                c.HitAt(m.Index);
                files.Add(m.Groups[1].Value);
            }
            foreach (var key in new[] { "ParsingException", "ConfigLoadingException", "Failed loading config", "Error loading config", "Failed to load config" })
            {
                int from = 0, n = 0;
                while (n++ < 10)
                {
                    var i = c.All.IndexOf(key, from, StringComparison.Ordinal);
                    if (i < 0) break;
                    from = i + key.Length;
                    // 有的异常在下一行才给出文件路径，把附近三行一起看
                    var start = c.All.LastIndexOf('\n', i) + 1;
                    var end = start;
                    for (int k = 0; k < 3 && end >= 0 && end < c.All.Length; k++) end = c.All.IndexOf('\n', end + 1);
                    if (end < 0) end = c.All.Length;
                    var chunk = c.All.Substring(start, end - start);
                    var mm = ReConfigPath.Match(chunk);
                    if (mm.Success || key == "ParsingException" || key == "ConfigLoadingException") c.HitAt(i);
                    if (mm.Success) files.Add(mm.Groups[1].Value);
                }
            }
            if (files.Count == 0) return false;

            var shown = new List<string>();
            foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var full = LocateConfig(c, f);
                if (!shown.Contains(full, StringComparer.OrdinalIgnoreCase)) shown.Add(full);
            }
            r.Add("有配置文件损坏：" + string.Join("、", shown.Select(Path.GetFileName).Distinct().Take(3)),
                shown.Take(5).Select(f => $"删除 {f}（下次启动会自动重新生成默认配置）").ToArray());
            return false;
        }

        static string LocateConfig(Ctx c, string f)
        {
            try
            {
                f = f.Trim().Trim('\'', '"').Replace('/', '\\');
                if (Path.IsPathRooted(f)) return f;
                var gameDir = c.Inst?.GameDir;
                if (gameDir == null) return f;
                var direct = Path.Combine(gameDir, f);
                if (File.Exists(direct)) return direct;
                var inConfig = Path.Combine(gameDir, "config", f);
                if (File.Exists(inConfig)) return inConfig;
                var cfgDir = Path.Combine(gameDir, "config");
                if (Directory.Exists(cfgDir))
                {
                    var found = Directory.GetFiles(cfgDir, Path.GetFileName(f), SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) return found;
                }
                return f.Contains("\\") ? direct : inConfig;
            }
            catch { return f; }
        }

        // ----- JVM 参数 -----

        static readonly Regex ReBadOption = R(@"Unrecognized VM option '([^']+)'|Unrecognized option: (\S+)|Invalid -XX: option|Error: Could not create the Java Virtual Machine");

        static bool BadJvmOption(Ctx c, CrashResult r)
        {
            var m = c.First(ReBadOption);
            if (!m.Success) return false;
            c.HitAt(m.Index);
            var opt = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : null;
            if (opt == null && r.Reasons.Count > 0) return false; // 只是 "无法创建虚拟机"，原因前面已经找到了
            r.Add(opt != null ? $"Java 不认识启动参数「{opt}」" : "Java 虚拟机无法启动（启动参数有误）",
                "检查设置和版本设置中的自定义 JVM 参数，删除有问题的参数",
                "有些参数只有新版 Java 支持（例如 Java 8 不支持 --add-opens、ZGC 等），也可以把 Java 改为「自动选择」");
            return false;
        }

        // ----- Fabric 通用错误 -----

        static readonly Regex ReFormatted = R(@"net\.fabricmc\.loader\.impl\.FormattedException:\s*([^\r\n]+)");

        static bool FabricGeneric(Ctx c, CrashResult r)
        {
            var m = c.First(ReFormatted);
            if (!m.Success) return false;
            if (r.Reasons.Count > 0) return false; // 已经由更具体的规则解释过了
            c.HitAt(m.Index);
            var msg = m.Groups[1].Value.Trim();
            if (msg.Length > 200) msg = msg.Substring(0, 200) + "…";
            r.Add($"Fabric 加载 Mod 时出错：{msg}", "按照错误信息处理对应的 Mod（更新、删除或安装缺少的前置）", "拿不准的话点击“导出错误报告”发给懂的人看");
            return false;
        }

        // ----- 路径含中文 -----

        static bool NonAsciiPath(Ctx c, CrashResult r)
        {
            if (c.Inst == null) return false;
            string dir;
            try { dir = c.Inst.GameDir; } catch { return false; }
            if (dir == null || dir.All(ch => ch < 128)) return false;
            if (!(c.Has("java.io.FileNotFoundException") || c.Has("Invalid path") || c.Has("InvalidPathException") || c.Has("Malformed input or input contains unmappable characters"))) return false;
            var mc = SafeMc(c);
            bool old = (mc != null && mc != "?" && Util.CompareVersion(mc, "1.13") < 0) || (c.Java != null && c.Java.Major <= 8);
            if (!old) return false;
            c.Hit("java.io.FileNotFoundException"); c.Hit("Invalid path"); c.Hit("InvalidPathException");
            r.Add("游戏路径里有中文或特殊字符，旧版本游戏无法正确读取",
                "把游戏文件夹移动到只有英文和数字的路径（例如 D:\\Minecraft），版本名也不要用中文");
            return false;
        }

        // ----- 崩溃报告里的可疑 Mod -----

        static readonly Regex ReSuspectHeader = R(@"Suspected Mods?(?:\(s\))?:[ \t]*([^\r\n]*)");
        static readonly Regex ReSuspectLine = R(@"^\s+(.+?) \(([\w\-.]+)\), Version");
        static readonly Regex ReModSection = R(@"-- MOD ([\w\-.]+) --");
        static readonly Regex ReModFile = R(@"Mod File: ([^\r\n]+)");
        static readonly Regex ReTransformerFrame = R(@"(?:TRANSFORMER|MC-BOOTSTRAP|SECURE-BOOTSTRAP|LAYER PLUGIN|PLUGIN|GAME)/([\w\-.]+)@[\w\-.+]+/");
        static readonly HashSet<string> NotMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "minecraft", "forge", "neoforge", "java.base", "fmlloader", "fmlcore", "javafml", "lowcodefml", "mixin", "modlauncher", "securejarhandler", "bootstraplauncher", "jdk.internal", "eventbus", "coremods", "none", "unknown" };

        static bool SuspectFromCrashReport(Ctx c, CrashResult r)
        {
            if (string.IsNullOrEmpty(c.Crash)) return false;
            var text = c.Crash.Replace("\r", "");
            int before = r.SuspectMods.Count;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var h = ReSuspectHeader.Match(lines[i]);
                if (!h.Success) continue;
                var inline = h.Groups[1].Value.Trim();
                if (inline.Length > 0)
                {
                    foreach (var part in inline.Split(','))
                    {
                        var p = part.Trim();
                        if (p.Length > 0 && !NotMods.Contains(p)) r.Suspect(p);
                    }
                }
                for (int j = i + 1; j < lines.Length && j < i + 40; j++)
                {
                    if (lines[j].Length == 0 || !char.IsWhiteSpace(lines[j][0])) break;
                    var sm = ReSuspectLine.Match(lines[j]);
                    if (sm.Success && !NotMods.Contains(sm.Groups[2].Value)) r.Suspect($"{sm.Groups[1].Value} ({sm.Groups[2].Value})");
                }
            }
            // -- MOD xxx -- 段落：加载失败的 Mod
            foreach (Match m in ReModSection.Matches(text))
            {
                var id = m.Groups[1].Value;
                if (NotMods.Contains(id)) continue;
                var tail = text.Substring(m.Index, Math.Min(600, text.Length - m.Index));
                var f = ReModFile.Match(tail);
                var file = f.Success ? FileNameOf(f.Groups[1].Value) : null;
                r.Suspect(file != null && file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? $"{id} ({file})" : id);
            }
            // 崩溃报告第一段堆栈里出现的 Mod（Forge 1.17+ 会标注 TRANSFORMER/modid@version/）
            var desc = text.IndexOf("Description:", StringComparison.Ordinal);
            if (desc >= 0)
            {
                // Description 行之后空一行才是堆栈，堆栈到下一个空行为止
                var start = text.IndexOf('\n', desc);
                if (start < 0) start = text.Length;
                while (start < text.Length && text[start] == '\n') start++;
                var stackEnd = text.IndexOf("\n\n", start, StringComparison.Ordinal);
                var stack = text.Substring(start, (stackEnd < 0 ? text.Length : stackEnd) - start);
                int added = 0;
                foreach (Match m in ReTransformerFrame.Matches(stack))
                {
                    var id = m.Groups[1].Value;
                    if (NotMods.Contains(id) || id.StartsWith("java.") || id.StartsWith("jdk.") || added >= 3) continue;
                    if (r.SuspectMods.Any(s => s.Equals(id, StringComparison.OrdinalIgnoreCase) || s.Contains("(" + id + ")"))) continue;
                    r.Suspect(id);
                    added++;
                }
            }
            if (r.SuspectMods.Count > before && r.Reasons.Count == 0)
            {
                var descLine = desc >= 0 ? LineAt(text, desc).Trim() : null;
                var i = descLine != null ? c.All.IndexOf(descLine, StringComparison.Ordinal) : -1;
                if (i >= 0) c.HitAt(i);
                r.Add("游戏崩溃了，崩溃报告指向这些 Mod：" + string.Join("、", r.SuspectMods.Take(5)),
                    "尝试更新这些 Mod，或者暂时把它们移出 mods 文件夹后再启动",
                    "如果是整合包，可以去整合包的发布页反馈");
            }
            return false;
        }

        // ----- 原生崩溃 -----

        static bool NativeCrash(Ctx c, CrashResult r)
        {
            if (r.Reasons.Count > 0) return false;
            uint code = unchecked((uint)c.ExitCode);
            string kind = code switch
            {
                0xC0000005 => "内存访问冲突",
                0xC0000409 => "栈缓冲区溢出",
                0xC0000374 => "堆内存损坏",
                0xC00000FD => "栈溢出",
                _ => null
            };
            bool hs = !string.IsNullOrEmpty(c.HsErr) || c.Has("A fatal error has been detected by the Java Runtime Environment");
            if (kind == null && !hs) return false;
            var dll = hs ? ProblemDll(c, true) : null;
            if (hs) c.Hit("EXCEPTION_");
            var detail = dll != null ? $"出错的模块是 {dll}" : kind != null ? $"{kind}，退出代码 {c.ExitCode}" : "Java 虚拟机崩溃";
            r.Add($"游戏进程发生了原生崩溃（{detail}），通常与显卡驱动或注入游戏的软件有关",
                "更新显卡驱动到最新版本",
                "关闭可能注入游戏的软件：录屏 / 直播软件、游戏加速器、悬浮窗、杀毒软件的游戏模式等",
                "也可以试试换一个版本的 Java");
            return false;
        }

        // =====================================================================
        // 导出错误报告
        // =====================================================================

        /// <summary>把排查需要的所有东西打包成 zip 放到桌面，返回路径</summary>
        public static string ExportReport(Instance inst, string gameOutput, CrashResult result, JavaInfo java)
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Paths.Logs;
            var baseName = $"{Branding.AppName}错误报告-{DateTime.Now:yyyyMMdd-HHmmss}";
            var path = Path.Combine(dir, baseName + ".zip");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{baseName} ({i}).zip");

            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (result != null) AddText(zip, "分析结果.txt", result.ToString());
                AddText(zip, "环境信息.txt", EnvironmentText(inst, java));
                AddText(zip, "游戏输出.txt", Redact(gameOutput ?? ""));
                AddText(zip, "启动器日志.txt", Redact(Log.RecentText()));

                if (inst != null)
                {
                    string gameDir = null;
                    try { gameDir = inst.GameDir; } catch { }
                    if (gameDir != null)
                    {
                        AddFile(zip, Path.Combine(gameDir, "logs", "latest.log"), "logs/latest.log", 8 << 20);
                        AddFile(zip, Path.Combine(gameDir, "logs", "debug.log"), "logs/debug.log", 8 << 20);
                        var crash = result?.CrashReportPath ?? Newest(Path.Combine(gameDir, "crash-reports"), "*.txt", DateTime.Now.AddHours(-1));
                        if (crash != null) AddFile(zip, crash, "crash-reports/" + Path.GetFileName(crash), 4 << 20);
                        var hs = result?.HsErrPath ?? Newest(gameDir, "hs_err_pid*.log", DateTime.Now.AddHours(-1));
                        if (hs != null) AddFile(zip, hs, Path.GetFileName(hs), 4 << 20);
                    }

                    // 版本 JSON（含继承链）
                    var ids = new List<string> { inst.Id };
                    try { if (inst.IsValid) ids = inst.Info.Chain.ToList(); } catch { }
                    foreach (var id in ids.Distinct())
                    {
                        try { AddFile(zip, inst.Repo.VersionJson(id), $"versions/{Util.SafeFileName(id)}.json", 4 << 20); } catch { }
                    }
                    try { AddFile(zip, inst.SettingsFile, "版本设置.json", 1 << 20); } catch { }

                    AddText(zip, "Mod 列表.txt", ModList(inst));
                }
            }
            Log.Info("已导出错误报告：" + path);
            return path;
        }

        static string EnvironmentText(Instance inst, JavaInfo java)
        {
            var sb = new StringBuilder();
            var (total, avail) = Util.Memory();
            sb.AppendLine($"启动器：{Branding.DisplayName} {AppInfo.VersionString}");
            sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"系统：Windows {Util.OsVersion}（{Util.OsArch}）");
            sb.AppendLine($"内存：共 {total} MB，可用 {avail} MB");
            sb.AppendLine($"处理器线程数：{Environment.ProcessorCount}");
            if (java != null)
            {
                sb.AppendLine($"Java：{java.Display}");
                sb.AppendLine($"Java 路径：{java.JavawPath}（{java.Source}）");
            }
            else sb.AppendLine("Java：未知");
            if (inst != null)
            {
                sb.AppendLine($"版本名：{inst.Id}");
                try
                {
                    sb.AppendLine($"游戏版本：{inst.McVersion}");
                    sb.AppendLine($"加载器：{(inst.Loaders.Count == 0 ? "原版" : string.Join("，", inst.Loaders))}");
                    if (inst.IsValid) sb.AppendLine($"需要的 Java：{inst.Info.JavaMajor}");
                    sb.AppendLine($"游戏目录：{inst.GameDir}（版本隔离：{(inst.Isolated ? "是" : "否")}）");
                    var mem = inst.Settings["memoryMb"].Int;
                    sb.AppendLine($"内存设置：{(Settings.MemoryAuto ? "自动" : Settings.MemoryMb + " MB")}" + (mem > 0 ? $"（版本单独设置 {mem} MB）" : ""));
                }
                catch (Exception ex) { sb.AppendLine("读取版本信息失败：" + ex.Message); }
            }
            if (!string.IsNullOrWhiteSpace(Settings.JvmArgs)) sb.AppendLine($"自定义 JVM 参数：{Settings.JvmArgs}");
            return sb.ToString();
        }

        static string ModList(Instance inst)
        {
            var sb = new StringBuilder();
            try
            {
                var dir = inst.ModsDir;
                if (!Directory.Exists(dir)) return "（没有 mods 文件夹）";
                var files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList();
                foreach (var sub in Directory.GetDirectories(dir))
                    files.AddRange(Directory.GetFiles(sub, "*", SearchOption.TopDirectoryOnly));
                sb.AppendLine($"mods 文件夹：{dir}");
                sb.AppendLine($"共 {files.Count} 个文件");
                sb.AppendLine();
                foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(f);
                    var rel = f.Substring(dir.Length).TrimStart('\\');
                    var state = rel.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? "  [已禁用]" : "";
                    sb.AppendLine($"{rel}  ({Util.FormatSize(fi.Length)}, {fi.LastWriteTime:yyyy-MM-dd HH:mm}){state}");
                }
            }
            catch (Exception ex) { sb.AppendLine("读取 Mod 列表失败：" + ex.Message); }
            return sb.ToString();
        }

        static void AddText(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = e.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(true)))
                w.Write(text ?? "");
        }

        static void AddFile(ZipArchive zip, string file, string name, long maxBytes)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
                AddText(zip, name, Redact(ReadTail(file, maxBytes)));
            }
            catch (Exception ex) { Log.Warn($"打包 {file} 失败：{ex.Message}"); }
        }

        static readonly Regex[] Secrets =
        {
            R(@"(--accessToken\s+)\S+"),
            R(@"(--session\s+)\S+"),
            R(@"(Session ID is )[^\r\n)]+"),
            R(@"(""?access_?[Tt]oken""?\s*[:=]\s*""?)[^""\s,}]+"),
            R(@"(""?refresh_?[Tt]oken""?\s*[:=]\s*""?)[^""\s,}]+"),
            R(@"()eyJ[\w-]{8,}\.[\w-]{8,}\.[\w-]{8,}"),
        };

        /// <summary>隐藏登录令牌，报告可以放心发给别人</summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            foreach (var re in Secrets)
            {
                try { text = re.Replace(text, "$1******"); }
                catch (RegexMatchTimeoutException) { }
            }
            return text;
        }

        // =====================================================================
        // 工具
        // =====================================================================

        static string ErrorLines(Ctx c, int max)
        {
            var src = string.Join("\n", c.Output, c.Latest, c.Crash);
            var list = new List<string>();
            foreach (var raw in src.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.IndexOf("Exception", StringComparison.Ordinal) < 0 && l.IndexOf("Error", StringComparison.Ordinal) < 0 && l.IndexOf("Caused by", StringComparison.Ordinal) < 0) continue;
                l = l.Trim();
                if (l.Length > 600) l = l.Substring(0, 600) + "…";
                if (list.Count > 0 && list[list.Count - 1] == l) continue;
                list.Add(l);
            }
            // 去掉完全重复的行（游戏输出和 latest.log 往往一样），保留最后出现的位置
            var seen = new HashSet<string>();
            var dedup = new List<string>();
            for (int i = list.Count - 1; i >= 0; i--)
                if (seen.Add(list[i])) dedup.Insert(0, list[i]);
            return string.Join("\n", dedup.Skip(Math.Max(0, dedup.Count - max)));
        }

        static string FileNameOf(string p)
        {
            p = (p ?? "").Trim();
            try { return Path.GetFileName(p); } catch { return p; }
        }

        static string StripColors(string s) => s.IndexOf('§') < 0 ? s : Regex.Replace(s, "§.", "");

        static string SafeMc(Ctx c)
        {
            try { return c.Inst?.McVersion; } catch { return null; }
        }

        static string SafeMainClass(Ctx c)
        {
            try { return c.Inst != null && c.Inst.IsValid ? c.Inst.Info.MainClass : null; } catch { return null; }
        }

        static List<LoaderInfo> SafeLoaders(Ctx c)
        {
            try { return c.Inst?.Loaders ?? new List<LoaderInfo>(); } catch { return new List<LoaderInfo>(); }
        }

        static int RecommendedJava(Ctx c)
        {
            try
            {
                if (c.Inst == null || !c.Inst.IsValid) return 0;
                var mc = c.Inst.McVersion;
                if (c.Inst.Loaders.Any(l => l.Type == LoaderType.Forge) && Util.CompareVersion(mc, "1.17") < 0) return 8;
                return c.Inst.Info.JavaMajor;
            }
            catch { return 0; }
        }

        static bool IsAfter(string file, DateTime since)
        {
            try { return File.Exists(file) && File.GetLastWriteTime(file) >= since; } catch { return false; }
        }

        static string Newest(string dir, string pattern, DateTime since)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                return new DirectoryInfo(dir).GetFiles(pattern)
                    .Where(f => f.LastWriteTime >= since)
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>读文件末尾最多 maxBytes 字节；先按 UTF-8 解码，失败再按系统编码（GBK）</summary>
        static string ReadTail(string path, long maxBytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                int offset = 0;
                if (start > 0)
                {
                    // 从下一个换行开始，避免把多字节字符切成两半
                    while (offset < read && buf[offset] != (byte)'\n') offset++;
                    if (offset < read) offset++;
                }
                try { return new UTF8Encoding(false, true).GetString(buf, offset, read - offset); }
                catch (DecoderFallbackException) { return Encoding.Default.GetString(buf, offset, read - offset); }
            }
        }
    }
}
