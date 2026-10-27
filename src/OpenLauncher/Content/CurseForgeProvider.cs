using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Content
{
    /// <summary>
    /// CurseForge Core API v1（https://docs.curseforge.com/rest-api/）。
    /// 没填 API Key 时走 MCIM 公益镜像（无需 Key）；填了 Key 时先带 Key 请求官方，失败再退回镜像——
    /// Key 只会发给官方接口，绝不会发给镜像。
    /// </summary>
    public class CurseForgeProvider : IContentProvider
    {
        public const string Api = "https://api.curseforge.com/v1";
        public const int GameId = 432;

        public const int ClassMod = 6, ClassModpack = 4471, ClassResourcePack = 12, ClassShader = 6552, ClassDataPack = 6945, ClassWorld = 17;

        public ContentSource Source => ContentSource.CurseForge;

        public static int ClassId(ContentType t) => t switch
        {
            ContentType.Modpack => ClassModpack,
            ContentType.ResourcePack => ClassResourcePack,
            ContentType.Shader => ClassShader,
            ContentType.DataPack => ClassDataPack,
            _ => ClassMod
        };

        public static ContentType TypeFromClass(int classId) => classId switch
        {
            ClassModpack => ContentType.Modpack,
            ClassResourcePack => ContentType.ResourcePack,
            ClassShader => ContentType.Shader,
            ClassDataPack => ContentType.DataPack,
            _ => ContentType.Mod
        };

        /// <summary>CurseForge 的 ModLoaderType：Forge=1, LiteLoader=3, Fabric=4, Quilt=5, NeoForge=6</summary>
        public static int LoaderTypeId(string loader) => (loader ?? "").ToLowerInvariant() switch
        {
            "forge" => 1,
            "liteloader" => 3,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => 0
        };

        public static string LoaderSlug(int id) => id switch
        {
            1 => "forge",
            3 => "liteloader",
            4 => "fabric",
            5 => "quilt",
            6 => "neoforge",
            _ => null
        };

        static string WebSection(ContentType t) => t switch
        {
            ContentType.Modpack => "modpacks",
            ContentType.ResourcePack => "texture-packs",
            ContentType.Shader => "shaders",
            ContentType.DataPack => "data-packs",
            _ => "mc-mods"
        };

        // ---------------- HTTP ----------------

        internal static Task<JNode> GetAsync(string pathAndQuery, CancellationToken ct) => SendAsync(pathAndQuery, null, ct);
        internal static Task<JNode> PostAsync(string path, JNode body, CancellationToken ct) => SendAsync(path, body, ct);

        static async Task<JNode> SendAsync(string path, JNode body, CancellationToken ct)
        {
            var official = Api + path;
            var key = (Settings.CurseForgeApiKey ?? "").Trim();
            bool hasKey = key.Length > 0;

            var attempts = new List<(string url, bool withKey)>();
            if (hasKey)
            {
                attempts.Add((official, true));
                if (Settings.ModSource != SourceMode.OfficialOnly)
                    foreach (var u in Mirrors.Expand(official))
                        if (u != official) attempts.Add((u, false));
            }
            else
            {
                foreach (var u in Mirrors.Expand(official)) attempts.Add((u, false));
            }

            Exception last = null, mirrorError = null;
            bool notFound = false, officialDenied = false;
            foreach (var (url, withKey) in attempts)
            {
                ct.ThrowIfCancellationRequested();
                var headers = withKey ? new Dictionary<string, string> { ["x-api-key"] = key } : null;
                try
                {
                    return body == null
                        ? await Http.GetJsonAsync(url, ct, headers, false, 20).ConfigureAwait(false)
                        : await Http.PostJsonAsync(url, body, headers, ct, false, 30).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    bool isOfficial = url == official;
                    if (ex is HttpStatusException hs)
                    {
                        if (hs.Status == 404) notFound = true;
                        if (isOfficial && (hs.Status == 401 || hs.Status == 403)) officialDenied = true;
                    }
                    if (!isOfficial && mirrorError == null) mirrorError = ex;
                    Log.Warn($"CurseForge 请求失败 {url}：{ex.Message}");
                }
            }

            if (notFound) throw new ContentException("CurseForge 上找不到该资源（可能已被删除）", last, 404);
            if (officialDenied && hasKey && mirrorError == null)
                throw new ContentException("CurseForge 拒绝了请求：API Key 无效或已过期，请在 设置 → 下载 中检查", last, 403);
            if (officialDenied && !hasKey)
            {
                var why = mirrorError == null ? "" : $"（镜像：{Describe(mirrorError)}）";
                throw new ContentException(
                    "CurseForge 暂时无法访问：免 Key 的公益镜像 MCIM 连接失败" + why +
                    "，而官方接口需要 API Key。可以稍后重试、改用 Modrinth，或在 设置 → 下载 中填写自己的 CurseForge API Key。", last, 403);
            }
            throw new ContentException("无法连接 CurseForge：" + Describe(last ?? new Exception("没有可用的地址")), last);
        }

        static string Describe(Exception ex)
        {
            if (ex is HttpStatusException hs) return hs.Status == 503 || hs.Status == 502 ? $"HTTP {hs.Status} 服务暂不可用" : $"HTTP {hs.Status}";
            if (ex is TimeoutException) return "连接超时";
            if (ex is FormatException) return "返回了无法识别的数据";
            return ex.InnerException?.Message ?? ex.Message;
        }

        // ---------------- 搜索 ----------------

        public async Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct)
        {
            int pageSize = Math.Max(1, Math.Min(50, q.Limit));
            int index = Math.Max(0, Math.Min(q.Offset, 10000 - pageSize));
            // 1=Featured(网站上的"相关度") 2=Popularity 3=LastUpdated 6=TotalDownloads 11=ReleasedDate
            var sortField = q.Sort switch
            {
                "downloads" => "6",
                "updated" => "3",
                "newest" => "11",
                "popularity" => "2",
                _ => "1"
            };
            string loaderType = null;
            if (q.Type == ContentType.Mod || q.Type == ContentType.Modpack)
            {
                // CurseForge 上 Quilt 专属资源极少，Quilt 实例按 Fabric 搜（Quilt 能加载 Fabric Mod）
                var l = (q.Loader ?? "").ToLowerInvariant() == "quilt" ? "fabric" : q.Loader;
                var id = LoaderTypeId(l);
                if (id > 0) loaderType = id.ToString();
            }
            var query = Http.Query(
                ("gameId", GameId.ToString()),
                ("classId", ClassId(q.Type).ToString()),
                ("searchFilter", string.IsNullOrWhiteSpace(q.Text) ? null : q.Text.Trim()),
                ("gameVersion", string.IsNullOrWhiteSpace(q.GameVersion) ? null : q.GameVersion.Trim()),
                ("modLoaderType", loaderType),
                ("sortField", sortField),
                ("sortOrder", "desc"),
                ("index", index.ToString()),
                ("pageSize", pageSize.ToString()));
            var json = await GetAsync("/mods/search?" + query, ct).ConfigureAwait(false);
            var result = new SearchResult { Total = Math.Min(10000, json["pagination"]["totalCount"].Int) };
            foreach (var m in json["data"]) result.Items.Add(ParseMod(m, q.Type));
            return result;
        }

        // ---------------- 项目 ----------------

        public async Task<ContentProject> GetProjectAsync(string id, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            var json = await GetAsync("/mods/" + Uri.EscapeDataString(id), ct).ConfigureAwait(false);
            return ParseMod(json["data"], null);
        }

        /// <summary>批量获取项目（POST /v1/mods）</summary>
        public async Task<List<ContentProject>> GetProjectsAsync(IEnumerable<string> ids, CancellationToken ct)
        {
            var list = new List<ContentProject>();
            var all = ids.Where(x => long.TryParse(x, out _)).Distinct().ToList();
            for (int i = 0; i < all.Count; i += 500)
            {
                var arr = JNode.Arr();
                foreach (var x in all.Skip(i).Take(500)) arr.Add(long.Parse(x));
                var json = await PostAsync("/mods", JNode.Obj().Set("modIds", arr), ct).ConfigureAwait(false);
                foreach (var m in json["data"]) list.Add(ParseMod(m, null));
            }
            return list;
        }

        internal static ContentProject ParseMod(JNode m, ContentType? fallbackType)
        {
            var type = m["classId"].IsNull && fallbackType.HasValue ? fallbackType.Value : TypeFromClass(m["classId"].Int);
            var loaders = new List<string>();
            var versions = new List<string>();
            foreach (var idx in m["latestFilesIndexes"])
            {
                var l = LoaderSlug(idx["modLoader"].Int);
                if (l != null && !loaders.Contains(l)) loaders.Add(l);
                var gv = idx["gameVersion"].Str;
                if (IsMcVersion(gv) && !versions.Contains(gv)) versions.Add(gv);
            }
            var slug = m["slug"].Str;
            return new ContentProject
            {
                Source = ContentSource.CurseForge,
                Id = m["id"].Str,
                Slug = slug,
                Title = m["name"].Str,
                Description = m["summary"].Str,
                Author = m["authors"][0]["name"].Str,
                IconUrl = m["logo"]["thumbnailUrl"].Str ?? m["logo"]["url"].Str,
                WebUrl = m["links"]["websiteUrl"].Str ?? $"https://www.curseforge.com/minecraft/{WebSection(type)}/{slug}",
                Downloads = m["downloadCount"].Long,
                Updated = ContentJson.Date(m["dateModified"].IsNull ? m["dateReleased"] : m["dateModified"]),
                Categories = m["categories"].Select(c => c["name"].Str).Where(s => s != null).Distinct().ToList(),
                Loaders = loaders,
                GameVersions = ModrinthProvider.SortVersionsDesc(versions),
                Type = type,
            };
        }

        // ---------------- 文件 ----------------

        public async Task<List<ContentFile>> GetFilesAsync(string projectId, string gameVersion, string loader, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(projectId)) throw new ArgumentNullException(nameof(projectId));
            var loaderIds = new List<int>();
            var lid = LoaderTypeId(loader);
            if (lid > 0) loaderIds.Add(lid);
            if (lid == 5) loaderIds.Add(4); // Quilt 同时列出 Fabric 文件
            if (loaderIds.Count == 0) loaderIds.Add(0);

            var all = new Dictionary<string, ContentFile>();
            foreach (var l in loaderIds)
            {
                // 带筛选时结果一般不多；不带筛选的热门 Mod 可能有上千个文件，最多取前 4 页
                for (int page = 0, index = 0; page < 4; page++)
                {
                    var query = Http.Query(
                        ("gameVersion", string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim()),
                        ("modLoaderType", l > 0 ? l.ToString() : null),
                        ("index", index.ToString()),
                        ("pageSize", "50"));
                    var json = await GetAsync("/mods/" + Uri.EscapeDataString(projectId) + "/files?" + query, ct).ConfigureAwait(false);
                    int n = 0;
                    foreach (var f in json["data"])
                    {
                        n++;
                        var cf = ParseFile(f);
                        if (cf != null) all[cf.FileId] = cf;
                    }
                    index += n;
                    if (n == 0 || index >= json["pagination"]["totalCount"].Int) break;
                }
            }
            return all.Values.OrderByDescending(f => f.Published).ToList();
        }

        public async Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(fileId)) throw new ArgumentNullException(nameof(fileId));
            if (string.IsNullOrEmpty(projectId))
            {
                var files = await GetFilesByIdsAsync(new[] { fileId }, ct).ConfigureAwait(false);
                if (files.Count == 0) throw new ContentException("CurseForge 上找不到该文件", null, 404);
                return files[0];
            }
            var json = await GetAsync("/mods/" + Uri.EscapeDataString(projectId) + "/files/" + Uri.EscapeDataString(fileId), ct).ConfigureAwait(false);
            return ParseFile(json["data"]) ?? throw new ContentException("CurseForge 返回的文件信息不完整");
        }

        /// <summary>批量获取文件（POST /v1/mods/files），用于整合包导入</summary>
        public static async Task<List<ContentFile>> GetFilesByIdsAsync(IEnumerable<string> fileIds, CancellationToken ct)
        {
            var list = new List<ContentFile>();
            var all = fileIds.Where(x => long.TryParse(x, out _)).Distinct().ToList();
            for (int i = 0; i < all.Count; i += 500)
            {
                var arr = JNode.Arr();
                foreach (var x in all.Skip(i).Take(500)) arr.Add(long.Parse(x));
                var json = await PostAsync("/mods/files", JNode.Obj().Set("fileIds", arr), ct).ConfigureAwait(false);
                foreach (var f in json["data"])
                {
                    var cf = ParseFile(f);
                    if (cf != null) list.Add(cf);
                }
            }
            return list;
        }

        internal static ContentFile ParseFile(JNode f)
        {
            var id = f["id"].Str;
            var modId = f["modId"].Str;
            var fileName = f["fileName"].Str;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(fileName)) return null;

            var cf = new ContentFile
            {
                Source = ContentSource.CurseForge,
                ProjectId = modId,
                FileId = id,
                DisplayName = f["displayName"].Str ?? fileName,
                VersionNumber = f["displayName"].Str ?? fileName,
                FileName = fileName,
                Size = f["fileLength"].Long,
                ReleaseType = f["releaseType"].Int switch { 2 => "beta", 3 => "alpha", _ => "release" },
                Published = ContentJson.Date(f["fileDate"]),
            };
            foreach (var h in f["hashes"])
                if (h["algo"].Int == 1) cf.Sha1 = h["value"].Str;

            foreach (var gv in f["gameVersions"].StrList())
            {
                var lower = gv.ToLowerInvariant();
                if (lower == "forge" || lower == "fabric" || lower == "quilt" || lower == "neoforge" || lower == "liteloader")
                {
                    if (!cf.Loaders.Contains(lower)) cf.Loaders.Add(lower);
                }
                else if (IsMcVersion(gv) && !cf.GameVersions.Contains(gv)) cf.GameVersions.Add(gv);
            }
            cf.GameVersions = ModrinthProvider.SortVersionsDesc(cf.GameVersions);

            foreach (var d in f["dependencies"])
            {
                var rel = d["relationType"].Int;
                if (rel != 3 && rel != 2) continue; // 3 = 必需，2 = 可选
                cf.Dependencies.Add(new ContentDependency { ProjectId = d["modId"].Str, Required = rel == 3 });
            }

            var edge = EdgeUrl(id, fileName);
            var dl = f["downloadUrl"].Str;
            cf.Url = string.IsNullOrEmpty(dl) ? edge : dl;
            foreach (var alt in new[] { edge, MediaUrl(id, fileName), modId == null ? null : SiteDownloadUrl(modId, id) })
                if (alt != null && alt != cf.Url && !cf.AltUrls.Contains(alt)) cf.AltUrls.Add(alt);
            return cf;
        }

        // ---------------- 下载地址 ----------------

        static string CdnPath(string fileId, string fileName)
        {
            if (!long.TryParse(fileId, out var id)) return null;
            return $"files/{id / 1000}/{id % 1000}/{Uri.EscapeDataString(fileName)}";
        }

        /// <summary>作者关闭第三方分发（downloadUrl 为 null）时按规则拼出的 CDN 地址</summary>
        public static string EdgeUrl(string fileId, string fileName)
            => CdnPath(fileId, fileName) is string p ? "https://edge.forgecdn.net/" + p : null;

        public static string MediaUrl(string fileId, string fileName)
            => CdnPath(fileId, fileName) is string p ? "https://mediafilez.forgecdn.net/" + p : null;

        /// <summary>CurseForge 网站的公开下载链接（会 302 到真实文件），作为最后的兜底</summary>
        public static string SiteDownloadUrl(string projectId, string fileId)
            => $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{fileId}/download";

        static bool IsMcVersion(string s) => !string.IsNullOrEmpty(s) && char.IsDigit(s[0]) && s.Contains(".");
    }
}
