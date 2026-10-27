using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    public enum LoaderType { None, Forge, NeoForge, Fabric, Quilt, OptiFine, LiteLoader }

    public class LoaderInfo
    {
        public LoaderType Type { get; set; }
        public string Version { get; set; }
        public override string ToString() => Version == null ? Name(Type) : $"{Name(Type)} {Version}";

        public static string Name(LoaderType t) => t switch
        {
            LoaderType.Forge => "Forge",
            LoaderType.NeoForge => "NeoForge",
            LoaderType.Fabric => "Fabric",
            LoaderType.Quilt => "Quilt",
            LoaderType.OptiFine => "OptiFine",
            LoaderType.LiteLoader => "LiteLoader",
            _ => "原版"
        };

        /// <summary>Modrinth / CurseForge 用的小写加载器名</summary>
        public static string Slug(LoaderType t) => t switch
        {
            LoaderType.Forge => "forge",
            LoaderType.NeoForge => "neoforge",
            LoaderType.Fabric => "fabric",
            LoaderType.Quilt => "quilt",
            LoaderType.LiteLoader => "liteloader",
            _ => null
        };
    }

    /// <summary>
    /// 读取并合并后的版本 JSON（处理 inheritsFrom 继承链）。
    /// </summary>
    public class VersionInfo
    {
        public string Id { get; private set; }
        public JNode Json { get; private set; }
        /// <summary>继承链：[自己, 父, 祖父…]</summary>
        public List<string> Chain { get; } = new List<string>();

        public string MainClass => Json["mainClass"].Str;
        public string Type => Json["type"].StrOr("release");
        public string AssetsId => Json["assetIndex"]["id"].Str ?? Json["assets"].Str ?? "legacy";
        public bool IsLegacyArguments => Json["arguments"].IsNull && Json["minecraftArguments"].Str != null;
        public DateTime ReleaseTime => DateTime.TryParse(Json["releaseTime"].Str, out var d) ? d : DateTime.MinValue;

        /// <summary>需要的 Java 主版本</summary>
        public int JavaMajor
        {
            get
            {
                var j = Json["javaVersion"]["majorVersion"];
                if (!j.IsNull) return j.Int;
                return GuessJavaMajor(McVersion, ReleaseTime);
            }
        }
        public string JavaComponent => Json["javaVersion"]["component"].Str;

        public static int GuessJavaMajor(string mc, DateTime release)
        {
            // 老 JSON 没写 javaVersion 的一律是 Java 8 时代
            if (release != DateTime.MinValue && release < new DateTime(2021, 5, 1)) return 8;
            if (mc == null) return 8;
            if (Util.CompareVersion(mc, "1.17") < 0) return 8;
            if (Util.CompareVersion(mc, "1.18") < 0) return 16;
            if (Util.CompareVersion(mc, "1.20.5") < 0) return 17;
            if (Util.CompareVersion(mc, "26") < 0) return 21;
            return 25;
        }

        string _mc;
        /// <summary>原版版本号（1.20.1 / 26.3），尽力推断</summary>
        public string McVersion => _mc ?? (_mc = DetectMcVersion());

        List<LoaderInfo> _loaders;
        public List<LoaderInfo> Loaders => _loaders ?? (_loaders = DetectLoaders());
        public LoaderType PrimaryLoader => Loaders.Select(l => l.Type).FirstOrDefault(t => t != LoaderType.OptiFine && t != LoaderType.LiteLoader) is var t && t != LoaderType.None
            ? t : Loaders.Count > 0 ? Loaders[0].Type : LoaderType.None;
        public bool IsModded => Loaders.Any(l => l.Type != LoaderType.OptiFine);

        public static VersionInfo Load(GameRepo repo, string id)
        {
            var v = new VersionInfo { Id = id };
            JNode merged = null;
            var cur = id;
            for (int depth = 0; cur != null && depth < 8; depth++)
            {
                var file = repo.VersionJson(cur);
                if (!File.Exists(file))
                {
                    if (depth == 0) throw new FileNotFoundException($"找不到版本 {cur} 的 JSON");
                    throw new FileNotFoundException($"版本 {id} 依赖的原版 {cur} 不存在，请重新安装或补全文件");
                }
                var json = JNode.ParseFile(file);
                v.Chain.Add(cur);
                merged = merged == null ? json : Merge(merged, json);
                cur = json["inheritsFrom"].Str;
                if (v.Chain.Contains(cur)) break;
            }
            merged.Remove("inheritsFrom");
            merged.Set("id", id);
            v.Json = merged;
            return v;
        }

        /// <summary>直接从一个 JSON 构造（安装过程中用）</summary>
        public static VersionInfo FromJson(string id, JNode json)
        {
            var v = new VersionInfo { Id = id, Json = json };
            v.Chain.Add(id);
            return v;
        }

        /// <summary>child 覆盖 parent；libraries 与 arguments 拼接</summary>
        public static JNode Merge(JNode child, JNode parent)
        {
            var r = parent.Clone();
            foreach (var kv in child.Props)
            {
                switch (kv.Key)
                {
                    case "libraries":
                        {
                            var libs = JNode.Arr();
                            var seen = new HashSet<string>();
                            foreach (var l in kv.Value)
                            {
                                libs.Add(l.Clone());
                                var m = Maven.Parse(l["name"].Str);
                                if (m != null) seen.Add(m.Key + "|" + l["natives"].IsNull + "|" + RulesKey(l));
                            }
                            foreach (var l in parent["libraries"])
                            {
                                var m = Maven.Parse(l["name"].Str);
                                if (m != null && seen.Contains(m.Key + "|" + l["natives"].IsNull + "|" + RulesKey(l))) continue;
                                libs.Add(l.Clone());
                            }
                            r.Set("libraries", libs);
                            break;
                        }
                    case "arguments":
                        {
                            var args = r["arguments"].IsObj ? r["arguments"] : JNode.Obj();
                            foreach (var a in kv.Value.Props)
                            {
                                var list = args[a.Key].IsArr ? args[a.Key] : JNode.Arr();
                                foreach (var x in a.Value) list.Add(x.Clone());
                                args.Set(a.Key, list);
                            }
                            r.Set("arguments", args);
                            break;
                        }
                    default:
                        r.Set(kv.Key, kv.Value.Clone());
                        break;
                }
            }
            return r;
        }

        static string RulesKey(JNode lib) => lib["rules"].IsNull ? "" : lib["rules"].ToJson(false);

        string DetectMcVersion()
        {
            // 1. 我们自己写入的标记 / HMCL 与 PCL 常用字段
            var s = Json["openlauncher"]["mc"].Str ?? Json["clientVersion"].Str;
            if (!string.IsNullOrEmpty(s)) return s;
            // 2. 继承链最底层就是原版
            if (Chain.Count > 1) return Chain[Chain.Count - 1];
            // 3. 从 Forge / Fabric 等库推断
            foreach (var l in Json["libraries"])
            {
                var m = Maven.Parse(l["name"].Str);
                if (m == null) continue;
                if (m.Group == "net.fabricmc" && m.Artifact == "intermediary") return m.Version;
                if (m.Group == "org.quiltmc" && m.Artifact == "hashed") return m.Version;
                if ((m.Group == "net.minecraftforge" && (m.Artifact == "forge" || m.Artifact == "fmlloader")) && m.Version.Contains("-"))
                    return m.Version.Split('-')[0];
            }
            var mcArgVer = Json["patches"][0]["version"].Str;
            if (!string.IsNullOrEmpty(mcArgVer)) return mcArgVer;
            // 4. 从版本名里找（官方启动器的原版文件夹名就是版本号）
            var match = Regex.Match(Id, @"(\d+\.\d+(\.\d+)?(-(pre|rc|snapshot)-?\d+)?|\d\dw\d\d[a-z])");
            return match.Success ? match.Groups[1].Value : AssetsId;
        }

        List<LoaderInfo> DetectLoaders()
        {
            var list = new List<LoaderInfo>();
            void add(LoaderType t, string ver) { if (!list.Any(x => x.Type == t)) list.Add(new LoaderInfo { Type = t, Version = ver }); }
            foreach (var l in Json["libraries"])
            {
                var m = Maven.Parse(l["name"].Str);
                if (m == null) continue;
                switch (m.Group)
                {
                    case "net.neoforged":
                        if (m.Artifact == "neoforge" || m.Artifact == "forge") add(LoaderType.NeoForge, m.Version.Contains("-") && m.Artifact == "forge" ? m.Version.Split('-').Last() : m.Version);
                        break;
                    case "net.neoforged.fancymodloader":
                        if (m.Artifact == "loader") add(LoaderType.NeoForge, null);
                        break;
                    case "net.minecraftforge":
                        if (m.Artifact == "forge" || m.Artifact == "fmlloader" || m.Artifact == "minecraftforge")
                        {
                            var ver = m.Version;
                            var parts = ver.Split('-');
                            if (parts.Length >= 2) ver = parts[1];
                            add(LoaderType.Forge, ver);
                        }
                        break;
                    case "net.fabricmc":
                        if (m.Artifact == "fabric-loader") add(LoaderType.Fabric, m.Version);
                        break;
                    case "org.quiltmc":
                        if (m.Artifact == "quilt-loader") add(LoaderType.Quilt, m.Version);
                        break;
                    case "optifine":
                        if (m.Artifact == "OptiFine" || m.Artifact == "optifine") add(LoaderType.OptiFine, m.Version.Contains("_") ? m.Version.Substring(m.Version.IndexOf('_') + 1) : m.Version);
                        break;
                    case "com.mumfrey":
                        if (m.Artifact == "liteloader") add(LoaderType.LiteLoader, m.Version);
                        break;
                }
            }
            // NeoForge 的 fmlloader 也叫 net.neoforged.fancymodloader，避免误判成 Forge
            if (list.Any(x => x.Type == LoaderType.NeoForge)) list.RemoveAll(x => x.Type == LoaderType.Forge);
            // 我们安装时写入的显式记录优先
            foreach (var n in Json["openlauncher"]["loaders"])
            {
                if (Enum.TryParse<LoaderType>(n["type"].Str, out var t))
                {
                    list.RemoveAll(x => x.Type == t);
                    list.Add(new LoaderInfo { Type = t, Version = n["version"].Str });
                }
            }
            if (Json["mainClass"].Str == "cpw.mods.modlauncher.Launcher" && !list.Any(x => x.Type == LoaderType.Forge || x.Type == LoaderType.NeoForge))
                list.Add(new LoaderInfo { Type = LoaderType.Forge });
            return list;
        }
    }
}
