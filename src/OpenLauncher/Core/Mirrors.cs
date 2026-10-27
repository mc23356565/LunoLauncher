using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace OpenLauncher.Core
{
    public class MirrorRule
    {
        /// <summary>game：游戏本体 / 加载器 / Java；mod：社区资源（Modrinth / CurseForge）</summary>
        public string Group { get; set; }
        public string Name { get; set; }
        public string From { get; set; }
        public string To { get; set; }

        public MirrorRule() { }
        public MirrorRule(string group, string name, string from, string to) { Group = group; Name = name; From = from; To = to; }

        public JNode ToJson() => JNode.Obj().Set("group", Group).Set("name", Name).Set("from", From).Set("to", To);
        public static MirrorRule FromJson(JNode n) => new MirrorRule(n["group"].StrOr("game"), n["name"].StrOr("自定义"), n["from"].Str, n["to"].Str);
    }

    /// <summary>
    /// 去中心化下载的核心：每个官方地址都可以映射到任意多个镜像，下载时按设置的顺序逐个尝试，
    /// 失败的主机会被自动降级。镜像表本身可以由用户编辑，也可以随签名更新清单下发。
    /// </summary>
    public static class Mirrors
    {
        public static readonly MirrorRule[] Builtin =
        {
            // BMCLAPI（bangbang93 维护的公益镜像）
            new MirrorRule("game", "BMCLAPI", "https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
            new MirrorRule("game", "BMCLAPI", "https://launchermeta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
            new MirrorRule("game", "BMCLAPI", "https://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/"),
            new MirrorRule("game", "BMCLAPI", "https://launcher.mojang.com/", "https://bmclapi2.bangbang93.com/"),
            new MirrorRule("game", "BMCLAPI", "https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/"),
            new MirrorRule("game", "BMCLAPI", "https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/"),
            new MirrorRule("game", "BMCLAPI", "https://maven.minecraftforge.net/", "https://bmclapi2.bangbang93.com/maven/"),
            new MirrorRule("game", "BMCLAPI", "https://files.minecraftforge.net/maven/", "https://bmclapi2.bangbang93.com/maven/"),
            new MirrorRule("game", "BMCLAPI", "https://maven.neoforged.net/releases/", "https://bmclapi2.bangbang93.com/maven/"),
            new MirrorRule("game", "BMCLAPI", "https://meta.fabricmc.net/", "https://bmclapi2.bangbang93.com/fabric-meta/"),
            new MirrorRule("game", "BMCLAPI", "https://maven.fabricmc.net/", "https://bmclapi2.bangbang93.com/maven/"),
            new MirrorRule("game", "BMCLAPI", "https://maven.quiltmc.org/repository/release/", "https://bmclapi2.bangbang93.com/maven/"),
            new MirrorRule("game", "BMCLAPI", "https://authlib-injector.yushi.moe/", "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/"),
            // MCIM（Modrinth / CurseForge 的公益镜像，无需 API Key）
            new MirrorRule("mod", "MCIM", "https://api.modrinth.com/", "https://mod.mcimirror.top/modrinth/"),
            new MirrorRule("mod", "MCIM", "https://cdn.modrinth.com/", "https://mod.mcimirror.top/"),
            new MirrorRule("mod", "MCIM", "https://api.curseforge.com/", "https://mod.mcimirror.top/curseforge/"),
            new MirrorRule("mod", "MCIM", "https://edge.forgecdn.net/", "https://mod.mcimirror.top/"),
            new MirrorRule("mod", "MCIM", "https://mediafilez.forgecdn.net/", "https://mod.mcimirror.top/"),
        };

        static readonly ConcurrentDictionary<string, int> Failures = new ConcurrentDictionary<string, int>();
        static List<MirrorRule> _rules;

        /// <summary>当前生效的镜像表：用户自定义 + 远程下发 + 内置（同名同 From 去重，前者优先）</summary>
        public static List<MirrorRule> Rules
        {
            get
            {
                if (_rules != null) return _rules;
                var list = new List<MirrorRule>();
                foreach (var n in Settings.GetNode("mirror.custom")) list.Add(MirrorRule.FromJson(n));
                foreach (var n in Settings.GetNode("mirror.remote")) list.Add(MirrorRule.FromJson(n));
                list.AddRange(Builtin);
                var disabled = new HashSet<string>(Settings.ExtraList("mirror.disabled"), StringComparer.OrdinalIgnoreCase);
                _rules = list.Where(r => !string.IsNullOrEmpty(r.From) && !string.IsNullOrEmpty(r.To) && !disabled.Contains(r.Name))
                             .GroupBy(r => r.Name + "|" + r.From).Select(g => g.First()).ToList();
                return _rules;
            }
        }

        public static void Reload() => _rules = null;

        public static IEnumerable<string> MirrorNames => Rules.Select(r => r.Name).Distinct();

        /// <summary>把一个官方地址展开成按优先级排好的候选地址列表</summary>
        public static List<string> Expand(string url)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(url)) return result;
            var matches = Rules.Where(r => url.StartsWith(r.From, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) { result.Add(url); return result; }

            var mode = matches[0].Group == "mod" ? Settings.ModSource : Settings.GameSource;
            var mirrors = matches.Select(r => r.To + url.Substring(r.From.Length)).Distinct().ToList();
            switch (mode)
            {
                case SourceMode.OfficialOnly: result.Add(url); break;
                case SourceMode.OfficialFirst: result.Add(url); result.AddRange(mirrors); break;
                default: result.AddRange(mirrors); result.Add(url); break;
            }
            // 连续失败过多的主机挪到最后（稳定排序，不打乱同级顺序）
            return result.Select((u, i) => (u, i)).OrderBy(t => Failures.TryGetValue(Host(t.u), out var f) && f >= 3 ? 1 : 0).ThenBy(t => t.i).Select(t => t.u).ToList();
        }

        /// <summary>启动时探测各镜像是否可达，不可达的先排到官方源后面（之后成功一次就恢复）</summary>
        public static async System.Threading.Tasks.Task ProbeAsync()
        {
            var hosts = Rules.Select(r => { try { return new Uri(r.To); } catch { return null; } })
                             .Where(u => u != null).GroupBy(u => u.Host.ToLowerInvariant()).Select(g => g.First()).ToList();
            await System.Threading.Tasks.Task.WhenAll(hosts.Select(async uri =>
            {
                var host = uri.Host.ToLowerInvariant();
                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6)))
                    using (var resp = await Http.Client.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, uri.GetLeftPart(UriPartial.Authority) + "/"),
                               System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        if ((int)resp.StatusCode >= 500) throw new Exception("HTTP " + (int)resp.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    Failures[host] = 3;
                    Log.Info($"镜像 {host} 暂时不可用（{ex.GetBaseException().Message}），本次先优先用其他来源");
                }
            })).ConfigureAwait(false);
        }

        public static void ReportFailure(string url)
        {
            var h = Host(url);
            Failures.AddOrUpdate(h, 1, (_, v) => v + 1);
        }

        public static void ReportSuccess(string url)
        {
            Failures.TryRemove(Host(url), out _);
        }

        public static string Host(string url)
        {
            try { return new Uri(url).Host.ToLowerInvariant(); } catch { return url; }
        }
    }
}
