using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.Content
{
    public enum ModpackFormat { Modrinth, CurseForge }

    /// <summary>整合包里需要下载的一个文件（Modrinth 格式）</summary>
    public class ModpackFileEntry
    {
        /// <summary>相对游戏目录的路径，如 mods/sodium.jar</summary>
        public string Path { get; set; }
        public List<string> Downloads { get; set; } = new List<string>();
        public string Sha1 { get; set; }
        public string Sha512 { get; set; }
        public long Size { get; set; }
    }

    /// <summary>整合包里引用的一个 CurseForge 文件</summary>
    public class ModpackCfEntry
    {
        public string ProjectId { get; set; }
        public string FileId { get; set; }
        public bool Required { get; set; } = true;
    }

    /// <summary>解析后的整合包清单（不含下载），界面可以先拿它做预览</summary>
    public class ModpackInfo
    {
        public ModpackFormat Format { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Summary { get; set; }
        public string McVersion { get; set; }
        public LoaderType Loader { get; set; }
        public string LoaderVersion { get; set; }
        /// <summary>zip 内的根前缀（整合包被多套了一层文件夹时不为空）</summary>
        public string Root { get; set; } = "";
        /// <summary>按顺序解压的覆盖目录（zip 内前缀，后者覆盖前者）</summary>
        public List<string> OverridePrefixes { get; set; } = new List<string>();
        public List<ModpackFileEntry> Files { get; set; } = new List<ModpackFileEntry>();
        public List<ModpackCfEntry> CfFiles { get; set; } = new List<ModpackCfEntry>();

        public string FormatText => Format == ModpackFormat.CurseForge ? "CurseForge" : "Modrinth";
        public string LoaderText => Loader == LoaderType.None ? "原版" : $"{LoaderInfo.Name(Loader)} {LoaderVersion}";
        public int FileCount => Format == ModpackFormat.CurseForge ? CfFiles.Count(f => f.Required) : Files.Count;
    }

    public static class ModpackImporter
    {
        /// <summary>只读清单，不安装。格式不对或包含越界路径时抛 ContentException。</summary>
        public static ModpackInfo ReadInfo(string packFile)
        {
            if (!File.Exists(packFile)) throw new FileNotFoundException("找不到整合包文件", packFile);
            try
            {
                using (var zip = ZipFile.OpenRead(packFile))
                    return Parse(zip);
            }
            catch (InvalidDataException ex) { throw new ContentException("整合包文件损坏或不是 zip 格式：" + ex.Message, ex); }
        }

        static ModpackInfo Parse(ZipArchive zip)
        {
            // 允许整合包外面多套一层文件夹（用户自己重新压缩时常见）
            ZipArchiveEntry Find(string name)
            {
                var e = zip.GetEntry(name);
                if (e != null) return e;
                return zip.Entries.FirstOrDefault(x =>
                {
                    var f = x.FullName.Replace('\\', '/');
                    var i = f.IndexOf('/');
                    return i > 0 && i == f.LastIndexOf('/') && f.Substring(i + 1) == name;
                });
            }

            var mr = Find("modrinth.index.json");
            if (mr != null) return ParseModrinth(zip, mr);
            var cf = Find("manifest.json");
            if (cf != null)
            {
                var j = ReadJson(cf);
                if (j["manifestType"].Str == "minecraftModpack" || !j["minecraft"].IsNull) return ParseCurseForge(zip, cf, j);
            }
            if (Find("mmc-pack.json") != null || Find("instance.cfg") != null)
                throw new ContentException("暂不支持 MultiMC / Prism 格式的整合包，请导入 Modrinth（.mrpack）或 CurseForge 格式");
            throw new ContentException("无法识别的整合包：找不到 modrinth.index.json 或 CurseForge 的 manifest.json");
        }

        static JNode ReadJson(ZipArchiveEntry e)
        {
            using (var r = new StreamReader(e.Open(), System.Text.Encoding.UTF8))
            {
                var j = LocalMods.ParseLenient(r.ReadToEnd());
                if (j == null || !j.IsObj) throw new ContentException($"整合包清单 {e.Name} 格式错误");
                return j;
            }
        }

        static string RootOf(ZipArchiveEntry e)
        {
            var f = e.FullName.Replace('\\', '/');
            var i = f.LastIndexOf('/');
            return i < 0 ? "" : f.Substring(0, i + 1);
        }

        /// <summary>
        /// 越界路径（..、绝对路径、盘符）直接拒绝整个整合包；strict 时连"Windows 上不合法的文件名"也拒绝
        /// （覆盖文件里的怪文件名只是跳过，见 ExtractOverrides）。
        /// </summary>
        static void CheckRelative(string rel, string what, bool strict)
        {
            if (PathGuard.IsEscaping(rel) || (strict && PathGuard.Resolve(@"C:\__ol_check__", rel) == null))
                throw new ContentException($"整合包包含非法路径，已拒绝导入（{what}：{rel}）");
        }

        static ModpackInfo ParseModrinth(ZipArchive zip, ZipArchiveEntry entry)
        {
            var j = ReadJson(entry);
            if (j["game"].Str != null && j["game"].Str != "minecraft") throw new ContentException("这不是 Minecraft 整合包");
            var info = new ModpackInfo
            {
                Format = ModpackFormat.Modrinth,
                Name = j["name"].Str,
                Version = j["versionId"].Str,
                Summary = j["summary"].Str,
                Root = RootOf(entry),
            };
            var deps = j["dependencies"];
            info.McVersion = deps["minecraft"].Str ?? "";
            foreach (var (key, type) in new[] { ("neoforge", LoaderType.NeoForge), ("forge", LoaderType.Forge), ("fabric-loader", LoaderType.Fabric), ("quilt-loader", LoaderType.Quilt) })
            {
                var v = deps[key].Str;
                if (string.IsNullOrEmpty(v)) continue;
                // 个别整合包把版本写成 1.20.1-47.2.0
                if (v.StartsWith(info.McVersion + "-")) v = v.Substring(info.McVersion.Length + 1);
                info.Loader = type;
                info.LoaderVersion = v;
                break;
            }
            if (info.McVersion.Length == 0) throw new ContentException("整合包没有写明 Minecraft 版本");

            foreach (var f in j["files"])
            {
                if (f["env"]["client"].Str == "unsupported") continue;
                var path = f["path"].Str;
                CheckRelative(path, "files", true);
                var e = new ModpackFileEntry
                {
                    Path = path.Replace('\\', '/'),
                    Sha1 = f["hashes"]["sha1"].Str,
                    Sha512 = f["hashes"]["sha512"].Str,
                    Size = f["fileSize"].Long,
                };
                foreach (var u in f["downloads"].StrList())
                    if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")) e.Downloads.Add(u);
                if (e.Downloads.Count == 0) throw new ContentException($"整合包里的 {path} 没有可用的下载地址");
                info.Files.Add(e);
            }
            info.OverridePrefixes.Add(info.Root + "overrides/");
            info.OverridePrefixes.Add(info.Root + "client-overrides/");
            CheckOverrides(zip, info);
            return info;
        }

        static ModpackInfo ParseCurseForge(ZipArchive zip, ZipArchiveEntry entry, JNode j)
        {
            var info = new ModpackInfo
            {
                Format = ModpackFormat.CurseForge,
                Name = j["name"].Str,
                Version = j["version"].Str,
                Author = j["author"].Str,
                Root = RootOf(entry),
                McVersion = j["minecraft"]["version"].Str,
            };
            if (string.IsNullOrEmpty(info.McVersion)) throw new ContentException("整合包没有写明 Minecraft 版本");

            JNode primary = JNode.Null;
            foreach (var l in j["minecraft"]["modLoaders"])
                if (primary.IsNull || l["primary"].Bool) { primary = l; if (l["primary"].Bool) break; }
            var id = primary["id"].Str;
            if (!string.IsNullOrEmpty(id))
            {
                var dash = id.IndexOf('-');
                var kind = (dash < 0 ? id : id.Substring(0, dash)).ToLowerInvariant();
                var ver = dash < 0 ? null : id.Substring(dash + 1);
                // 个别整合包写成 forge-1.20.1-47.2.0
                if (ver != null && ver.StartsWith(info.McVersion + "-")) ver = ver.Substring(info.McVersion.Length + 1);
                info.Loader = kind switch
                {
                    "forge" => LoaderType.Forge,
                    "neoforge" => LoaderType.NeoForge,
                    "fabric" => LoaderType.Fabric,
                    "quilt" => LoaderType.Quilt,
                    _ => LoaderType.None
                };
                if (info.Loader == LoaderType.None) throw new ContentException($"不支持的加载器：{id}");
                info.LoaderVersion = ver;
            }

            foreach (var f in j["files"])
            {
                var pid = f["projectID"].Str;
                var fid = f["fileID"].Str;
                if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(fid)) continue;
                info.CfFiles.Add(new ModpackCfEntry { ProjectId = pid, FileId = fid, Required = f["required"].BoolOr(true) });
            }
            var ov = (j["overrides"].Str ?? "overrides").Replace('\\', '/').Trim('/');
            CheckRelative(ov, "overrides", true);
            info.OverridePrefixes.Add(info.Root + ov + "/");
            CheckOverrides(zip, info);
            return info;
        }

        static void CheckOverrides(ZipArchive zip, ModpackInfo info)
        {
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                foreach (var p in info.OverridePrefixes)
                {
                    if (!name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = name.Substring(p.Length);
                    if (rel.Length == 0 || rel.EndsWith("/")) continue;
                    CheckRelative(rel, "overrides", false);
                }
            }
        }

        // ---------------- 导入 ----------------

        public static Task<string> ImportAsync(GameRepo repo, string packFile, string name, LauncherTask task)
            => ImportCoreAsync(repo, packFile, name, task, 0, 1, null);

        /// <summary>先把整合包下载到临时目录再导入</summary>
        public static async Task<string> InstallFromFileAsync(GameRepo repo, ContentFile packFile, string name, LauncherTask task)
        {
            if (packFile == null) throw new ArgumentNullException(nameof(packFile));
            var dir = Paths.Ensure(Path.Combine(Paths.Temp, "modpacks", Guid.NewGuid().ToString("N").Substring(0, 8)));
            var path = PathGuard.FileIn(dir, packFile.FileName);
            try
            {
                await Downloader.RunAsync(new[]
                {
                    new DownloadItem { Url = packFile.Url, AltUrls = packFile.AltUrls?.ToList(), Path = path, Sha1 = packFile.Sha1, Sha512 = packFile.Sha512, Size = packFile.Size }
                }, task, 0, 0.12, "下载整合包").ConfigureAwait(false);
                var source = JNode.Obj()
                    .Set("source", packFile.Source == ContentSource.CurseForge ? "curseforge" : "modrinth")
                    .Set("projectId", packFile.ProjectId)
                    .Set("fileId", packFile.FileId);
                return await ImportCoreAsync(repo, path, name, task, 0.12, 1, source).ConfigureAwait(false);
            }
            finally { Util.TryDeleteDir(dir); }
        }

        static async Task<string> ImportCoreAsync(GameRepo repo, string packFile, string name, LauncherTask task, double from, double to, JNode source)
        {
            if (repo == null) throw new ArgumentNullException(nameof(repo));
            var ct = task?.Token ?? CancellationToken.None;
            double P(double x) => from + (to - from) * x;
            void Report(double x, string detail) => task?.Report(P(x), detail);

            Report(0, "读取整合包…");
            var info = await Task.Run(() => ReadInfo(packFile), ct).ConfigureAwait(false);
            Log.Info($"导入整合包：{info.Name} {info.Version}（{info.FormatText}，MC {info.McVersion}，{info.LoaderText}，{info.FileCount} 个文件）");

            var wanted = repo.UniqueName(string.IsNullOrWhiteSpace(name) ? (info.Name ?? Path.GetFileNameWithoutExtension(packFile)) : name);
            // 记下已有的版本文件夹：失败清理时绝不能删到用户原有的版本
            var existing = new HashSet<string>(
                Directory.Exists(repo.Versions) ? Directory.GetDirectories(repo.Versions).Select(Path.GetFileName) : Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            string id = null;
            try
            {
                id = await Installer.InstallAsync(new InstallRequest
                {
                    Repo = repo,
                    McVersion = info.McVersion,
                    Loader = info.Loader,
                    LoaderVersion = info.LoaderVersion,
                    Name = wanted,
                }, task, P(0), P(0.45)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(id) || existing.Contains(id))
                {
                    var reused = id;
                    id = null;
                    throw new ContentException($"安装器没有新建版本文件夹（返回了 {reused ?? "空"}），为避免覆盖已有版本已停止导入");
                }

                var inst = new Instance(repo, id);
                inst.SetSetting("isolation", "on");
                var meta = JNode.Obj()
                    .Set("name", info.Name)
                    .Set("version", info.Version)
                    .Set("format", info.Format == ModpackFormat.CurseForge ? "curseforge" : "modrinth")
                    .Set("author", info.Author)
                    .Set("mcVersion", info.McVersion)
                    .Set("loader", LoaderInfo.Slug(info.Loader))
                    .Set("loaderVersion", info.LoaderVersion)
                    .Set("importedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                if (source != null) foreach (var kv in source.Props) meta.Set(kv.Key, kv.Value);
                inst.SetSetting("modpack", meta);

                var gameDir = inst.Dir; // 隔离后游戏目录就是版本文件夹
                Directory.CreateDirectory(gameDir);

                Report(0.45, "解压覆盖文件…");
                await Task.Run(() => ExtractOverrides(packFile, info, gameDir, id, x => Report(0.45 + 0.05 * x, null), ct), ct).ConfigureAwait(false);

                if (info.Format == ModpackFormat.Modrinth)
                    await DownloadModrinthAsync(info, gameDir, id, task, P(0.5), P(1)).ConfigureAwait(false);
                else
                    await DownloadCurseForgeAsync(info, gameDir, id, task, P(0.5), P(1)).ConfigureAwait(false);

                Report(1, "整合包导入完成");
                Log.Info($"整合包导入完成：{id}");
                return id;
            }
            catch
            {
                // 失败或取消：清掉这次新建的半成品版本，避免留下一个打不开的实例
                foreach (var d in new[] { id, wanted })
                    if (!string.IsNullOrEmpty(d) && !existing.Contains(d)) Util.TryDeleteDir(repo.VersionDir(d));
                throw;
            }
        }

        /// <summary>版本文件夹里不能被整合包覆盖的东西</summary>
        static bool IsProtected(string gameDir, string full, string id)
        {
            var rel = full.Substring(Path.GetFullPath(gameDir).TrimEnd('\\').Length).TrimStart('\\');
            var first = rel.Split('\\')[0];
            return first.Equals(id + ".json", StringComparison.OrdinalIgnoreCase)
                || first.Equals(id + ".jar", StringComparison.OrdinalIgnoreCase)
                || first.Equals(id + "-natives", StringComparison.OrdinalIgnoreCase)
                || first.Equals(Branding.AppName, StringComparison.OrdinalIgnoreCase);
        }

        static void ExtractOverrides(string packFile, ModpackInfo info, string gameDir, string id, Action<double> progress, CancellationToken ct)
        {
            using (var zip = ZipFile.OpenRead(packFile))
            {
                var entries = new List<(ZipArchiveEntry e, string target)>();
                foreach (var prefix in info.OverridePrefixes)
                    foreach (var e in zip.Entries)
                    {
                        var name = e.FullName.Replace('\\', '/');
                        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        var rel = name.Substring(prefix.Length);
                        if (rel.Length == 0 || name.EndsWith("/")) continue;
                        var target = PathGuard.Resolve(gameDir, rel);
                        if (target == null) { Log.Warn("跳过文件名不合法的覆盖文件：" + name); continue; }
                        if (IsProtected(gameDir, target, id)) { Log.Warn("跳过会覆盖版本文件的条目：" + name); continue; }
                        entries.Add((e, target));
                    }
                for (int i = 0; i < entries.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (e, target) = entries[i];
                    Util.EnsureParent(target);
                    e.ExtractToFile(target, true);
                    if (i % 20 == 0) progress((double)i / entries.Count);
                }
                Log.Info($"已解压 {entries.Count} 个覆盖文件");
            }
        }

        static async Task DownloadModrinthAsync(ModpackInfo info, string gameDir, string id, LauncherTask task, double from, double to)
        {
            var ct = task?.Token ?? CancellationToken.None;
            var items = new List<DownloadItem>();
            foreach (var f in info.Files)
            {
                var path = PathGuard.Resolve(gameDir, f.Path) ?? throw new ContentException("整合包包含非法路径，已拒绝导入：" + f.Path);
                if (IsProtected(gameDir, path, id)) throw new ContentException("整合包试图覆盖版本文件，已拒绝导入：" + f.Path);
                items.Add(new DownloadItem
                {
                    Url = f.Downloads[0],
                    AltUrls = f.Downloads.Skip(1).ToList(),
                    Path = path,
                    Sha1 = f.Sha1,
                    Sha512 = string.IsNullOrEmpty(f.Sha1) ? f.Sha512 : null,
                    Size = f.Size,
                });
            }
            await Downloader.RunAsync(items, task, from, to, "下载整合包文件").ConfigureAwait(false);

            // 顺手记下 Modrinth 项目，之后装前置 / 换版本时能认出来（失败无所谓）
            try
            {
                var withHash = info.Files.Where(f => !string.IsNullOrEmpty(f.Sha1)).ToList();
                if (withHash.Count == 0) return;
                var found = await ModrinthProvider.LookupHashesAsync(withHash.Select(f => f.Sha1), ct).ConfigureAwait(false);
                var indexes = new Dictionary<string, ContentIndex>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in withHash)
                {
                    if (!found.TryGetValue(f.Sha1, out var v)) continue;
                    var full = PathGuard.Resolve(gameDir, f.Path);
                    var dir = Path.GetDirectoryName(full);
                    if (!indexes.TryGetValue(dir, out var idx)) indexes[dir] = idx = ContentIndex.Load(dir);
                    var cf = ModrinthProvider.ParseVersion(v, f.Sha1);
                    if (cf != null) idx.Set(Path.GetFileName(full), cf);
                }
                foreach (var idx in indexes.Values) idx.Save();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Warn("记录整合包 Mod 来源失败：" + ex.Message); }
        }

        static async Task DownloadCurseForgeAsync(ModpackInfo info, string gameDir, string id, LauncherTask task, double from, double to)
        {
            var ct = task?.Token ?? CancellationToken.None;
            var entries = info.CfFiles.Where(f => f.Required).ToList();
            if (entries.Count == 0) { task?.Report(to); return; }

            task?.Report(from, "获取 CurseForge 文件信息…");
            var files = new Dictionary<string, ContentFile>();
            try
            {
                foreach (var f in await CurseForgeProvider.GetFilesByIdsAsync(entries.Select(e => e.FileId), ct).ConfigureAwait(false))
                    files[f.FileId] = f;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Warn("批量获取 CurseForge 文件失败，改为逐个解析下载链接：" + ex.Message); }

            // 按项目类型分文件夹（Mod / 资源包 / 光影）
            var classes = new Dictionary<string, ContentType>();
            if (files.Count > 0)
            {
                try
                {
                    foreach (var p in await ((CurseForgeProvider)ContentService.CurseForge).GetProjectsAsync(entries.Select(e => e.ProjectId), ct).ConfigureAwait(false))
                        classes[p.Id] = p.Type;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warn("获取 CurseForge 项目类型失败，按扩展名判断：" + ex.Message); }
            }

            // API 里查不到的文件：通过 CurseForge 网站的公开下载链接拿到真实文件名
            var missing = entries.Where(e => !files.ContainsKey(e.FileId)).ToList();
            var failed = new ConcurrentBag<string>();
            if (missing.Count > 0)
            {
                task?.Report(from, $"解析下载链接（{missing.Count} 个）…");
                using (var sem = new SemaphoreSlim(8))
                {
                    int done = 0;
                    await Task.WhenAll(missing.Select(async e =>
                    {
                        await sem.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var site = CurseForgeProvider.SiteDownloadUrl(e.ProjectId, e.FileId);
                            var real = await ResolveSiteDownloadAsync(site, ct).ConfigureAwait(false);
                            var fileName = Uri.UnescapeDataString(new Uri(real).AbsolutePath.Split('/').Last());
                            var cf = new ContentFile
                            {
                                Source = ContentSource.CurseForge,
                                ProjectId = e.ProjectId,
                                FileId = e.FileId,
                                FileName = fileName,
                                DisplayName = fileName,
                                Url = CurseForgeProvider.EdgeUrl(e.FileId, fileName) ?? real,
                            };
                            cf.AltUrls.Add(CurseForgeProvider.MediaUrl(e.FileId, fileName));
                            cf.AltUrls.Add(site);
                            lock (files) files[e.FileId] = cf;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            failed.Add($"{e.ProjectId}/{e.FileId}");
                            Log.Warn($"解析 CurseForge 下载链接失败 {e.ProjectId}/{e.FileId}：{ex.Message}");
                        }
                        finally
                        {
                            sem.Release();
                            var n = Interlocked.Increment(ref done);
                            task?.Report(from + (to - from) * 0.1 * n / missing.Count);
                        }
                    })).ConfigureAwait(false);
                }
            }
            if (!failed.IsEmpty)
                throw new ContentException($"有 {failed.Count} 个文件无法获取下载地址（CurseForge 暂时无法访问）。可以稍后重试，或在 设置 → 下载 中填写 CurseForge API Key。");

            var items = new List<DownloadItem>();
            var placed = new List<(ContentFile file, string path)>();
            foreach (var e in entries)
            {
                var f = files[e.FileId];
                string folder;
                if (classes.TryGetValue(e.ProjectId, out var type))
                    folder = type switch
                    {
                        ContentType.ResourcePack => "resourcepacks",
                        ContentType.Shader => "shaderpacks",
                        ContentType.DataPack => "datapacks",
                        _ => "mods"
                    };
                else folder = f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "resourcepacks" : "mods";
                var path = PathGuard.FileIn(Path.Combine(gameDir, folder), f.FileName);
                items.Add(new DownloadItem { Url = f.Url, AltUrls = f.AltUrls.Where(u => u != null).ToList(), Path = path, Sha1 = f.Sha1, Size = f.Size });
                placed.Add((f, path));
            }
            await Downloader.RunAsync(items, task, from + (to - from) * 0.1, to, "下载整合包文件").ConfigureAwait(false);

            try
            {
                foreach (var g in placed.GroupBy(p => Path.GetDirectoryName(p.path), StringComparer.OrdinalIgnoreCase))
                {
                    var idx = ContentIndex.Load(g.Key);
                    foreach (var (file, path) in g) idx.Set(Path.GetFileName(path), file);
                    idx.Save();
                }
            }
            catch (Exception ex) { Log.Warn("记录整合包 Mod 来源失败：" + ex.Message); }
        }

        static readonly HttpClient NoRedirect = CreateNoRedirectClient();

        static HttpClient CreateNoRedirectClient()
        {
            var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(Http.UserAgent);
            return c;
        }

        /// <summary>CurseForge 网站下载链接 → 第一次跳转的 CDN 地址（不下载文件本体）</summary>
        static async Task<string> ResolveSiteDownloadAsync(string url, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var resp = await NoRedirect.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(new Uri(url), resp.Headers.Location);
                            var clean = loc.GetLeftPart(UriPartial.Path);
                            if (!clean.EndsWith("/")) return clean;
                        }
                        throw new HttpStatusException(url, code, null);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) when (attempt < 1) { await Task.Delay(500, ct).ConfigureAwait(false); }
            }
        }
    }
}
