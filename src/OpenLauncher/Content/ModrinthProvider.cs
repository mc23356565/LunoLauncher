using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Content
{
    /// <summary>
    /// Modrinth API v2（https://docs.modrinth.com/api/）。请求统一写官方地址，
    /// 由 Http + Mirrors 自动按设置走 MCIM 镜像或官方源。
    /// </summary>
    public class ModrinthProvider : IContentProvider
    {
        public const string Api = "https://api.modrinth.com/v2";
        public ContentSource Source => ContentSource.Modrinth;

        /// <summary>出现在 categories 里但其实是"加载器"的标签</summary>
        static readonly HashSet<string> LoaderTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fabric", "forge", "neoforge", "quilt", "liteloader", "modloader", "rift", "risugami", "legacy-fabric", "ornithe", "nilloader", "babric", "bta-babric", "java-agent",
            "iris", "optifine", "canvas", "vanilla", "minecraft", "datapack",
            "bukkit", "spigot", "paper", "purpur", "folia", "sponge", "bungeecord", "velocity", "waterfall", "geyser",
        };

        /// <summary>界面上值得展示的加载器（服务端插件平台不展示）</summary>
        static readonly HashSet<string> ShownLoaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fabric", "forge", "neoforge", "quilt", "liteloader", "iris", "optifine", "canvas", "vanilla", "datapack",
        };

        // ---------------- HTTP ----------------

        /// <summary>
        /// 逐个尝试镜像 / 官方。官方返回 404 立即认定不存在；镜像的 404 可能只是没同步，继续试下一个，
        /// 但最后仍以 404 为准（避免"镜像 503"盖住真正的"找不到"）。
        /// </summary>
        internal static async Task<JNode> GetAsync(string pathAndQuery, CancellationToken ct, string api = Api)
        {
            var official = api + pathAndQuery;
            Exception last = null, notFound = null;
            foreach (var url in Mirrors.Expand(official))
            {
                ct.ThrowIfCancellationRequested();
                try { return await Http.GetJsonAsync(url, ct, null, false, 20).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    if (ex is HttpStatusException hs && hs.Status == 404)
                    {
                        notFound = ex;
                        if (url == official) break;
                    }
                }
            }
            throw Wrap(notFound ?? last ?? new Exception("没有可用的地址"));
        }

        internal static async Task<JNode> PostAsync(string path, JNode body, CancellationToken ct)
        {
            try { return await Http.PostJsonAsync(Api + path, body, null, ct, true, 30).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested)) { throw Wrap(ex); }
        }

        static Exception Wrap(Exception ex)
        {
            if (ex is ContentException) return ex;
            if (ex is HttpStatusException hs)
            {
                if (hs.Status == 404) return new ContentException("Modrinth 上找不到该资源（可能已被删除或隐藏）", ex, 404);
                if (hs.Status == 429) return new ContentException("Modrinth 请求过于频繁，请稍等一会儿再试", ex, 429);
                if (hs.Status == 410) return new ContentException("Modrinth 接口已变更，请更新启动器", ex, 410);
                return new ContentException($"Modrinth 返回错误（HTTP {hs.Status}），请稍后重试或在设置中切换下载源", ex, hs.Status);
            }
            if (ex is TimeoutException) return new ContentException("连接 Modrinth 超时，请检查网络或在 设置 → 下载 中切换下载源", ex);
            if (ex is HttpRequestException) return new ContentException("无法连接 Modrinth：" + (ex.InnerException?.Message ?? ex.Message), ex);
            if (ex is FormatException) return new ContentException("Modrinth 返回了无法识别的数据（镜像可能异常），请稍后重试", ex);
            return new ContentException("Modrinth 请求失败：" + ex.Message, ex);
        }

        static string Arr(IEnumerable<string> items) => ContentJson.StrArr(items).ToJson(false);

        // ---------------- 类型映射 ----------------

        public static string TypeSlug(ContentType t) => t switch
        {
            ContentType.Modpack => "modpack",
            ContentType.ResourcePack => "resourcepack",
            ContentType.Shader => "shader",
            ContentType.DataPack => "datapack",
            _ => "mod"
        };

        public static ContentType TypeFromSlug(string s, IEnumerable<string> loaders = null)
        {
            switch (s)
            {
                case "modpack": return ContentType.Modpack;
                case "resourcepack": return ContentType.ResourcePack;
                case "shader": return ContentType.Shader;
                case "datapack": return ContentType.DataPack;
            }
            // Modrinth 把纯数据包也记成 mod，只是加载器只有 datapack
            if (loaders != null)
            {
                var l = loaders.ToList();
                if (l.Count > 0 && l.All(x => x == "datapack")) return ContentType.DataPack;
            }
            return ContentType.Mod;
        }

        /// <summary>Quilt 能直接加载绝大多数 Fabric Mod，所以查 Quilt 时一并查 Fabric</summary>
        internal static List<string> LoaderFilter(string loader)
        {
            if (string.IsNullOrEmpty(loader)) return null;
            loader = loader.ToLowerInvariant();
            if (loader == "quilt") return new List<string> { "quilt", "fabric" };
            return new List<string> { loader };
        }

        static string WebUrl(string typeSlug, string slug) => $"https://modrinth.com/{typeSlug}/{slug}";

        // ---------------- 搜索 ----------------

        public async Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct)
        {
            var facets = JNode.Arr();
            var typeSlug = TypeSlug(q.Type);
            facets.Add(JNode.Arr().Add("project_type:" + typeSlug));
            if ((q.Type == ContentType.Mod || q.Type == ContentType.Modpack) && LoaderFilter(q.Loader) is List<string> lf)
            {
                var or = JNode.Arr();
                foreach (var l in lf) or.Add("categories:" + l);
                facets.Add(or);
            }
            if (!string.IsNullOrWhiteSpace(q.GameVersion)) facets.Add(JNode.Arr().Add("versions:" + q.GameVersion.Trim()));

            var index = q.Sort switch
            {
                "downloads" => "downloads",
                "updated" => "updated",
                "newest" => "newest",
                "follows" => "follows",
                _ => "relevance"
            };
            var query = Http.Query(
                ("query", string.IsNullOrWhiteSpace(q.Text) ? null : q.Text.Trim()),
                ("facets", facets.ToJson(false)),
                ("index", index),
                ("offset", Math.Max(0, q.Offset).ToString()),
                ("limit", Math.Max(1, Math.Min(100, q.Limit)).ToString()));
            var json = await GetAsync("/search?" + query, ct).ConfigureAwait(false);

            var result = new SearchResult { Total = json["total_hits"].Int };
            foreach (var h in json["hits"])
            {
                var cats = h["categories"].StrList();
                var p = new ContentProject
                {
                    Source = ContentSource.Modrinth,
                    Id = h["project_id"].Str,
                    Slug = h["slug"].Str,
                    Title = h["title"].Str,
                    Description = h["description"].Str,
                    Author = h["author"].Str,
                    IconUrl = NullIfEmpty(h["icon_url"].Str),
                    Downloads = h["downloads"].Long,
                    Updated = ContentJson.Date(h["date_modified"]),
                    Categories = (h["display_categories"].IsArr ? h["display_categories"].StrList() : cats).Where(c => !LoaderTags.Contains(c)).ToList(),
                    Loaders = cats.Where(c => ShownLoaders.Contains(c)).Select(c => c.ToLowerInvariant()).ToList(),
                    GameVersions = SortVersionsDesc(h["versions"].StrList()),
                    Type = q.Type,
                };
                p.WebUrl = WebUrl(typeSlug, p.Slug ?? p.Id);
                result.Items.Add(p);
            }
            return result;
        }

        // ---------------- 项目 ----------------

        public Task<ContentProject> GetProjectAsync(string id, CancellationToken ct) => GetProjectAsync(id, ct, true);

        internal async Task<ContentProject> GetProjectAsync(string id, CancellationToken ct, bool withAuthor)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            var j = await GetAsync("/project/" + Uri.EscapeDataString(id), ct).ConfigureAwait(false);
            var p = ParseProject(j);
            if (withAuthor)
            {
                try { p.Author = await GetAuthorAsync(j, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warn($"获取 Modrinth 作者失败 {id}：{ex.Message}"); }
            }
            return p;
        }

        /// <summary>批量获取项目（不含作者），用于依赖展示等</summary>
        public async Task<List<ContentProject>> GetProjectsAsync(IEnumerable<string> ids, CancellationToken ct)
        {
            var list = new List<ContentProject>();
            var all = ids.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            for (int i = 0; i < all.Count; i += 100)
            {
                var chunk = all.Skip(i).Take(100);
                var j = await GetAsync("/projects?" + Http.Query(("ids", Arr(chunk))), ct).ConfigureAwait(false);
                foreach (var p in j) list.Add(ParseProject(p));
            }
            return list;
        }

        static ContentProject ParseProject(JNode j)
        {
            var loaders = j["loaders"].StrList();
            var typeSlug = j["project_type"].StrOr("mod");
            var type = TypeFromSlug(typeSlug, loaders);
            var cats = j["categories"].StrList();
            cats.AddRange(j["additional_categories"].StrList());
            return new ContentProject
            {
                Source = ContentSource.Modrinth,
                Id = j["id"].Str,
                Slug = j["slug"].Str,
                Title = j["title"].Str,
                Description = j["description"].Str,
                IconUrl = NullIfEmpty(j["icon_url"].Str),
                Downloads = j["downloads"].Long,
                Updated = ContentJson.Date(j["updated"]),
                Categories = cats.Where(c => !LoaderTags.Contains(c)).Distinct().ToList(),
                Loaders = loaders.Where(c => ShownLoaders.Contains(c)).Select(c => c.ToLowerInvariant()).ToList(),
                GameVersions = SortVersionsDesc(j["game_versions"].StrList()),
                Type = type,
                WebUrl = WebUrl(type == ContentType.DataPack ? "datapack" : typeSlug, j["slug"].Str ?? j["id"].Str),
            };
        }

        static async Task<string> GetAuthorAsync(JNode project, CancellationToken ct)
        {
            var org = project["organization"].Str;
            if (!string.IsNullOrEmpty(org))
            {
                try
                {
                    var o = await GetAsync("/organization/" + Uri.EscapeDataString(org), ct, "https://api.modrinth.com/v3").ConfigureAwait(false);
                    var name = o["name"].Str;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* 退回团队成员 */ }
            }
            var members = await GetAsync("/project/" + Uri.EscapeDataString(project["id"].Str) + "/members", ct).ConfigureAwait(false);
            JNode owner = null;
            foreach (var m in members)
                if (m["role"].Str == "Owner" || m["is_owner"].Bool) { owner = m; break; }
            owner = owner ?? members[0];
            return owner["user"]["username"].Str;
        }

        // ---------------- 文件 ----------------

        public async Task<List<ContentFile>> GetFilesAsync(string projectId, string gameVersion, string loader, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(projectId)) throw new ArgumentNullException(nameof(projectId));
            var lf = LoaderFilter(loader);
            var query = Http.Query(
                ("loaders", lf == null ? null : Arr(lf)),
                ("game_versions", string.IsNullOrWhiteSpace(gameVersion) ? null : Arr(new[] { gameVersion.Trim() })),
                ("include_changelog", "false")); // 不要更新日志，热门项目能小好几 MB
            var url = "/project/" + Uri.EscapeDataString(projectId) + "/version?" + query;
            var json = await GetAsync(url, ct).ConfigureAwait(false);
            var list = new List<ContentFile>();
            foreach (var v in json)
            {
                var f = ParseVersion(v);
                if (f != null) list.Add(f);
            }
            return list.OrderByDescending(f => f.Published).ToList();
        }

        public async Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(fileId)) throw new ArgumentNullException(nameof(fileId));
            var v = await GetAsync("/version/" + Uri.EscapeDataString(fileId), ct).ConfigureAwait(false);
            var f = ParseVersion(v);
            if (f == null) throw new ContentException("这个版本没有可下载的文件");
            return f;
        }

        /// <summary>把一个 Modrinth version 转成 ContentFile；preferSha1 指定时取哈希匹配的那个文件，否则取主文件</summary>
        internal static ContentFile ParseVersion(JNode v, string preferSha1 = null)
        {
            JNode file = null;
            foreach (var f in v["files"])
            {
                if (preferSha1 != null && string.Equals(f["hashes"]["sha1"].Str, preferSha1, StringComparison.OrdinalIgnoreCase)) { file = f; break; }
                if (preferSha1 == null && f["primary"].Bool) { file = f; break; }
            }
            if (file == null)
            {
                foreach (var f in v["files"]) if (f["primary"].Bool) { file = f; break; }
                if (file == null) file = v["files"][0];
            }
            if (file.IsNull || string.IsNullOrEmpty(file["url"].Str)) return null;

            var cf = new ContentFile
            {
                Source = ContentSource.Modrinth,
                ProjectId = v["project_id"].Str,
                FileId = v["id"].Str,
                DisplayName = v["name"].Str ?? v["version_number"].Str,
                VersionNumber = v["version_number"].Str,
                FileName = file["filename"].Str ?? Path.GetFileName(Uri.UnescapeDataString(new Uri(file["url"].Str).AbsolutePath)),
                Url = file["url"].Str,
                Sha1 = file["hashes"]["sha1"].Str,
                Sha512 = file["hashes"]["sha512"].Str,
                Size = file["size"].Long,
                GameVersions = v["game_versions"].StrList(),
                Loaders = v["loaders"].StrList().Select(x => x.ToLowerInvariant()).ToList(),
                ReleaseType = v["version_type"].StrOr("release"),
                Published = ContentJson.Date(v["date_published"]),
            };
            foreach (var d in v["dependencies"])
            {
                var type = d["dependency_type"].Str;
                if (type != "required" && type != "optional") continue; // incompatible / embedded 不需要下载
                cf.Dependencies.Add(new ContentDependency
                {
                    ProjectId = d["project_id"].Str,
                    FileId = d["version_id"].Str,
                    FileName = d["file_name"].Str,
                    Required = type == "required",
                });
            }
            return cf;
        }

        // ---------------- 哈希查询（更新检查 / 导出整合包） ----------------

        /// <summary>sha1 → 该文件所属的 version（JSON）。查不到的哈希不在结果里。</summary>
        public static async Task<Dictionary<string, JNode>> LookupHashesAsync(IEnumerable<string> sha1s, CancellationToken ct)
        {
            var result = new Dictionary<string, JNode>(StringComparer.OrdinalIgnoreCase);
            var all = sha1s.Where(h => !string.IsNullOrEmpty(h)).Select(h => h.ToLowerInvariant()).Distinct().ToList();
            for (int i = 0; i < all.Count; i += 500)
            {
                var body = JNode.Obj().Set("hashes", ContentJson.StrArr(all.Skip(i).Take(500))).Set("algorithm", "sha1");
                var json = await PostAsync("/version_files", body, ct).ConfigureAwait(false);
                foreach (var kv in json.Props) result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>sha1 → 符合筛选条件的最新 version（JSON）</summary>
        public static async Task<Dictionary<string, JNode>> LookupUpdatesAsync(IEnumerable<string> sha1s, IList<string> loaders, IList<string> gameVersions, CancellationToken ct)
        {
            var result = new Dictionary<string, JNode>(StringComparer.OrdinalIgnoreCase);
            var all = sha1s.Where(h => !string.IsNullOrEmpty(h)).Select(h => h.ToLowerInvariant()).Distinct().ToList();
            for (int i = 0; i < all.Count; i += 500)
            {
                var body = JNode.Obj().Set("hashes", ContentJson.StrArr(all.Skip(i).Take(500))).Set("algorithm", "sha1");
                if (loaders != null && loaders.Count > 0) body.Set("loaders", ContentJson.StrArr(loaders));
                if (gameVersions != null && gameVersions.Count > 0) body.Set("game_versions", ContentJson.StrArr(gameVersions));
                var json = await PostAsync("/version_files/update", body, ct).ConfigureAwait(false);
                foreach (var kv in json.Props) result[kv.Key] = kv.Value;
            }
            return result;
        }

        // ---------------- 工具 ----------------

        static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        internal static List<string> SortVersionsDesc(List<string> versions)
        {
            var list = versions.Distinct().ToList();
            list.Sort((a, b) => -ContentService.CompareGameVersion(a, b));
            return list;
        }
    }
}
