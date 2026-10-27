using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenLauncher.Core
{
    public static class Util
    {
        // ---------- 哈希 ----------
        public static string Sha1File(string path) => HashFile(path, SHA1.Create());
        public static string Sha256File(string path) => HashFile(path, SHA256.Create());
        public static string Sha512File(string path) => HashFile(path, SHA512.Create());

        static string HashFile(string path, HashAlgorithm algo)
        {
            using (algo)
            using (var fs = new FileStream(LP(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
                return Hex(algo.ComputeHash(fs));
        }

        public static string Sha256(byte[] data) { using (var a = SHA256.Create()) return Hex(a.ComputeHash(data)); }
        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>离线 UUID：与官方服务端 offline 模式一致的 UUID v3("OfflinePlayer:" + 名字)</summary>
        public static string OfflineUuid(string name)
        {
            byte[] hash;
            using (var md5 = MD5.Create()) hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
            return Hex(hash);
        }

        // ---------- 文件 ----------
        /// <summary>
        /// 长路径：超过 240 字符时加 \\?\ 前缀，绕过 Windows 260 字符限制（系统没开长路径支持也能用）。
        /// </summary>
        public static string LP(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 240 || path.StartsWith(@"\\?\")) return path;
            var full = Path.GetFullPath(path);
            return full.StartsWith(@"\\") ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }

        public static void EnsureParent(string path)
        {
            var d = Path.GetDirectoryName(LP(path));
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(LP(path))) File.Delete(LP(path)); } catch { }
        }

        public static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        public static void CopyDir(string src, string dst, bool overwrite = true)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)), overwrite);
        }

        public static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Warn("打开文件夹失败: " + ex.Message); }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("打开网址失败: " + ex.Message); }
        }

        /// <summary>解压 zip 中以 prefix 开头的条目到 dest（去掉 prefix）。防 zip-slip。</summary>
        public static int ExtractPrefix(ZipArchive zip, string prefix, string dest)
        {
            int n = 0;
            var destFull = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = name.Substring(prefix.Length).TrimStart('/');
                if (rel.Length == 0) continue;
                var target = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', '\\')));
                if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                EnsureParent(target);
                e.ExtractToFile(LP(target), true);
                n++;
            }
            return n;
        }

        public static string ReadZipText(ZipArchive zip, string entry)
        {
            var e = zip.GetEntry(entry) ?? zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, entry, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd();
        }

        public static byte[] ReadZipBytes(ZipArchive zip, string entry)
        {
            var e = zip.GetEntry(entry);
            if (e == null) return null;
            using (var s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>文件名里不能出现的字符替换掉</summary>
        public static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }

        public static string FormatSize(double bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (bytes >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
            return i == 0 ? $"{bytes:0} {u[i]}" : $"{bytes:0.##} {u[i]}";
        }

        public static string FormatCount(long n)
        {
            if (n >= 100_000_000) return (n / 100_000_000.0).ToString("0.#") + " 亿";
            if (n >= 10_000) return (n / 10_000.0).ToString("0.#") + " 万";
            return n.ToString();
        }

        /// <summary>把命令行参数按 Windows 规则加引号</summary>
        public static string QuoteArg(string a)
        {
            if (string.IsNullOrEmpty(a)) return "\"\"";
            if (a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (var c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>简单地按空格切分参数（支持双引号）</summary>
        public static List<string> SplitArgs(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            var cur = new StringBuilder();
            bool inQ = false, has = false;
            foreach (var c in s)
            {
                if (c == '"') { inQ = !inQ; has = true; continue; }
                if (!inQ && char.IsWhiteSpace(c))
                {
                    if (has || cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); has = false; }
                    continue;
                }
                cur.Append(c);
            }
            if (has || cur.Length > 0) list.Add(cur.ToString());
            return list;
        }

        // ---------- 系统信息 ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public static (long totalMb, long availMb) Memory()
        {
            var m = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(m)) return (8192, 4096);
            return ((long)(m.ullTotalPhys / 1048576), (long)(m.ullAvailPhys / 1048576));
        }

        [DllImport("kernel32.dll")] static extern void GetNativeSystemInfo(out SYSTEM_INFO info);
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture, wReserved;
            public uint dwPageSize; public IntPtr lpMin, lpMax, mask;
            public uint num, type, gran; public ushort level, rev;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        /// <summary>系统真实架构：x64 / x86 / arm64</summary>
        public static string OsArch
        {
            get
            {
                try
                {
                    if (IsWow64Process2(Process.GetCurrentProcess().Handle, out _, out var native))
                    {
                        if (native == 0xAA64) return "arm64";
                        if (native == 0x8664) return "x64";
                        if (native == 0x14c) return "x86";
                    }
                }
                catch { }
                GetNativeSystemInfo(out var si);
                return si.wProcessorArchitecture switch { 9 => "x64", 12 => "arm64", _ => "x86" };
            }
        }

        public static Version OsVersion
        {
            get
            {
                // Environment.OSVersion 在没有 manifest 声明时会撒谎，读注册表更准
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        var major = k?.GetValue("CurrentMajorVersionNumber");
                        var build = k?.GetValue("CurrentBuildNumber") as string;
                        if (major != null && build != null)
                            return new Version(Convert.ToInt32(major), Convert.ToInt32(k.GetValue("CurrentMinorVersionNumber") ?? 0), int.Parse(build));
                    }
                }
                catch { }
                return Environment.OSVersion.Version;
            }
        }

        /// <summary>比较 "1.20.4" / "26.3" / "0.16.10" 这类版本号，非数字段按字符串比较</summary>
        public static int CompareVersion(string a, string b)
        {
            var pa = (a ?? "").Split('.', '-', '+', '_');
            var pb = (b ?? "").Split('.', '-', '+', '_');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                var x = i < pa.Length ? pa[i] : "0";
                var y = i < pb.Length ? pb[i] : "0";
                bool nx = long.TryParse(x, out var ix), ny = long.TryParse(y, out var iy);
                int c = nx && ny ? ix.CompareTo(iy) : nx ? 1 : ny ? -1 : string.CompareOrdinal(x, y);
                if (c != 0) return c;
            }
            return 0;
        }
    }
}
