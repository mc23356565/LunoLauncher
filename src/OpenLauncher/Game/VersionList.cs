using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    public class McVersionEntry
    {
        public string Id { get; set; }
        /// <summary>release / snapshot / old_beta / old_alpha</summary>
        public string Type { get; set; }
        public string Url { get; set; }
        public string Sha1 { get; set; }
        public DateTime ReleaseTime { get; set; }

        public bool IsAprilFools { get; set; }
        public string TypeText => IsAprilFools ? "愚人节版" : Type switch
        {
            "release" => "正式版",
            "snapshot" => "快照版",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            _ => Type
        };
        public string DateText => ReleaseTime == DateTime.MinValue ? "" : ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd");
        public override string ToString() => Id;
    }

    public class LoaderVersionEntry
    {
        public LoaderType Loader { get; set; }
        public string Version { get; set; }
        /// <summary>Forge 的完整 maven 版本（1.20.1-47.2.0）</summary>
        public string FullVersion { get; set; }
        public bool Stable { get; set; } = true;
        public bool Recommended { get; set; }
        public string Tag => Recommended ? "推荐" : Stable ? "" : "测试版";
        public override string ToString() => Version;
    }

    /// <summary>
    /// 所有版本列表都直接从上游官方元数据实时获取（必要时走镜像），启动器不内置任何版本号，
    /// 所以就算启动器本身很久不更新，也能装到最新的 Minecraft 和加载器。
    /// </summary>
    public static class VersionList
    {
        public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        static List<McVersionEntry> _cache;
        static DateTime _cacheTime;

        static string CacheFile => Path.Combine(Paths.Cache, "version_manifest_v2.json");

        public static async Task<List<McVersionEntry>> GetMinecraftAsync(bool force = false, CancellationToken ct = default)
        {
            if (!force && _cache != null && (DateTime.Now - _cacheTime).TotalMinutes < 10) return _cache;
            JNode json;
            try
            {
                json = await Http.GetJsonAsync(ManifestUrl, ct).ConfigureAwait(false);
                try { json.SaveFile(CacheFile, false); } catch { }
            }
            catch (Exception ex) when (File.Exists(CacheFile))
            {
                Log.Warn("获取版本列表失败，使用缓存：" + ex.Message);
                json = JNode.ParseFile(CacheFile);
            }
            var list = new List<McVersionEntry>();
            foreach (var v in json["versions"])
            {
                var e = new McVersionEntry
                {
                    Id = v["id"].Str,
                    Type = v["type"].Str,
                    Url = v["url"].Str,
                    Sha1 = v["sha1"].Str,
                    ReleaseTime = DateTime.TryParse(v["releaseTime"].Str, out var d) ? d : DateTime.MinValue,
                };
                // 愚人节版本：4 月 1 日发布的快照
                e.IsAprilFools = e.Type == "snapshot" && e.ReleaseTime.Month == 4 && e.ReleaseTime.Day <= 2 && !Regex.IsMatch(e.Id, @"^\d\dw\d\d[a-z]$|pre|rc|snapshot");
                list.Add(e);
            }
            _cache = list;
            _cacheTime = DateTime.Now;
            return list;
        }

        public static async Task<McVersionEntry> FindAsync(string id, CancellationToken ct = default)
        {
            var list = await GetMinecraftAsync(false, ct).ConfigureAwait(false);
            var e = list.FirstOrDefault(v => v.Id == id);
            if (e == null)
            {
                list = await GetMinecraftAsync(true, ct).ConfigureAwait(false);
                e = list.FirstOrDefault(v => v.Id == id);
            }
            return e ?? throw new Exception($"官方版本列表里没有 {id}");
        }

        // ---------- 加载器 ----------
        public static async Task<List<LoaderVersionEntry>> GetLoaderVersionsAsync(LoaderType loader, string mc, CancellationToken ct = default)
        {
            switch (loader)
            {
                case LoaderType.Fabric: return await FabricAsync(mc, ct).ConfigureAwait(false);
                case LoaderType.Quilt: return await QuiltAsync(mc, ct).ConfigureAwait(false);
                case LoaderType.Forge: return await ForgeAsync(mc, ct).ConfigureAwait(false);
                case LoaderType.NeoForge: return await NeoForgeAsync(mc, ct).ConfigureAwait(false);
                default: return new List<LoaderVersionEntry>();
            }
        }

        static async Task<List<LoaderVersionEntry>> FabricAsync(string mc, CancellationToken ct)
        {
            var json = await Http.GetJsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mc)}", ct).ConfigureAwait(false);
            var list = json.Select(n => new LoaderVersionEntry
            {
                Loader = LoaderType.Fabric,
                Version = n["loader"]["version"].Str,
                Stable = n["loader"]["stable"].BoolOr(true),
            }).Where(e => e.Version != null).ToList();
            var rec = list.FirstOrDefault(e => e.Stable);
            if (rec != null) rec.Recommended = true;
            return list;
        }

        static async Task<List<LoaderVersionEntry>> QuiltAsync(string mc, CancellationToken ct)
        {
            var json = await Http.GetJsonAsync($"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mc)}", ct).ConfigureAwait(false);
            var list = json.Select(n => new LoaderVersionEntry
            {
                Loader = LoaderType.Quilt,
                Version = n["loader"]["version"].Str,
                Stable = !(n["loader"]["version"].Str ?? "").Contains("beta"),
            }).Where(e => e.Version != null).ToList();
            var rec = list.FirstOrDefault(e => e.Stable);
            if (rec != null) rec.Recommended = true;
            return list;
        }

        static async Task<List<LoaderVersionEntry>> ForgeAsync(string mc, CancellationToken ct)
        {
            var xml = await Http.GetStringAsync("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct).ConfigureAwait(false);
            var prefix = mc + "-";
            var list = Regex.Matches(xml, @"<version>([^<]+)</version>").Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .Select(v =>
                {
                    var rest = v.Substring(prefix.Length);
                    // 1.7.10-10.13.4.1614-1.7.10 这种带后缀的
                    var ver = rest.Split('-')[0];
                    return new LoaderVersionEntry { Loader = LoaderType.Forge, Version = ver, FullVersion = v };
                })
                .OrderByDescending(e => e.Version, Comparer<string>.Create(Util.CompareVersion))
                .ToList();
            try
            {
                var promos = await Http.GetJsonAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct, timeoutSec: 10).ConfigureAwait(false);
                var rec = promos["promos"][mc + "-recommended"].Str ?? promos["promos"][mc + "-latest"].Str;
                var hit = list.FirstOrDefault(e => e.Version == rec);
                if (hit != null) hit.Recommended = true;
            }
            catch (Exception ex) { Log.Warn("Forge 推荐版本获取失败：" + ex.Message); }
            return list;
        }

        static async Task<List<LoaderVersionEntry>> NeoForgeAsync(string mc, CancellationToken ct)
        {
            var list = new List<LoaderVersionEntry>();
            if (mc == "1.20.1")
            {
                // 1.20.1 时期 NeoForge 还叫 net.neoforged:forge，版本形如 1.20.1-47.1.106
                var old = await Http.GetJsonAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/forge", ct).ConfigureAwait(false);
                foreach (var v in old["versions"].StrList().Where(v => v.StartsWith("1.20.1-")))
                    list.Add(new LoaderVersionEntry { Loader = LoaderType.NeoForge, Version = v.Substring(7), FullVersion = v, Stable = !v.Contains("beta") });
            }
            else
            {
                var json = await Http.GetJsonAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", ct).ConfigureAwait(false);
                foreach (var v in json["versions"].StrList())
                {
                    if (NeoForgeToMc(v) == mc)
                        list.Add(new LoaderVersionEntry { Loader = LoaderType.NeoForge, Version = v, FullVersion = v, Stable = !v.Contains("beta") && !v.Contains("alpha") });
                }
            }
            list = list.OrderByDescending(e => e.Version, Comparer<string>.Create(Util.CompareVersion)).ToList();
            var rec = list.FirstOrDefault(e => e.Stable);
            if (rec != null) rec.Recommended = true;
            return list;
        }

        /// <summary>
        /// NeoForge 版本号 → MC 版本号：
        /// 1.x 时代 "21.1.77" → "1.21.1"，"21.0.5" → "1.21"；
        /// 26.x 起 "26.3.0.95" → "26.3"，"26.1.2.10" → "26.1.2"
        /// </summary>
        public static string NeoForgeToMc(string neo)
        {
            var core = neo.Split('-')[0];
            var p = core.Split('.');
            if (p.Length < 3 || !int.TryParse(p[0], out var major)) return null;
            if (major >= 26)
            {
                if (p.Length < 4) return $"{p[0]}.{p[1]}";
                return p[2] == "0" ? $"{p[0]}.{p[1]}" : $"{p[0]}.{p[1]}.{p[2]}";
            }
            return p[1] == "0" ? $"1.{p[0]}" : $"1.{p[0]}.{p[1]}";
        }
    }
}
