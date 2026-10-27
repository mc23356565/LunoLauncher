using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace LunoLauncher.Core
{
    /// <summary>
    /// 内置 EasyTier：启动器 exe 里打包了一份压缩的官方二进制，第一次用到时解压到
    /// 「数据目录\tunnel\bin」，之后直接复用。这样把 exe 发给朋友就能联机，不用另装 EasyTier。
    /// </summary>
    public static class TunnelPayload
    {
        /// <summary>内置的 EasyTier 版本。换包时一起改。</summary>
        public const string Version = "2.6.4";

        const string ResourceName = "Luno.Payload.EasyTier.zip";
        const string MarkerName = ".version";

        /// <summary>解压后必须存在的文件，缺一个就算失败。</summary>
        static readonly string[] Files = { "easytier-core.exe", "easytier-cli.exe", "wintun.dll" };

        static readonly object Gate = new object();
        static bool _done;
        static string _dir;
        static string _error;

        /// <summary>解压目标目录。</summary>
        public static string Dir => Path.Combine(Tunnel.TunnelDir, "bin");

        /// <summary>上一次解压失败的原因；成功时为 null。</summary>
        public static string Error => _error;

        /// <summary>这个 exe 里到底有没有打包二进制（诊断用）。</summary>
        public static bool Packed
        {
            get
            {
                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                        return s != null;
                }
                catch { return false; }
            }
        }

        /// <summary>确保内置副本已解压，返回目录；失败返回 null。可重复调用（结果有缓存）。</summary>
        public static string Ensure()
        {
            lock (Gate)
            {
                if (_done) return _dir;
                _done = true;
                try { _dir = Extract(); }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    Log.Warn("[隧道] 解压内置 EasyTier 失败：" + ex.Message);
                }
                return _dir;
            }
        }

        /// <summary>扔掉已解压的副本，下次重新解压（文件被改坏时用）。</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _done = false;
                _dir = null;
                _error = null;
                try { Util.TryDeleteDir(Dir); } catch { }
            }
        }

        static string Extract()
        {
            var dir = Dir;
            // 文件齐 + 版本标记匹配 → 直接用，不再重复解压
            if (IsCurrent(dir)) return dir;

            Stream stream = null;
            try
            {
                stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    _error = "这个 exe 是没打包联机组件的构建";
                    return null;
                }

                Directory.CreateDirectory(dir);
                // 先删掉版本标记：解压到一半时下次会重来，不会把半截文件当成好的
                try { File.Delete(Path.Combine(dir, MarkerName)); } catch { }

                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    stream = null; // 交给 ZipArchive 释放
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项
                        var target = Path.Combine(dir, entry.FullName.Replace('/', '\\'));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        try { entry.ExtractToFile(target, true); }
                        catch (IOException)
                        {
                            // 文件正被占用（比如 easytier-core 在跑）：已有的那份先用着
                            if (!File.Exists(target)) throw;
                        }
                    }
                }

                // 注意：这里只能看"文件齐不齐"，不能看版本标记 —— 标记还没写呢
                var missing = MissingFiles(dir);
                if (missing.Count > 0)
                {
                    _error = "解压后缺少 " + string.Join("、", missing.ToArray());
                    return null;
                }

                File.WriteAllText(Path.Combine(dir, MarkerName), Version);
                Log.Info("[隧道] 内置 EasyTier " + Version + " 已解压到 " + dir);
                return dir;
            }
            finally
            {
                if (stream != null) stream.Dispose();
            }
        }

        /// <summary>缺哪些文件（空表示齐了）。</summary>
        static List<string> MissingFiles(string dir)
        {
            var missing = new List<string>();
            try
            {
                if (!Directory.Exists(dir)) { missing.Add("目录"); return missing; }
            }
            catch { missing.Add("目录"); return missing; }

            foreach (var f in Files)
            {
                try
                {
                    var p = Path.Combine(dir, f);
                    if (!File.Exists(p) || new FileInfo(p).Length == 0) missing.Add(f);
                }
                catch { missing.Add(f); }
            }
            return missing;
        }

        /// <summary>文件齐、而且版本标记等于当前内置版本。</summary>
        static bool IsCurrent(string dir)
        {
            if (MissingFiles(dir).Count > 0) return false;
            try
            {
                var marker = Path.Combine(dir, MarkerName);
                return File.Exists(marker) && File.ReadAllText(marker).Trim() == Version;
            }
            catch { return false; }
        }
    }
}
