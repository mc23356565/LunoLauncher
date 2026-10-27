using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LunoLauncher.Core;

namespace LunoLauncher.Content
{
    /// <summary>mods 文件夹里的一个 Mod 文件</summary>
    public class LocalMod : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void On([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        string _filePath;
        bool _enabled;
        ContentFile _update;
        ImageSource _icon;
        bool _iconTried;

        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; On(); On(nameof(FileName)); }
        }
        public string FileName => Path.GetFileName(_filePath);

        public string ModId { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Authors { get; set; }
        /// <summary>fabric / quilt / forge / neoforge / 空（无法识别）</summary>
        public string Loader { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        /// <summary>读取元数据失败时的原因（文件仍会列出）</summary>
        public string LoadError { get; set; }

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; On(); }
        }

        public byte[] IconBytes { get; set; }

        /// <summary>第一次访问时解码并冻结（可跨线程使用）；没有图标返回 null</summary>
        public ImageSource Icon
        {
            get
            {
                if (!_iconTried) { _iconTried = true; _icon = DecodeIcon(IconBytes); }
                return _icon;
            }
        }

        /// <summary>更新检查得到的新文件；null 表示没有更新</summary>
        public ContentFile Update
        {
            get => _update;
            set { _update = value; On(); On(nameof(HasUpdate)); }
        }
        public bool HasUpdate => _update != null;

        /// <summary>更新检查时顺便识别出的 Modrinth 项目 ID（不在 Modrinth 上则为 null）</summary>
        public string ProjectId { get; set; }

        string _sha1;
        DateTime _sha1Stamp;
        /// <summary>文件 SHA-1（按修改时间缓存）</summary>
        public string Sha1
        {
            get
            {
                try
                {
                    var t = File.GetLastWriteTimeUtc(_filePath);
                    if (_sha1 == null || t != _sha1Stamp) { _sha1 = Util.Sha1File(_filePath); _sha1Stamp = t; }
                }
                catch { _sha1 = null; }
                return _sha1;
            }
        }

        public string DisplayName => string.IsNullOrEmpty(Name) ? Path.GetFileNameWithoutExtension(FileName ?? "") : Name;
        public string SizeText => Util.FormatSize(Size);

        internal void RaiseAll() => On(string.Empty);

        internal void CopyMetaFrom(LocalMod o)
        {
            ModId = o.ModId; Name = o.Name; Version = o.Version; Description = o.Description; Authors = o.Authors;
            Loader = o.Loader; Size = o.Size; Modified = o.Modified; LoadError = o.LoadError; IconBytes = o.IconBytes;
            _iconTried = false; _icon = null;
        }

        static ImageSource DecodeIcon(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.DecodePixelWidth = 64;
                bi.StreamSource = new MemoryStream(bytes);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        public override string ToString() => $"{DisplayName} {Version} ({FileName})";
    }

    public static class LocalMods
    {
        const string Disabled = ".disabled";

        // ---------------- 扫描 ----------------

        public static List<LocalMod> Scan(string modsDir) => Scan(modsDir, null);

        /// <summary>
        /// 扫描 mods 文件夹。preferLoader 用于同时带多种元数据的"多加载器"jar（如 "fabric"），可为空。
        /// 坏文件不会抛异常，只是用文件名兜底。
        /// </summary>
        public static List<LocalMod> Scan(string modsDir, string preferLoader)
        {
            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return new List<LocalMod>();
            List<string> files;
            try
            {
                files = Directory.GetFiles(modsDir).Where(f =>
                {
                    var n = f.ToLowerInvariant();
                    if (n.EndsWith(Disabled)) n = n.Substring(0, n.Length - Disabled.Length);
                    return n.EndsWith(".jar") || n.EndsWith(".zip") || n.EndsWith(".litemod");
                }).ToList();
            }
            catch (Exception ex) { Log.Warn("读取 mods 文件夹失败：" + ex.Message); return new List<LocalMod>(); }

            var result = new LocalMod[files.Count];
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                i => result[i] = Read(files[i], preferLoader));
            return result.Where(m => m != null)
                         .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                         .ToList();
        }

        /// <summary>读取单个 Mod 文件的元数据，永不抛异常</summary>
        public static LocalMod Read(string file, string preferLoader = null)
        {
            var m = new LocalMod { FilePath = file, Enabled = !file.EndsWith(Disabled, StringComparison.OrdinalIgnoreCase) };
            try
            {
                var fi = new FileInfo(file);
                m.Size = fi.Length;
                m.Modified = fi.LastWriteTime;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    ReadMeta(zip, m, preferLoader);
            }
            catch (Exception ex)
            {
                m.LoadError = ex.Message;
            }
            if (string.IsNullOrWhiteSpace(m.Name))
            {
                var n = Path.GetFileName(file);
                if (n.EndsWith(Disabled, StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - Disabled.Length);
                m.Name = Path.GetFileNameWithoutExtension(n);
            }
            return m;
        }

        static void ReadMeta(ZipArchive zip, LocalMod m, string preferLoader)
        {
            var readers = new List<(string loader, Func<ZipArchive, LocalMod, bool> read)>
            {
                ("fabric", ReadFabric),
                ("quilt", ReadQuilt),
                ("neoforge", (z, x) => ReadModsToml(z, x, "META-INF/neoforge.mods.toml", "neoforge")),
                ("forge", (z, x) => ReadModsToml(z, x, "META-INF/mods.toml", "forge")),
                ("forge", ReadMcmodInfo),
                ("liteloader", ReadLiteMod),
            };
            if (!string.IsNullOrEmpty(preferLoader))
            {
                var pl = preferLoader.ToLowerInvariant();
                // Quilt 实例优先读 quilt.mod.json，其次 fabric.mod.json
                readers = readers.OrderBy(r => r.loader == pl ? 0 : pl == "quilt" && r.loader == "fabric" ? 1 : 2).ToList();
            }
            foreach (var (_, read) in readers)
            {
                try { if (read(zip, m)) return; }
                catch (Exception ex) { m.LoadError = ex.Message; }
            }
        }

        // ---- Fabric ----
        static bool ReadFabric(ZipArchive zip, LocalMod m)
        {
            var text = Util.ReadZipText(zip, "fabric.mod.json");
            if (text == null) return false;
            var j = ParseLenient(text);
            if (j == null || !j.IsObj) return false;
            m.Loader = "fabric";
            m.ModId = j["id"].Str;
            m.Name = j["name"].Str ?? m.ModId;
            m.Version = j["version"].Str;
            m.Description = j["description"].Str;
            m.Authors = JoinPeople(j["authors"]);
            m.IconBytes = ReadIcon(zip, j["icon"]);
            return true;
        }

        // ---- Quilt ----
        static bool ReadQuilt(ZipArchive zip, LocalMod m)
        {
            var text = Util.ReadZipText(zip, "quilt.mod.json");
            if (text == null) return false;
            var j = ParseLenient(text);
            if (j == null || !j.IsObj) return false;
            var ql = j["quilt_loader"];
            var meta = ql["metadata"];
            m.Loader = "quilt";
            m.ModId = ql["id"].Str;
            m.Name = meta["name"].Str ?? m.ModId;
            m.Version = ql["version"].Str;
            m.Description = meta["description"].Str;
            var people = meta["contributors"];
            m.Authors = people.IsObj ? string.Join(", ", people.Keys) : JoinPeople(people);
            m.IconBytes = ReadIcon(zip, meta["icon"]);
            return true;
        }

        // ---- Forge / NeoForge (mods.toml) ----
        static bool ReadModsToml(ZipArchive zip, LocalMod m, string entry, string loader)
        {
            var text = Util.ReadZipText(zip, entry);
            if (text == null) return false;
            var tables = MiniToml.Parse(text);
            var root = tables[0].Values;
            var mod = tables.FirstOrDefault(t => t.IsArray && t.Name == "mods")?.Values ?? new Dictionary<string, string>();
            string get(string k) => mod.TryGetValue(k, out var v) ? v : root.TryGetValue(k, out var r) ? r : null;

            m.Loader = loader;
            // NeoForge 1.20.2~1.20.4 还在用 mods.toml，看依赖里有没有 neoforge
            if (loader == "forge" && Regex.IsMatch(text, @"modId\s*=\s*[""']neoforge[""']")) m.Loader = "neoforge";
            m.ModId = get("modId");
            m.Name = get("displayName") ?? m.ModId;
            m.Version = get("version");
            m.Description = get("description")?.Trim();
            m.Authors = get("authors");
            if (m.Version == null || m.Version.Contains("${"))
            {
                var manifest = ReadManifest(zip);
                if (manifest.TryGetValue("Implementation-Version", out var iv) && !iv.Contains("${")) m.Version = iv;
                else if (m.Version != null && m.Version.Contains("${")) m.Version = null;
            }
            var logo = get("logoFile");
            if (!string.IsNullOrEmpty(logo)) m.IconBytes = ReadEntryBytes(zip, logo);
            return !string.IsNullOrEmpty(m.ModId) || !string.IsNullOrEmpty(m.Name);
        }

        // ---- 老 Forge (mcmod.info) ----
        static bool ReadMcmodInfo(ZipArchive zip, LocalMod m)
        {
            var text = Util.ReadZipText(zip, "mcmod.info");
            if (text == null) return false;
            var j = ParseLenient(text);
            if (j == null) return false;
            var info = j.IsArr ? j[0] : j["modList"][0];
            if (!info.IsObj) return false;
            m.Loader = "forge";
            m.ModId = info["modid"].Str;
            m.Name = info["name"].Str ?? m.ModId;
            m.Version = info["version"].Str;
            if (m.Version != null && m.Version.Contains("${")) m.Version = null;
            m.Description = info["description"].Str?.Trim();
            m.Authors = JoinPeople(info["authorList"].IsNull ? info["authors"] : info["authorList"]);
            var logo = info["logoFile"].Str;
            if (!string.IsNullOrEmpty(logo)) m.IconBytes = ReadEntryBytes(zip, logo);
            return true;
        }

        // ---- LiteLoader ----
        static bool ReadLiteMod(ZipArchive zip, LocalMod m)
        {
            var text = Util.ReadZipText(zip, "litemod.json");
            if (text == null) return false;
            var j = ParseLenient(text);
            if (j == null || !j.IsObj) return false;
            m.Loader = "liteloader";
            m.ModId = j["name"].Str;
            m.Name = j["displayName"].Str ?? j["name"].Str;
            m.Version = j["version"].Str;
            m.Description = j["description"].Str;
            m.Authors = j["author"].Str;
            return true;
        }

        static string JoinPeople(JNode n)
        {
            if (n.IsStr) return n.Str;
            var names = new List<string>();
            foreach (var p in n)
            {
                var s = p.IsObj ? p["name"].Str : p.Str;
                if (!string.IsNullOrWhiteSpace(s)) names.Add(s.Trim());
            }
            return names.Count == 0 ? null : string.Join(", ", names);
        }

        /// <summary>fabric/quilt 的 icon：字符串，或 { "16": "...", "128": "..." }</summary>
        static byte[] ReadIcon(ZipArchive zip, JNode icon)
        {
            string path = null;
            if (icon.IsStr) path = icon.Str;
            else if (icon.IsObj)
            {
                var best = icon.Props
                    .Select(kv => (size: int.TryParse(kv.Key, out var s) ? s : 0, path: kv.Value.Str))
                    .Where(x => x.path != null)
                    .OrderBy(x => x.size >= 64 ? 0 : 1).ThenBy(x => x.size >= 64 ? x.size : -x.size)
                    .FirstOrDefault();
                path = best.path;
            }
            return string.IsNullOrEmpty(path) ? null : ReadEntryBytes(zip, path);
        }

        static byte[] ReadEntryBytes(ZipArchive zip, string path)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            var e = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, path, StringComparison.OrdinalIgnoreCase));
            if (e == null || e.Length <= 0 || e.Length > 4 * 1024 * 1024) return null;
            using (var s = e.Open())
            using (var ms = new MemoryStream((int)e.Length))
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static Dictionary<string, string> ReadManifest(ZipArchive zip)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var text = Util.ReadZipText(zip, "META-INF/MANIFEST.MF");
            if (text == null) return dict;
            string lastKey = null;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.StartsWith(" ") && lastKey != null) { dict[lastKey] += raw.Substring(1); continue; }
                var i = raw.IndexOf(':');
                if (i <= 0) { if (raw.Length == 0 && dict.Count > 0) break; continue; } // 只读主段
                lastKey = raw.Substring(0, i).Trim();
                if (!dict.ContainsKey(lastKey)) dict[lastKey] = raw.Substring(i + 1).Trim();
            }
            return dict;
        }

        /// <summary>
        /// 宽松 JSON：不少 Mod 的元数据里有注释、尾逗号、字符串里直接换行，先修一遍再交给 JNode。
        /// </summary>
        internal static JNode ParseLenient(string text)
        {
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            if (JNode.TryParse(text, out var n)) return n;
            var sb = new StringBuilder(text.Length);
            bool inStr = false, esc = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inStr)
                {
                    if (esc) { esc = false; sb.Append(c); continue; }
                    if (c == '\\') { esc = true; sb.Append(c); continue; }
                    if (c == '"') { inStr = false; sb.Append(c); continue; }
                    if (c == '\n') { sb.Append("\\n"); continue; }
                    if (c == '\r') continue;
                    if (c == '\t') { sb.Append("\\t"); continue; }
                    if (c < ' ') continue;
                    sb.Append(c);
                    continue;
                }
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i++;
                    continue;
                }
                if (c == ',')
                {
                    int k = i + 1;
                    while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
                    if (k < text.Length && (text[k] == '}' || text[k] == ']')) continue;
                }
                sb.Append(c);
            }
            return JNode.TryParse(sb.ToString(), out n) ? n : null;
        }

        // ---------------- 启用 / 禁用 / 删除 ----------------

        /// <summary>改名 xxx.jar ↔ xxx.jar.disabled</summary>
        public static void SetEnabled(LocalMod m, bool enabled)
        {
            if (m == null || m.Enabled == enabled) return;
            var path = m.FilePath;
            string target;
            if (enabled)
            {
                if (!path.EndsWith(Disabled, StringComparison.OrdinalIgnoreCase)) { m.Enabled = true; return; }
                target = path.Substring(0, path.Length - Disabled.Length);
            }
            else target = path + Disabled;

            if (File.Exists(target)) throw new IOException($"已存在同名文件 {Path.GetFileName(target)}，请先处理重复的 Mod");
            File.Move(path, target);
            m.FilePath = target;
            m.Enabled = enabled;
        }

        /// <summary>移到回收站</summary>
        public static void Delete(LocalMod m)
        {
            if (m == null) return;
            RecycleBin.Delete(m.FilePath);
            try
            {
                var dir = Path.GetDirectoryName(m.FilePath);
                var name = m.FileName;
                if (name.EndsWith(Disabled, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - Disabled.Length);
                var index = ContentIndex.Load(dir);
                index.Remove(name);
                index.Save();
            }
            catch { }
        }

        // ---------------- 更新 ----------------

        /// <summary>
        /// 通过 Modrinth 哈希接口检查更新：找出每个 Mod 在当前游戏版本 + 加载器下的最新版本，
        /// 只有发布时间比当前文件更晚的才算更新。不在 Modrinth 上的 Mod 不会有结果。
        /// </summary>
        public static async Task CheckUpdatesAsync(IList<LocalMod> mods, string gameVersion, string loader, CancellationToken ct)
        {
            var list = mods?.Where(m => m != null).ToList() ?? new List<LocalMod>();
            if (list.Count == 0) return;

            var hashes = await Task.Run(() =>
            {
                var d = new Dictionary<LocalMod, string>();
                var arr = list.ToArray();
                var res = new string[arr.Length];
                Parallel.For(0, arr.Length, new ParallelOptions { CancellationToken = ct }, i => res[i] = arr[i].Sha1);
                for (int i = 0; i < arr.Length; i++) if (res[i] != null) d[arr[i]] = res[i];
                return d;
            }, ct).ConfigureAwait(false);
            if (hashes.Count == 0) return;

            var loaders = ModrinthProvider.LoaderFilter(loader);
            var gvs = string.IsNullOrWhiteSpace(gameVersion) ? null : new List<string> { gameVersion.Trim() };
            var current = await ModrinthProvider.LookupHashesAsync(hashes.Values, ct).ConfigureAwait(false);
            var latest = current.Count == 0
                ? new Dictionary<string, JNode>()
                : await ModrinthProvider.LookupUpdatesAsync(current.Keys, loaders, gvs, ct).ConfigureAwait(false);

            var results = new List<(LocalMod mod, ContentFile update, string project)>();
            foreach (var kv in hashes)
            {
                var m = kv.Key;
                var sha1 = kv.Value;
                ContentFile update = null;
                current.TryGetValue(sha1, out var cur);
                var project = cur?["project_id"].Str;
                if (cur != null && latest.TryGetValue(sha1, out var v))
                {
                    var nf = ModrinthProvider.ParseVersion(v);
                    bool newer = nf != null
                        && !string.Equals(nf.Sha1, sha1, StringComparison.OrdinalIgnoreCase)
                        && v["id"].Str != cur["id"].Str
                        && ContentJson.Date(v["date_published"]) > ContentJson.Date(cur["date_published"]);
                    if (newer) update = nf;
                }
                results.Add((m, update, project));
            }
            ct.ThrowIfCancellationRequested();
            await ContentJson.OnUiAsync(() =>
            {
                foreach (var (m, update, project) in results)
                {
                    m.ProjectId = project ?? m.ProjectId;
                    m.Update = update;
                }
            }).ConfigureAwait(false);
            Log.Info($"Mod 更新检查：{hashes.Count} 个文件，Modrinth 识别 {current.Count} 个，可更新 {results.Count(r => r.update != null)} 个");
        }

        /// <summary>下载新版本到同一文件夹，成功后把旧文件移到回收站；保持原来的启用 / 禁用状态</summary>
        public static async Task UpdateAsync(LocalMod m, LauncherTask task)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            var u = m.Update ?? throw new InvalidOperationException("这个 Mod 没有可用的更新");
            var ct = task?.Token ?? CancellationToken.None;
            var dir = Path.GetDirectoryName(m.FilePath);
            var old = m.FilePath;
            bool wasEnabled = m.Enabled;

            var target = PathGuard.FileIn(dir, u.FileName);
            var final = wasEnabled ? target : target + Disabled;
            await Downloader.RunAsync(new[]
            {
                new DownloadItem { Url = u.Url, AltUrls = u.AltUrls?.ToList(), Path = target, Sha1 = u.Sha1, Sha512 = u.Sha512, Size = u.Size }
            }, task, 0, 1, "更新 " + m.DisplayName).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (!string.Equals(final, target, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(final)) RecycleBin.Delete(final); // 同名的旧禁用文件
                File.Move(target, final);
            }
            if (!string.Equals(old, final, StringComparison.OrdinalIgnoreCase) && !string.Equals(old, target, StringComparison.OrdinalIgnoreCase) && File.Exists(old))
            {
                try { RecycleBin.Delete(old); }
                catch (Exception ex) { Log.Warn($"删除旧版本失败 {old}：{ex.Message}"); }
            }

            try
            {
                var index = ContentIndex.Load(dir);
                var oldName = Path.GetFileName(old);
                if (oldName.EndsWith(Disabled, StringComparison.OrdinalIgnoreCase)) oldName = oldName.Substring(0, oldName.Length - Disabled.Length);
                index.Remove(oldName);
                index.Set(Path.GetFileName(target), u);
                index.Save();
            }
            catch { }

            var fresh = Read(final, m.Loader);
            await ContentJson.OnUiAsync(() =>
            {
                m.CopyMetaFrom(fresh);
                m.FilePath = final;
                m.Enabled = wasEnabled;
                m.Update = null;
                m.RaiseAll();
            }).ConfigureAwait(false);
            Log.Info($"Mod 已更新：{Path.GetFileName(old)} → {Path.GetFileName(final)}");
        }
    }

    /// <summary>
    /// 只够读 mods.toml 的极简 TOML 解析：支持注释、[表]、[[表数组]]、基本/字面/多行字符串、字符串数组。
    /// 其余值按原文保存。
    /// </summary>
    internal static class MiniToml
    {
        public class Table
        {
            public string Name;
            public bool IsArray;
            public Dictionary<string, string> Values = new Dictionary<string, string>();
        }

        public static List<Table> Parse(string text)
        {
            var tables = new List<Table> { new Table { Name = "" } };
            var cur = tables[0];
            int i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '﻿' || char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '#') { SkipLine(text, ref i); continue; }
                if (c == '[')
                {
                    bool arr = i + 1 < n && text[i + 1] == '[';
                    int start = i + (arr ? 2 : 1);
                    int end = text.IndexOf(']', start);
                    if (end < 0) break;
                    cur = new Table { Name = text.Substring(start, end - start).Trim().Trim('"'), IsArray = arr };
                    tables.Add(cur);
                    i = end + 1;
                    SkipLine(text, ref i);
                    continue;
                }
                // key = value
                int eq = -1;
                for (int k = i; k < n && text[k] != '\n'; k++) if (text[k] == '=') { eq = k; break; }
                if (eq < 0) { SkipLine(text, ref i); continue; }
                var key = text.Substring(i, eq - i).Trim().Trim('"', '\'');
                i = eq + 1;
                while (i < n && (text[i] == ' ' || text[i] == '\t')) i++;
                var value = ReadValue(text, ref i);
                if (key.Length > 0 && !cur.Values.ContainsKey(key)) cur.Values[key] = value;
                SkipLine(text, ref i);
            }
            return tables;
        }

        static void SkipLine(string s, ref int i)
        {
            while (i < s.Length && s[i] != '\n') i++;
            if (i < s.Length) i++;
        }

        static string ReadValue(string s, ref int i)
        {
            int n = s.Length;
            if (i >= n) return "";
            if (Starts(s, i, "\"\"\"")) return ReadMulti(s, ref i, "\"\"\"", true);
            if (Starts(s, i, "'''")) return ReadMulti(s, ref i, "'''", false);
            char c = s[i];
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < n && s[i] != '"' && s[i] != '\n')
                {
                    if (s[i] == '\\' && i + 1 < n) { i++; sb.Append(Unescape(s, ref i)); continue; }
                    sb.Append(s[i++]);
                }
                if (i < n && s[i] == '"') i++;
                return sb.ToString();
            }
            if (c == '\'')
            {
                int end = s.IndexOf('\'', i + 1);
                int nl = s.IndexOf('\n', i + 1);
                if (end < 0 || (nl >= 0 && nl < end)) end = nl < 0 ? n : nl;
                var v = s.Substring(i + 1, end - i - 1);
                i = Math.Min(n, end + 1);
                return v;
            }
            if (c == '[')
            {
                i++;
                var items = new List<string>();
                while (i < n)
                {
                    char d = s[i];
                    if (char.IsWhiteSpace(d) || d == ',') { i++; continue; }
                    if (d == '#') { while (i < n && s[i] != '\n') i++; continue; }
                    if (d == ']') { i++; break; }
                    if (d == '{') { SkipBraces(s, ref i); continue; }
                    int before = i;
                    var item = ReadValue(s, ref i);
                    if (i == before) i++;
                    if (!string.IsNullOrEmpty(item)) items.Add(item);
                }
                return string.Join(", ", items);
            }
            if (c == '{')
            {
                int start = i;
                SkipBraces(s, ref i);
                return s.Substring(start, i - start);
            }
            {
                int start = i;
                while (i < n && s[i] != '\n' && s[i] != '#' && s[i] != ',' && s[i] != ']') i++;
                return s.Substring(start, i - start).Trim();
            }
        }

        static string ReadMulti(string s, ref int i, string quote, bool escapes)
        {
            i += 3;
            if (i < s.Length && s[i] == '\r') i++;
            if (i < s.Length && s[i] == '\n') i++; // 开头紧跟的换行不算内容
            var sb = new StringBuilder();
            while (i < s.Length && !Starts(s, i, quote))
            {
                if (escapes && s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    if (s[i] == '\n' || s[i] == '\r') { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; continue; } // 行尾反斜杠
                    sb.Append(Unescape(s, ref i));
                    continue;
                }
                sb.Append(s[i++]);
            }
            i = Math.Min(s.Length, i + 3);
            return sb.ToString().Replace("\r\n", "\n");
        }

        static string Unescape(string s, ref int i)
        {
            char e = s[i++];
            switch (e)
            {
                case 'n': return "\n";
                case 't': return "\t";
                case 'r': return "\r";
                case 'b': return "\b";
                case 'f': return "\f";
                case '"': return "\"";
                case '\\': return "\\";
                case 'u':
                case 'U':
                    {
                        int len = e == 'u' ? 4 : 8;
                        if (i + len <= s.Length && int.TryParse(s.Substring(i, len), System.Globalization.NumberStyles.HexNumber, null, out var cp))
                        {
                            i += len;
                            try { return char.ConvertFromUtf32(cp); } catch { return "?"; }
                        }
                        return "\\" + e;
                    }
                default: return "\\" + e;
            }
        }

        static void SkipBraces(string s, ref int i)
        {
            int depth = 0;
            bool inStr = false;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { i++; return; } }
            }
        }

        static bool Starts(string s, int i, string p) => string.CompareOrdinal(s, i, p, 0, p.Length) == 0;
    }
}
