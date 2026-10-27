using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.Content
{
    /// <summary>InstallAsync 的详细结果</summary>
    public class InstallReport
    {
        /// <summary>本次下载（或校验通过）的文件完整路径，第一个是用户选的文件</summary>
        public List<string> Installed { get; } = new List<string>();
        /// <summary>已经装过而跳过的前置（显示名）</summary>
        public List<string> Skipped { get; } = new List<string>();
        /// <summary>找不到适配当前版本的前置（显示名）</summary>
        public List<string> Missing { get; } = new List<string>();
        /// <summary>被新版本替换掉（移到回收站）的旧文件</summary>
        public List<string> Replaced { get; } = new List<string>();
    }

    public static class ContentService
    {
        static readonly ModrinthProvider _modrinth = new ModrinthProvider();
        static readonly CurseForgeProvider _curseForge = new CurseForgeProvider();

        public static IContentProvider Modrinth => _modrinth;
        public static IContentProvider CurseForge => _curseForge;
        public static IContentProvider Get(ContentSource s) => s == ContentSource.CurseForge ? (IContentProvider)_curseForge : _modrinth;

        /// <summary>设置里选的默认来源</summary>
        public static ContentSource PreferredSource
            => string.Equals(Settings.PreferredContentSource, "curseforge", StringComparison.OrdinalIgnoreCase) ? ContentSource.CurseForge : ContentSource.Modrinth;

        public static string SourceName(ContentSource s) => s == ContentSource.CurseForge ? "CurseForge" : "Modrinth";

        public static string TypeName(ContentType t) => t switch
        {
            ContentType.Modpack => "整合包",
            ContentType.ResourcePack => "资源包",
            ContentType.Shader => "光影",
            ContentType.DataPack => "数据包",
            _ => "Mod"
        };

        public static string LoaderDisplayName(string slug) => (slug ?? "").ToLowerInvariant() switch
        {
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "quilt" => "Quilt",
            "liteloader" => "LiteLoader",
            "iris" => "Iris",
            "optifine" => "OptiFine",
            "canvas" => "Canvas",
            "vanilla" => "原版",
            "minecraft" => "原版",
            "datapack" => "数据包",
            _ => slug
        };

        /// <summary>实例的加载器 → Modrinth / CurseForge 用的小写名（原版 / 仅 OptiFine 返回 null）</summary>
        public static string LoaderSlug(Instance inst)
        {
            if (inst == null || !inst.IsValid) return null;
            return LoaderInfo.Slug(inst.PrimaryLoader);
        }

        public static int CompareGameVersion(string a, string b) => Util.CompareVersion(a, b);

        /// <summary>"1.20.1, 1.20, 1.19.4 等 12 个版本"</summary>
        public static string CompactVersions(List<string> versions)
        {
            if (versions == null || versions.Count == 0) return "";
            if (versions.Count <= 3) return string.Join(", ", versions);
            return string.Join(", ", versions.Take(3)) + $" 等 {versions.Count} 个版本";
        }

        // ---------------- 搜索 / 查询 ----------------

        /// <summary>搜索。中文关键词先经 ZhNames 翻译成英文。</summary>
        public static async Task<SearchResult> SearchAsync(ContentSource s, SearchQuery q, CancellationToken ct)
        {
            var real = q.Clone();
            real.Text = ZhNames.Translate(q.Text);
            if (real.Text != q.Text) Log.Info($"搜索词翻译：{q.Text} → {real.Text}");
            var r = await Get(s).SearchAsync(real, ct).ConfigureAwait(false);
            r.EffectiveText = real.Text;
            return r;
        }

        /// <summary>列出项目文件；资源包 / 光影 / 数据包不按 Mod 加载器筛选</summary>
        public static Task<List<ContentFile>> GetFilesAsync(ContentProject p, string gameVersion, string loader, CancellationToken ct)
        {
            bool byLoader = p.Type == ContentType.Mod || p.Type == ContentType.Modpack;
            return Get(p.Source).GetFilesAsync(p.Id, gameVersion, byLoader ? loader : null, ct);
        }

        /// <summary>从新到旧的列表里挑一个：最新的正式版，没有就最新的任意版本</summary>
        public static ContentFile PickBest(IEnumerable<ContentFile> files)
        {
            var list = files?.Where(f => f != null).OrderByDescending(f => f.Published).ToList() ?? new List<ContentFile>();
            return list.FirstOrDefault(f => f.IsRelease) ?? list.FirstOrDefault();
        }

        /// <summary>
        /// 某类资源在实例里的目标文件夹。数据包返回 GameDir\datapacks，由界面再让用户挑存档（saves\&lt;世界&gt;\datapacks）。
        /// 整合包不装进实例，返回 null。
        /// </summary>
        public static string TargetDir(Instance inst, ContentType type) => type switch
        {
            ContentType.Mod => inst.ModsDir,
            ContentType.ResourcePack => inst.ResourcePacksDir,
            ContentType.Shader => inst.ShaderPacksDir,
            ContentType.DataPack => Path.Combine(inst.GameDir, "datapacks"),
            _ => null
        };

        /// <summary>存档的数据包文件夹</summary>
        public static string WorldDataPacksDir(Instance inst, string worldFolderName)
            => PathGuard.Resolve(inst.SavesDir, worldFolderName) is string w ? Path.Combine(w, "datapacks") : null;

        // ---------------- 安装 ----------------

        public static async Task<List<string>> InstallAsync(ContentFile file, string targetDir, bool withDependencies, string gameVersion, string loader, LauncherTask task)
            => (await InstallWithReportAsync(file, targetDir, withDependencies, gameVersion, loader, task).ConfigureAwait(false)).Installed;

        /// <summary>
        /// 下载文件到 targetDir。withDependencies 时递归解析必需前置（同游戏版本 + 加载器），已装的跳过。
        /// 同一项目的旧文件会在新文件下载成功后移到回收站。
        /// </summary>
        public static async Task<InstallReport> InstallWithReportAsync(ContentFile file, string targetDir, bool withDependencies, string gameVersion, string loader, LauncherTask task)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (string.IsNullOrEmpty(targetDir)) throw new ArgumentNullException(nameof(targetDir));
            var ct = task?.Token ?? CancellationToken.None;
            Directory.CreateDirectory(targetDir);
            var report = new InstallReport();
            var index = ContentIndex.Load(targetDir);

            // 先认一认文件夹里已有的文件属于哪个项目（只查没记录过的），装前置、换版本都靠它
            if (file.Source == ContentSource.Modrinth)
            {
                task?.Report(-1, "正在检查已安装的文件…");
                await IdentifyLocalAsync(targetDir, index, ct).ConfigureAwait(false);
            }

            var plan = new List<ContentFile> { file };
            var skipped = new List<(ContentSource src, string id, string name)>();
            var missing = new List<(ContentSource src, string id, string name)>();
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (withDependencies && file.Dependencies.Any(d => d.Required))
            {
                task?.Report(-1, "正在解析前置…");
                await ResolveDependenciesAsync(file, targetDir, index, gameVersion, loader, plan, skipped, missing, titles, ct).ConfigureAwait(false);
            }

            // 下载
            var items = new List<DownloadItem>();
            foreach (var f in plan)
            {
                if (string.IsNullOrEmpty(f.Url)) throw new ContentException($"{f.FileName} 没有下载地址");
                items.Add(new DownloadItem
                {
                    Url = f.Url,
                    AltUrls = f.AltUrls?.ToList(),
                    Path = PathGuard.FileIn(targetDir, f.FileName),
                    Sha1 = f.Sha1,
                    Sha512 = string.IsNullOrEmpty(f.Sha1) ? f.Sha512 : null,
                    Size = f.Size,
                });
            }
            var label = plan.Count == 1 ? "下载 " + file.FileName : $"下载 {file.FileName} 及 {plan.Count - 1} 个前置";
            await Downloader.RunAsync(items, task, 0, 1, label).ConfigureAwait(false);

            // 记索引、清理同项目的旧文件
            for (int i = 0; i < plan.Count; i++)
            {
                var f = plan[i];
                var path = items[i].Path;
                report.Installed.Add(path);
                if (!string.IsNullOrEmpty(f.ProjectId))
                {
                    foreach (var old in index.ExistingFilesOf(f.Source, f.ProjectId))
                    {
                        if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase) || string.Equals(old, path + ".disabled", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            RecycleBin.Delete(old);
                            index.Remove(Path.GetFileName(old));
                            report.Replaced.Add(old);
                            Log.Info($"已替换旧版本：{old} → {path}");
                        }
                        catch (Exception ex) { Log.Warn($"移除旧版本失败 {old}：{ex.Message}"); }
                    }
                }
                index.Set(Path.GetFileName(path), f);
            }
            index.Save();

            // 跳过 / 缺失的前置换成可读的名字（尽力而为）
            await FillTitlesAsync(skipped.Concat(missing), titles, ct).ConfigureAwait(false);
            string nameOf((ContentSource src, string id, string name) x)
                => x.id != null && titles.TryGetValue(ContentIndex.Key(x.src, x.id), out var t) ? t : x.name ?? x.id;
            report.Skipped.AddRange(skipped.Select(nameOf));
            report.Missing.AddRange(missing.Select(nameOf));

            Log.Info($"安装完成：{string.Join(", ", report.Installed.Select(Path.GetFileName))}" +
                     (report.Skipped.Count > 0 ? $"；已有前置：{string.Join(", ", report.Skipped)}" : "") +
                     (report.Missing.Count > 0 ? $"；缺少前置：{string.Join(", ", report.Missing)}" : ""));
            if (report.Missing.Count > 0) task?.Report(1, "已安装，但找不到适配的前置：" + string.Join("、", report.Missing));
            return report;
        }

        static async Task ResolveDependenciesAsync(ContentFile root, string targetDir, ContentIndex index, string gameVersion, string loader,
            List<ContentFile> plan, List<(ContentSource, string, string)> skipped, List<(ContentSource, string, string)> missing,
            Dictionary<string, string> titles, CancellationToken ct)
        {
            var presence = new Presence(targetDir, index, titles);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ContentIndex.Key(root.Source, root.ProjectId) };
            var queue = new Queue<ContentFile>();
            queue.Enqueue(root);

            while (queue.Count > 0 && plan.Count < 200)
            {
                var cur = queue.Dequeue();
                var src = cur.Source;
                var provider = Get(src);
                foreach (var dep in cur.Dependencies.Where(d => d.Required))
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(dep.ProjectId) && string.IsNullOrEmpty(dep.FileId))
                    {
                        // Modrinth 的"外部依赖"只有文件名，没法自动下载
                        if (!string.IsNullOrEmpty(dep.FileName) && !presence.HasFileName(dep.FileName)) missing.Add((src, null, dep.FileName));
                        continue;
                    }
                    var key = !string.IsNullOrEmpty(dep.ProjectId) ? ContentIndex.Key(src, dep.ProjectId) : "file:" + dep.FileId;
                    if (!visited.Add(key)) continue;

                    ContentFile depFile = null;
                    try
                    {
                        if (!string.IsNullOrEmpty(dep.ProjectId) && await presence.IsPresentAsync(src, dep.ProjectId, ct).ConfigureAwait(false))
                        {
                            skipped.Add((src, dep.ProjectId, null));
                            continue;
                        }
                        depFile = await ResolveDependencyAsync(provider, dep, gameVersion, loader, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Log.Warn($"解析前置 {dep.ProjectId ?? dep.FileId} 失败：{ex.Message}"); }

                    if (depFile == null) { missing.Add((src, dep.ProjectId, dep.ProjectId ?? dep.FileId)); continue; }

                    // 只给了 version_id 的依赖：拿到文件后再按项目判一次
                    if (string.IsNullOrEmpty(dep.ProjectId) && !string.IsNullOrEmpty(depFile.ProjectId))
                    {
                        if (!visited.Add(ContentIndex.Key(src, depFile.ProjectId))) continue;
                        try
                        {
                            if (await presence.IsPresentAsync(src, depFile.ProjectId, ct).ConfigureAwait(false))
                            {
                                skipped.Add((src, depFile.ProjectId, depFile.DisplayName));
                                continue;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { }
                    }
                    if (presence.HasFileName(depFile.FileName)) { skipped.Add((src, depFile.ProjectId, depFile.DisplayName)); continue; }

                    plan.Add(depFile);
                    queue.Enqueue(depFile);
                }
            }
        }

        static async Task<ContentFile> ResolveDependencyAsync(IContentProvider provider, ContentDependency dep, string gameVersion, string loader, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(dep.FileId))
            {
                // 作者钉死了版本（Modrinth version_id）就用它
                try { return await provider.GetFileAsync(dep.ProjectId, dep.FileId, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (!string.IsNullOrEmpty(dep.ProjectId)) { Log.Warn($"指定的前置版本 {dep.FileId} 获取失败，改为挑最新版：{ex.Message}"); }
            }
            var files = await provider.GetFilesAsync(dep.ProjectId, gameVersion, loader, ct).ConfigureAwait(false);
            return PickBest(files);
        }

        static async Task FillTitlesAsync(IEnumerable<(ContentSource src, string id, string name)> items, Dictionary<string, string> titles, CancellationToken ct)
        {
            var todo = items.Where(x => x.id != null && !titles.ContainsKey(ContentIndex.Key(x.src, x.id))).ToList();
            if (todo.Count == 0) return;
            try
            {
                foreach (var g in todo.GroupBy(x => x.src))
                {
                    var ids = g.Select(x => x.id).Distinct();
                    var projects = g.Key == ContentSource.CurseForge
                        ? await _curseForge.GetProjectsAsync(ids, ct).ConfigureAwait(false)
                        : await _modrinth.GetProjectsAsync(ids, ct).ConfigureAwait(false);
                    foreach (var p in projects)
                        if (p.Id != null && p.Title != null) titles[ContentIndex.Key(g.Key, p.Id)] = p.Title;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Warn("获取前置名称失败：" + ex.Message); }
        }

        // ---------------- 识别本地文件 ----------------

        static readonly ConcurrentDictionary<string, (long len, DateTime time, string sha1)> HashCache
            = new ConcurrentDictionary<string, (long len, DateTime time, string sha1)>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, bool> NotOnModrinth = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>带缓存的 SHA-1（按路径 + 大小 + 修改时间）</summary>
        public static string CachedSha1(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (HashCache.TryGetValue(path, out var c) && c.len == fi.Length && c.time == fi.LastWriteTimeUtc) return c.sha1;
                var h = Util.Sha1File(path);
                HashCache[path] = (fi.Length, fi.LastWriteTimeUtc, h);
                return h;
            }
            catch { return null; }
        }

        /// <summary>
        /// 给文件夹里还没有索引记录的 jar/zip 算哈希，到 Modrinth 查出所属项目并写进索引。
        /// 失败不影响安装（只是少了"已安装"判断）。
        /// </summary>
        public static async Task IdentifyLocalAsync(string dir, ContentIndex index, CancellationToken ct)
        {
            try
            {
                var files = LocalFiles(dir).Where(f => index.ProjectOf(Path.GetFileName(f)) == null).ToList();
                if (files.Count == 0) return;
                var hashes = await Task.Run(() =>
                {
                    var arr = new string[files.Count];
                    Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = ct }, i => arr[i] = CachedSha1(files[i]));
                    return files.Select((f, i) => (file: f, sha1: arr[i])).Where(x => x.sha1 != null && !NotOnModrinth.ContainsKey(x.sha1)).ToList();
                }, ct).ConfigureAwait(false);
                if (hashes.Count == 0) return;

                var found = await ModrinthProvider.LookupHashesAsync(hashes.Select(h => h.sha1), ct).ConfigureAwait(false);
                foreach (var (f, sha1) in hashes)
                {
                    if (!found.TryGetValue(sha1, out var v)) { NotOnModrinth[sha1] = true; continue; }
                    var cf = ModrinthProvider.ParseVersion(v, sha1);
                    if (cf == null) continue;
                    var name = Path.GetFileName(f);
                    if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 9);
                    index.Set(name, cf);
                }
                index.Save();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Warn("识别已安装文件失败：" + ex.Message); }
        }

        /// <summary>判断某个前置是否已经在目标文件夹里</summary>
        sealed class Presence
        {
            readonly string _dir;
            readonly ContentIndex _index;
            readonly Dictionary<string, string> _titles;
            readonly HashSet<string> _fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> _modIds;

            public Presence(string dir, ContentIndex index, Dictionary<string, string> titles)
            {
                _dir = dir;
                _index = index;
                _titles = titles;
                try
                {
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var n = Path.GetFileName(f);
                        if (n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 9);
                        _fileNames.Add(n);
                    }
                }
                catch { }
            }

            public bool HasFileName(string name) => name != null && _fileNames.Contains(PathGuard.SafeName(name));

            public async Task<bool> IsPresentAsync(ContentSource src, string projectId, CancellationToken ct)
            {
                // 1. 索引（自己装的 + 按哈希认出来的）
                if (_index.ExistingFilesOf(src, projectId).Count > 0) return true;

                // 2. 兜底：项目 slug 和 jar 里的 modId 相同（从别处手动放进来的 Mod）
                ContentProject p = null;
                try
                {
                    p = src == ContentSource.Modrinth
                        ? await _modrinth.GetProjectAsync(projectId, ct, false).ConfigureAwait(false)
                        : await _curseForge.GetProjectAsync(projectId, ct).ConfigureAwait(false);
                    if (p.Title != null) _titles[ContentIndex.Key(src, projectId)] = p.Title;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warn($"获取前置信息失败 {projectId}：{ex.Message}"); }
                if (string.IsNullOrEmpty(p?.Slug)) return false;

                if (_modIds == null)
                {
                    _modIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var mods = await Task.Run(() => LocalMods.Scan(_dir), ct).ConfigureAwait(false);
                        foreach (var m in mods) if (!string.IsNullOrEmpty(m.ModId)) _modIds.Add(Norm(m.ModId));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }
                }
                return _modIds.Contains(Norm(p.Slug));
            }

            static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        /// <summary>目录里的 jar / zip（含 .disabled）</summary>
        internal static List<string> LocalFiles(string dir)
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir).Where(f =>
            {
                var n = f.ToLowerInvariant();
                if (n.EndsWith(".disabled")) n = n.Substring(0, n.Length - 9);
                return n.EndsWith(".jar") || n.EndsWith(".zip") || n.EndsWith(".litemod");
            }).ToList();
        }
    }
}
