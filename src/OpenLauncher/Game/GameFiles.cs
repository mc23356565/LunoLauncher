using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    /// <summary>
    /// 一个版本运行需要哪些文件：本体 jar、库、原生库、资源索引与资源、日志配置。
    /// 安装和"启动前补全文件"共用这套逻辑。
    /// </summary>
    public static class GameFiles
    {
        public static DownloadItem ClientJar(GameRepo repo, VersionInfo v)
        {
            var c = v.Json["downloads"]["client"];
            if (c.IsNull) return null;
            return new DownloadItem(c["url"].Str, repo.VersionJar(v.Id), c["sha1"].Str, c["size"].Long);
        }

        public static List<DownloadItem> Libraries(GameRepo repo, VersionInfo v, string arch = null)
        {
            var list = new List<DownloadItem>();
            foreach (var j in v.Json["libraries"])
            {
                var lib = new Library(j);
                if (lib.Maven == null || !lib.Allowed) continue;
                if (IsServerOnly(j)) continue;
                var art = lib.Artifact(repo.Libraries);
                if (art != null && !string.IsNullOrEmpty(art.Url)) list.Add(art);
                if (lib.IsLegacyNative)
                {
                    var nat = lib.NativeArtifact(repo.Libraries, arch);
                    if (nat != null && !string.IsNullOrEmpty(nat.Url)) list.Add(nat);
                }
            }
            return list;
        }

        /// <summary>老 Forge 的 versionInfo 里标了 clientreq:false 的库只给服务端用</summary>
        public static bool IsServerOnly(JNode lib) => !lib["clientreq"].IsNull && !lib["clientreq"].Bool;

        /// <summary>没有下载地址、需要安装器生成的库（Forge 的 client / srg jar），缺了就只能重装</summary>
        public static List<string> MissingGeneratedLibraries(GameRepo repo, VersionInfo v)
        {
            var missing = new List<string>();
            foreach (var j in v.Json["libraries"])
            {
                var lib = new Library(j);
                if (lib.Maven == null || !lib.Allowed) continue;
                var art = lib.Artifact(repo.Libraries);
                if (art != null && string.IsNullOrEmpty(art.Url) && !File.Exists(Util.LP(art.Path))) missing.Add(lib.Name);
            }
            return missing;
        }

        public static DownloadItem LoggingConfig(GameRepo repo, VersionInfo v)
        {
            var f = v.Json["logging"]["client"]["file"];
            if (f.IsNull || f["id"].Str == null) return null;
            return new DownloadItem(f["url"].Str, Path.Combine(repo.Assets, "log_configs", f["id"].Str), f["sha1"].Str, f["size"].Long);
        }

        public static string AssetIndexPath(GameRepo repo, VersionInfo v) => Path.Combine(repo.Assets, "indexes", v.AssetsId + ".json");

        public static async Task<JNode> EnsureAssetIndexAsync(GameRepo repo, VersionInfo v, LauncherTask task)
        {
            var ai = v.Json["assetIndex"];
            var path = AssetIndexPath(repo, v);
            if (ai["url"].Str != null)
            {
                var item = new DownloadItem(ai["url"].Str, path, ai["sha1"].Str, ai["size"].Long);
                if (!item.IsValid()) await Downloader.DownloadFileAsync(item, task, task?.Progress ?? 0, task?.Progress ?? 0).ConfigureAwait(false);
            }
            if (!File.Exists(path)) return null;
            return JNode.ParseFile(path);
        }

        public static List<DownloadItem> Assets(GameRepo repo, JNode index)
        {
            var list = new List<DownloadItem>();
            if (index == null) return list;
            foreach (var kv in index["objects"].Props)
            {
                var hash = kv.Value["hash"].Str;
                if (hash == null || hash.Length < 2) continue;
                var sub = hash.Substring(0, 2);
                list.Add(new DownloadItem($"https://resources.download.minecraft.net/{sub}/{hash}",
                    Path.Combine(repo.Assets, "objects", sub, hash), hash, kv.Value["size"].Long));
            }
            return list;
        }

        /// <summary>下载/补全某个版本运行所需的全部文件</summary>
        public static async Task CompleteAsync(GameRepo repo, VersionInfo v, LauncherTask task, double from, double to, bool includeAssets = true, bool quickCheck = false, string arch = null)
        {
            task?.Report(from, "读取资源索引…");
            var items = new List<DownloadItem>();
            var jar = ClientJar(repo, v);
            if (jar != null) items.Add(jar);
            items.AddRange(Libraries(repo, v, arch));
            var log = LoggingConfig(repo, v);
            if (log != null) items.Add(log);
            if (includeAssets)
            {
                var index = await EnsureAssetIndexAsync(repo, v, task).ConfigureAwait(false);
                items.AddRange(Assets(repo, index));
            }
            await Downloader.RunAsync(items, task, from, to, quickCheck ? "检查游戏文件" : "下载游戏文件", quickCheck).ConfigureAwait(false);

            // 外部启动器装的继承版本：自身文件夹没有 jar 时，从被继承的版本拷一份
            if (jar == null && !File.Exists(repo.VersionJar(v.Id)))
            {
                foreach (var id in v.Chain.Skip(1))
                {
                    var p = repo.VersionJar(id);
                    if (File.Exists(p)) { File.Copy(p, repo.VersionJar(v.Id), true); break; }
                }
            }
        }

        /// <summary>
        /// 解压原生库到 natives 目录。
        /// 旧格式按 natives 字段取 classifier；1.19+ 的 natives-windows* 库也顺带解压（只取当前架构）。
        /// </summary>
        /// <param name="arch">Java 的架构（ARM 电脑上可能用 x64 Java 跑老版本），为空时用系统架构</param>
        public static void ExtractNatives(GameRepo repo, VersionInfo v, string nativesDir, string arch = null)
        {
            Directory.CreateDirectory(nativesDir);
            arch = arch ?? Util.OsArch;
            string wanted = arch == "arm64" ? "natives-windows-arm64" : arch == "x86" ? "natives-windows-x86" : "natives-windows";
            foreach (var j in v.Json["libraries"])
            {
                var lib = new Library(j);
                if (lib.Maven == null || !lib.Allowed) continue;
                string jarPath = null;
                if (lib.IsLegacyNative) jarPath = lib.NativeArtifact(repo.Libraries, arch)?.Path;
                else if (lib.IsNativeClassifier && string.Equals(lib.Maven.Classifier, wanted, StringComparison.OrdinalIgnoreCase))
                    jarPath = lib.Artifact(repo.Libraries)?.Path;
                if (jarPath == null || !File.Exists(Util.LP(jarPath))) continue;

                var excludes = j["extract"]["exclude"].StrList();
                excludes.Add("META-INF/");
                try
                {
                    using (var zip = ZipFile.OpenRead(Util.LP(jarPath)))
                    {
                        foreach (var e in zip.Entries)
                        {
                            var name = e.FullName.Replace('\\', '/');
                            if (name.EndsWith("/") || excludes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                            // 只要动态库本体，jar 里的 .class / .sha1 / .git 之类跳过
                            var ext = Path.GetExtension(name).ToLowerInvariant();
                            if (ext != ".dll" && ext != ".so" && ext != ".dylib" && ext != ".jnilib") continue;
                            var target = Path.Combine(nativesDir, Path.GetFileName(name));
                            if (File.Exists(target) && new FileInfo(target).Length == e.Length) continue;
                            try { e.ExtractToFile(target, true); }
                            catch (IOException) { /* 文件被占用（游戏多开），跳过 */ }
                        }
                    }
                }
                catch (Exception ex) { Log.Warn($"解压原生库失败 {jarPath}：{ex.Message}"); }
            }
        }

        /// <summary>1.6 以前的版本需要把资源拷到 virtual/legacy 或游戏目录 resources 下</summary>
        public static string PrepareLegacyAssets(GameRepo repo, VersionInfo v, string gameDir)
        {
            var path = AssetIndexPath(repo, v);
            if (!File.Exists(path)) return repo.Assets;
            JNode index;
            try { index = JNode.ParseFile(path); } catch { return repo.Assets; }
            bool virt = index["virtual"].Bool;
            bool toRes = index["map_to_resources"].Bool;
            if (!virt && !toRes) return repo.Assets;

            var dest = toRes ? Path.Combine(gameDir, "resources") : Path.Combine(repo.Assets, "virtual", v.AssetsId);
            foreach (var kv in index["objects"].Props)
            {
                var hash = kv.Value["hash"].Str;
                if (hash == null) continue;
                var src = Path.Combine(repo.Assets, "objects", hash.Substring(0, 2), hash);
                var dst = Path.Combine(dest, kv.Key.Replace('/', '\\'));
                if (!File.Exists(src) || File.Exists(Util.LP(dst))) continue;
                try { Util.EnsureParent(dst); File.Copy(src, Util.LP(dst)); } catch { }
            }
            return dest;
        }
    }
}
