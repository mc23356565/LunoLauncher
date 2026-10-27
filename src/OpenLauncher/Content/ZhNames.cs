using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenLauncher.Core;

namespace OpenLauncher.Content
{
    /// <summary>
    /// 中文俗称 → 英文搜索词。Modrinth / CurseForge 都不认中文，这里先翻译再搜。
    /// 词表来源（后者覆盖前者）：内置 zh-names.json → 数据目录下的 zh-names.json（更新下发）→ 设置项 content.zhNames（用户自定义）。
    /// </summary>
    public static class ZhNames
    {
        static Dictionary<string, string> _map;
        static List<KeyValuePair<string, string>> _cjkKeys;
        static readonly object Lock = new object();

        /// <summary>数据目录里可被更新覆盖的词表</summary>
        public static string OverrideFile => Path.Combine(Paths.DataDir, "zh-names.json");

        public static IReadOnlyDictionary<string, string> Entries { get { Ensure(); return _map; } }

        public static void Reload() { lock (Lock) { _map = null; _cjkKeys = null; } }

        static void Ensure()
        {
            if (_map != null) return;
            lock (Lock)
            {
                if (_map != null) return;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (var s = typeof(ZhNames).Assembly.GetManifestResourceStream("zh-names.json"))
                        if (s != null)
                            using (var r = new StreamReader(s, Encoding.UTF8))
                                Merge(map, JNode.Parse(r.ReadToEnd()));
                }
                catch (Exception ex) { Log.Warn("读取内置中文词表失败：" + ex.Message); }
                try { if (File.Exists(OverrideFile)) Merge(map, JNode.ParseFile(OverrideFile)); }
                catch (Exception ex) { Log.Warn("读取中文词表失败：" + ex.Message); }
                try { Merge(map, JNode.Obj().Set("entries", Settings.GetNode("content.zhNames"))); }
                catch { }

                _cjkKeys = map.Where(kv => ContainsCjk(kv.Key)).OrderByDescending(kv => kv.Key.Length).ToList();
                _map = map;
            }
        }

        static void Merge(Dictionary<string, string> map, JNode json)
        {
            foreach (var kv in json["entries"].Props)
            {
                var k = kv.Key?.Trim();
                var v = kv.Value.Str?.Trim();
                if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(v)) continue;
                map[k] = v;
            }
        }

        /// <summary>
        /// 翻译搜索词：整句命中直接替换；否则把句子里出现的中文俗称逐个替换（长词优先），
        /// 只要替换过，剩下的中文字就丢掉（例如"钠 模组" → "sodium"）。一个都没命中则原样返回。
        /// </summary>
        public static string Translate(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return query;
            Ensure();
            var q = query.Trim();
            if (_map.TryGetValue(q, out var whole)) return whole;
            if (!ContainsCjk(q)) return q;

            var sb = new StringBuilder(q);
            bool hit = false;
            foreach (var kv in _cjkKeys)
            {
                var cur = sb.ToString();
                if (cur.IndexOf(kv.Key, StringComparison.Ordinal) < 0) continue;
                // 用不含中文的占位分隔，防止替换结果里的词再被后续中文键命中
                sb.Replace(kv.Key, " " + kv.Value + " ");
                hit = true;
            }
            if (!hit) return q;

            var text = sb.ToString();
            var cleaned = new StringBuilder(text.Length);
            foreach (var c in text) cleaned.Append(IsCjk(c) ? ' ' : c);
            var result = Regex.Replace(cleaned.ToString(), @"\s+", " ").Trim();
            return result.Length == 0 ? q : result;
        }

        public static bool ContainsCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s) if (IsCjk(c)) return true;
            return false;
        }

        static bool IsCjk(char c) =>
            (c >= '一' && c <= '鿿') ||   // 中日韩统一表意文字
            (c >= '㐀' && c <= '䶿') ||   // 扩展 A
            (c >= '豈' && c <= '﫿') ||   // 兼容表意文字
            (c >= '぀' && c <= 'ヿ') ||   // 假名
            (c >= '　' && c <= '〿') ||   // 中文标点
            (c >= '＀' && c <= '￯') ||   // 全角字符
            char.IsSurrogate(c);                  // 扩展 B 以后（代理对）
    }
}
