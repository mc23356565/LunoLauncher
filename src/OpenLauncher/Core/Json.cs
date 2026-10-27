using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenLauncher.Core
{
    public enum JType { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// 零依赖的 JSON 节点。缺失的键返回 JNode.Null 而不是 null，方便链式读取：
    /// json["downloads"]["client"]["url"].Str
    /// </summary>
    public sealed class JNode : IEnumerable<JNode>
    {
        public static readonly JNode Null = new JNode(JType.Null);

        public JType Type { get; }
        readonly string _scalar;            // 数字的原文 / 字符串值
        readonly bool _bool;
        readonly List<JNode> _arr;
        readonly Dictionary<string, JNode> _obj;
        readonly List<string> _keys;

        JNode(JType t)
        {
            Type = t;
            if (t == JType.Array) _arr = new List<JNode>();
            if (t == JType.Object) { _obj = new Dictionary<string, JNode>(); _keys = new List<string>(); }
        }
        JNode(JType t, string s) : this(t) { _scalar = s; }
        JNode(bool b) : this(JType.Bool) { _bool = b; }

        public static JNode Obj() => new JNode(JType.Object);
        public static JNode Arr() => new JNode(JType.Array);
        public static JNode Arr(IEnumerable<JNode> items) { var a = Arr(); foreach (var i in items) a.Add(i); return a; }
        public static JNode From(string s) => s == null ? new JNode(JType.Null) : new JNode(JType.String, s);
        public static JNode From(long n) => new JNode(JType.Number, n.ToString(CultureInfo.InvariantCulture));
        public static JNode From(double n) => new JNode(JType.Number, n.ToString("R", CultureInfo.InvariantCulture));
        public static JNode From(bool b) => new JNode(b);

        public static implicit operator JNode(string s) => From(s);
        public static implicit operator JNode(long n) => From(n);
        public static implicit operator JNode(int n) => From((long)n);
        public static implicit operator JNode(double n) => From(n);
        public static implicit operator JNode(bool b) => From(b);

        public bool IsNull => Type == JType.Null;
        public bool IsObj => Type == JType.Object;
        public bool IsArr => Type == JType.Array;
        public bool IsStr => Type == JType.String;

        public JNode this[string key]
        {
            get => Type == JType.Object && key != null && _obj.TryGetValue(key, out var v) ? v : Null;
            set => Set(key, value);
        }

        public JNode this[int i]
        {
            get => Type == JType.Array && i >= 0 && i < _arr.Count ? _arr[i] : Null;
            set { if (Type == JType.Array) _arr[i] = value ?? From((string)null); }
        }

        public JNode Set(string key, JNode value)
        {
            if (Type != JType.Object) throw new InvalidOperationException("不是 JSON 对象");
            value = value ?? From((string)null);
            if (!_obj.ContainsKey(key)) _keys.Add(key);
            _obj[key] = value;
            return this;
        }

        public bool Remove(string key)
        {
            if (Type != JType.Object || !_obj.Remove(key)) return false;
            _keys.Remove(key);
            return true;
        }

        public JNode Add(JNode v)
        {
            if (Type != JType.Array) throw new InvalidOperationException("不是 JSON 数组");
            _arr.Add(v ?? From((string)null));
            return this;
        }

        public void Insert(int index, JNode v) => _arr.Insert(index, v ?? From((string)null));
        public void RemoveAt(int index) => _arr.RemoveAt(index);

        public bool Has(string key) => Type == JType.Object && _obj.ContainsKey(key);
        public int Count => Type == JType.Array ? _arr.Count : Type == JType.Object ? _obj.Count : 0;
        public IEnumerable<string> Keys => Type == JType.Object ? (IEnumerable<string>)_keys : new string[0];

        public IEnumerable<KeyValuePair<string, JNode>> Props
        {
            get
            {
                if (Type != JType.Object) yield break;
                foreach (var k in _keys.ToArray()) yield return new KeyValuePair<string, JNode>(k, _obj[k]);
            }
        }

        public IEnumerator<JNode> GetEnumerator()
        {
            if (Type == JType.Array) return _arr.GetEnumerator();
            if (Type == JType.Object) return ((IEnumerable<JNode>)_keys.ConvertAll(k => _obj[k])).GetEnumerator();
            return ((IEnumerable<JNode>)new JNode[0]).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ---------- 取值 ----------
        public string Str => Type switch
        {
            JType.String => _scalar,
            JType.Number => _scalar,
            JType.Bool => _bool ? "true" : "false",
            _ => null
        };
        public string StrOr(string def) => Str ?? def;

        public double Double
        {
            get
            {
                if (Type == JType.Number || Type == JType.String)
                    return double.TryParse(_scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                if (Type == JType.Bool) return _bool ? 1 : 0;
                return 0;
            }
        }
        public long Long
        {
            get
            {
                if ((Type == JType.Number || Type == JType.String) && long.TryParse(_scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                return (long)Double;
            }
        }
        public int Int => (int)Long;
        public bool Bool => Type == JType.Bool ? _bool : Type == JType.String ? _scalar == "true" : Type == JType.Number && Double != 0;
        public bool BoolOr(bool def) => Type == JType.Null ? def : Bool;
        public long LongOr(long def) => Type == JType.Null ? def : Long;
        public int IntOr(int def) => Type == JType.Null ? def : Int;

        public List<string> StrList()
        {
            var l = new List<string>();
            if (Type == JType.Array)
            {
                foreach (var n in _arr) if (n.Str != null) l.Add(n.Str);
            }
            else if (Type == JType.String) l.Add(_scalar);
            return l;
        }

        public JNode Clone()
        {
            switch (Type)
            {
                case JType.Array: { var a = Arr(); foreach (var n in _arr) a.Add(n.Clone()); return a; }
                case JType.Object: { var o = Obj(); foreach (var k in _keys) o.Set(k, _obj[k].Clone()); return o; }
                default: return this.Type == JType.Null ? From((string)null) : this; // 标量不可变，可共享
            }
        }

        // ---------- 序列化 ----------
        public override string ToString() => ToJson(false);

        public string ToJson(bool indent = true)
        {
            var sb = new StringBuilder();
            Write(sb, indent, 0);
            return sb.ToString();
        }

        void Write(StringBuilder sb, bool indent, int depth)
        {
            switch (Type)
            {
                case JType.Null: sb.Append("null"); break;
                case JType.Bool: sb.Append(_bool ? "true" : "false"); break;
                case JType.Number: sb.Append(_scalar); break;
                case JType.String: WriteString(sb, _scalar); break;
                case JType.Array:
                    if (_arr.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('[');
                    for (int i = 0; i < _arr.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (indent) { sb.Append('\n'); sb.Append(' ', (depth + 1) * 2); }
                        _arr[i].Write(sb, indent, depth + 1);
                    }
                    if (indent) { sb.Append('\n'); sb.Append(' ', depth * 2); }
                    sb.Append(']');
                    break;
                case JType.Object:
                    if (_keys.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    for (int i = 0; i < _keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (indent) { sb.Append('\n'); sb.Append(' ', (depth + 1) * 2); }
                        WriteString(sb, _keys[i]);
                        sb.Append(indent ? ": " : ":");
                        _obj[_keys[i]].Write(sb, indent, depth + 1);
                    }
                    if (indent) { sb.Append('\n'); sb.Append(' ', depth * 2); }
                    sb.Append('}');
                    break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- 解析 ----------
        public static JNode Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipWs();
            var v = p.ReadValue();
            p.SkipWs();
            if (!p.End) throw p.Error("JSON 末尾有多余内容");
            return v;
        }

        public static bool TryParse(string text, out JNode node)
        {
            try { node = Parse(text); return true; }
            catch { node = Null; return false; }
        }

        public static JNode ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

        public void SaveFile(string path, bool indent = true)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(indent), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        sealed class Parser
        {
            readonly string s;
            int i;
            public Parser(string text)
            {
                s = text;
                if (s.Length > 0 && s[0] == '﻿') i = 1;
            }
            public bool End => i >= s.Length;
            public FormatException Error(string msg) => new FormatException($"{msg}（位置 {i}）");

            public void SkipWs()
            {
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }
                    // 容忍 // 与 /* */ 注释（部分整合包手写的 json 会带）
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                    {
                        int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = e < 0 ? s.Length : e + 2; continue;
                    }
                    break;
                }
            }

            public JNode ReadValue()
            {
                if (End) throw Error("JSON 意外结束");
                char c = s[i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return From(ReadString());
                    case 't': Expect("true"); return From(true);
                    case 'f': Expect("false"); return From(false);
                    case 'n': Expect("null"); return From((string)null);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error($"无法识别的字符 '{c}'");
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Error($"应为 {word}");
                i += word.Length;
            }

            JNode ReadNumber()
            {
                int start = i;
                if (s[i] == '-') i++;
                while (i < s.Length)
                {
                    char c = s[i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') i++;
                    else break;
                }
                return new JNode(JType.Number, s.Substring(start, i - start));
            }

            string ReadString()
            {
                i++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw Error("字符串未闭合");
                    char c = s[i++];
                    if (c == '"') break;
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) throw Error("转义未完成");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw Error("\\u 转义不完整");
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                return sb.ToString();
            }

            JNode ReadArray()
            {
                i++;
                var a = Arr();
                SkipWs();
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    SkipWs();
                    a.Add(ReadValue());
                    SkipWs();
                    if (End) throw Error("数组未闭合");
                    if (s[i] == ',') { i++; SkipWs(); if (i < s.Length && s[i] == ']') { i++; return a; } continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw Error("数组中应为 , 或 ]");
                }
            }

            JNode ReadObject()
            {
                i++;
                var o = Obj();
                SkipWs();
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    SkipWs();
                    if (End || s[i] != '"') throw Error("对象键应为字符串");
                    var key = ReadString();
                    SkipWs();
                    if (End || s[i] != ':') throw Error("应为 :");
                    i++;
                    SkipWs();
                    o.Set(key, ReadValue());
                    SkipWs();
                    if (End) throw Error("对象未闭合");
                    if (s[i] == ',') { i++; SkipWs(); if (i < s.Length && s[i] == '}') { i++; return o; } continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw Error("对象中应为 , 或 }");
                }
            }
        }
    }
}
