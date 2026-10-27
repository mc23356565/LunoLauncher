using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.Content
{
    /// <summary>
    /// 把实例导出为 Modrinth 整合包（.mrpack）：能在 Modrinth 上按哈希查到的 Mod / 资源包 / 光影写进 files[]（只存下载地址），
    /// 其余文件（配置、查不到的 Mod 等）放进 overrides/。
    /// </summary>
    public static class ModpackExporter
    {
        /// <summary>游戏目录顶层永远不导出的东西（个人数据、缓存、日志、启动器文件）</summary>
        static readonly HashSet<string> SkipTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "logs", "crash-reports", "screenshots", "versions", "libraries", "assets", "natives", "downloads", "debug", "backups",
            ".fabric", ".quilt", ".mixin.out", ".cache", ".git", "cache", "local", "temp",
            "journeymap", "XaeroWaypoints", "XaeroWorldMap", "replay_recordings", "replay_videos", "essential", "moddata",
            "usercache.json", "usernamecache.json", "launcher_profiles.json", "launcher_accounts.json", "launcher_log.txt",
            "command_history.txt", "realms_persistence.json", "patchouli_data.json", Branding.AppName,
        };

        /// <summary>Modrinth 上只需写下载地址的文件夹</summary>
        static readonly string[] HashableDirs = { "mods", "resourcepacks", "shaderpacks" };

        public static async Task ExportMrpackAsync(Instance inst, string outFile, string packName, string packVersion, bool includeSaves, LauncherTask task)
        {
            if (inst == null) throw new ArgumentNullException(nameof(inst));
            if (string.IsNullOrEmpty(outFile)) throw new ArgumentNullException(nameof(outFile));
            var ct = task?.Token ?? CancellationToken.None;
            var gameDir = Path.GetFullPath(inst.GameDir).TrimEnd('\\');
            if (!Directory.Exists(gameDir)) throw new DirectoryNotFoundException("游戏目录不存在：" + gameDir);

            // 1. 收集文件
            task?.Report(0, "收集文件…");
            var all = await Task.Run(() => Collect(inst, gameDir, includeSaves, ct), ct).ConfigureAwait(false);

            // 2. 计算 Mod / 资源包 / 光影的哈希并在 Modrinth 上查找
            var candidates = all.Where(rel =>
            {
                var parts = rel.Split('/');
                return parts.Length == 2 && HashableDirs.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
                    && (rel.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            }).ToList();

            var hashes = new Dictionary<string, (string sha1, string sha512, long size)>();
            int done = 0;
            await Task.Run(() => Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, rel =>
            {
                var full = Path.Combine(gameDir, rel.Replace('/', '\\'));
                try
                {
                    var h = (Util.Sha1File(full), Util.Sha512File(full), new FileInfo(full).Length);
                    lock (hashes) hashes[rel] = h;
                }
                catch (Exception ex) { Log.Warn($"计算哈希失败 {rel}：{ex.Message}"); }
                var n = Interlocked.Increment(ref done);
                if (n % 10 == 0) task?.Report(0.3 * n / Math.Max(1, candidates.Count), $"计算哈希：{n}/{candidates.Count}");
            }), ct).ConfigureAwait(false);

            task?.Report(0.3, "在 Modrinth 上查找文件…");
            Dictionary<string, JNode> found;
            try { found = await ModrinthProvider.LookupHashesAsync(hashes.Values.Select(h => h.sha1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Modrinth 哈希查询失败，所有文件都放进 overrides：" + ex.Message);
                found = new Dictionary<string, JNode>();
            }

            var files = JNode.Arr();
            var remote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in hashes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!found.TryGetValue(kv.Value.sha1, out var version)) continue;
                string url = null;
                foreach (var f in version["files"])
                    if (string.Equals(f["hashes"]["sha1"].Str, kv.Value.sha1, StringComparison.OrdinalIgnoreCase)) { url = f["url"].Str; break; }
                if (string.IsNullOrEmpty(url)) continue;
                bool isMod = kv.Key.StartsWith("mods/", StringComparison.OrdinalIgnoreCase);
                var env = JNode.Obj().Set("client", "required").Set("server", isMod ? ServerSide(version) : "unsupported");
                files.Add(JNode.Obj()
                    .Set("path", kv.Key)
                    .Set("hashes", JNode.Obj().Set("sha1", kv.Value.sha1).Set("sha512", kv.Value.sha512))
                    .Set("env", env)
                    .Set("downloads", JNode.Arr().Add(url))
                    .Set("fileSize", kv.Value.size));
                remote.Add(kv.Key);
            }

            // 3. 清单
            var deps = JNode.Obj().Set("minecraft", inst.McVersion);
            foreach (var l in inst.Loaders)
            {
                var key = l.Type switch
                {
                    LoaderType.Forge => "forge",
                    LoaderType.NeoForge => "neoforge",
                    LoaderType.Fabric => "fabric-loader",
                    LoaderType.Quilt => "quilt-loader",
                    _ => null
                };
                if (key != null && !string.IsNullOrEmpty(l.Version)) deps.Set(key, l.Version);
            }
            var index = JNode.Obj()
                .Set("formatVersion", 1)
                .Set("game", "minecraft")
                .Set("versionId", string.IsNullOrWhiteSpace(packVersion) ? "1.0.0" : packVersion.Trim())
                .Set("name", string.IsNullOrWhiteSpace(packName) ? inst.Id : packName.Trim())
                .Set("summary", $"由 {Branding.DisplayName} 导出")
                .Set("files", files)
                .Set("dependencies", deps);

            // 4. 打包
            var overrides = all.Where(r => !remote.Contains(r)).ToList();
            var tmp = outFile + ".oltmp";
            Util.EnsureParent(outFile);
            try
            {
                await Task.Run(() =>
                {
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                        var e = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
                        using (var w = new StreamWriter(e.Open(), new System.Text.UTF8Encoding(false))) w.Write(index.ToJson(true));
                        for (int i = 0; i < overrides.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var rel = overrides[i];
                            var full = Path.Combine(gameDir, rel.Replace('/', '\\'));
                            try
                            {
                                var entry = zip.CreateEntry("overrides/" + rel, NoCompress(rel) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                                entry.LastWriteTime = File.GetLastWriteTime(full);
                                using (var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                using (var dst = entry.Open())
                                    src.CopyTo(dst);
                            }
                            catch (IOException ex) { Log.Warn($"跳过无法读取的文件 {rel}：{ex.Message}"); }
                            if (i % 20 == 0) task?.Report(0.4 + 0.6 * i / Math.Max(1, overrides.Count), $"打包：{i}/{overrides.Count}");
                        }
                    }
                }, ct).ConfigureAwait(false);
                if (File.Exists(outFile)) File.Delete(outFile);
                File.Move(tmp, outFile);
            }
            catch
            {
                Util.TryDelete(tmp);
                throw;
            }
            task?.Report(1, $"导出完成：{remote.Count} 个文件走 Modrinth 下载，{overrides.Count} 个文件打包进 overrides");
            Log.Info($"导出整合包 {outFile}：files {remote.Count}，overrides {overrides.Count}");
        }

        static string ServerSide(JNode version)
        {
            // version 里没有环境信息时按"服务端也需要"处理，最稳妥
            var env = version["environment"].Str;
            if (env == "client_only") return "unsupported";
            return "required";
        }

        static bool NoCompress(string rel)
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            return ext == ".jar" || ext == ".zip" || ext == ".png" || ext == ".ogg" || ext == ".gz" || ext == ".mca";
        }

        /// <summary>收集要导出的文件（相对游戏目录、用 / 分隔）</summary>
        static List<string> Collect(Instance inst, string gameDir, bool includeSaves, CancellationToken ct)
        {
            var skip = new HashSet<string>(SkipTop, StringComparer.OrdinalIgnoreCase)
            {
                inst.Id + ".json", inst.Id + ".jar", inst.Id + "-natives",
            };
            if (!includeSaves) skip.Add("saves");
            var list = new List<string>();

            void Walk(string dir, string relPrefix)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var f in Directory.GetFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    if (relPrefix.Length == 0 && skip.Contains(name)) continue;
                    if (name == ContentIndex.FileName || name.EndsWith(".oltmp", StringComparison.OrdinalIgnoreCase)) continue;
                    var lower = name.ToLowerInvariant();
                    if (lower.EndsWith(".log") || lower.StartsWith("hs_err_") || lower.EndsWith(".disabled") && relPrefix.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (relPrefix.Length == 0 && lower.StartsWith("launcher_")) continue;
                    if (includeSaves && relPrefix.StartsWith("saves/", StringComparison.OrdinalIgnoreCase) && lower == "session.lock") continue;
                    list.Add(relPrefix + name);
                }
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (relPrefix.Length == 0 && skip.Contains(name)) continue;
                    // 符号链接 / 联接点不跟进，避免把别处的文件打包进去
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    Walk(d, relPrefix + name + "/");
                }
            }
            Walk(gameDir, "");
            return list;
        }
    }
}
