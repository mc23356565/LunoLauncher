using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    /// <summary>版本 JSON 里 rules 字段的判定（os / arch / version / versionRange / features）</summary>
    public static class Rules
    {
        public static string OsName => "windows";
        static readonly Lazy<Version> OsVer = new Lazy<Version>(() => Util.OsVersion);

        public static bool Allowed(JNode rules, IDictionary<string, bool> features = null)
        {
            if (rules == null || rules.IsNull || rules.Count == 0) return true;
            bool allow = false;
            foreach (var r in rules)
                if (Matches(r, features)) allow = r["action"].Str != "disallow";
            return allow;
        }

        static bool Matches(JNode rule, IDictionary<string, bool> features)
        {
            var os = rule["os"];
            if (!os.IsNull)
            {
                var name = os["name"].Str;
                if (name != null && name != OsName) return false;

                var arch = os["arch"].Str;
                if (arch != null)
                {
                    var mine = Util.OsArch;
                    // 官方 JSON 里 arch 只出现 x86（=32 位）
                    if (arch == "x86" && mine != "x86") return false;
                    if (arch == "x64" && mine != "x64") return false;
                    if (arch == "arm64" && mine != "arm64") return false;
                }

                var ver = os["version"].Str;
                if (ver != null)
                {
                    var v = OsVer.Value;
                    try { if (!Regex.IsMatch($"{v.Major}.{v.Minor}", ver)) return false; } catch { return false; }
                }

                var range = os["versionRange"];
                if (!range.IsNull)
                {
                    var v = OsVer.Value;
                    var mine = $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
                    var min = range["min"].Str;
                    var max = range["max"].Str;
                    if (min != null && Util.CompareVersion(mine, min) < 0) return false;
                    if (max != null && Util.CompareVersion(mine, max) >= 0) return false;
                }
            }

            var feats = rule["features"];
            if (!feats.IsNull)
            {
                foreach (var kv in feats.Props)
                {
                    bool have = features != null && features.TryGetValue(kv.Key, out var f) && f;
                    if (have != kv.Value.Bool) return false;
                }
            }
            return true;
        }
    }
}
