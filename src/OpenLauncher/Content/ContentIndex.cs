using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLauncher.Core;

namespace OpenLauncher.Content
{
    /// <summary>
    /// 记录"这个文件夹里哪个文件来自哪个项目"的小索引（.openlauncher-index.json），
    /// 用于装前置时判断是否已安装、换版本时清掉旧文件。游戏只加载 jar/zip，不会理会这个文件。
    /// </summary>
    public class ContentIndex
    {
        public const string FileName = ".openlauncher-index.json";
        static readonly object Lock = new object();

        public string Dir { get; }
        readonly JNode _files;
        readonly HashSet<string> _removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ContentIndex(string dir, JNode files) { Dir = dir; _files = files; }

        public static ContentIndex Load(string dir)
        {
            JNode files = null;
            try
            {
                var p = Path.Combine(dir, FileName);
                lock (Lock)
                    if (File.Exists(p)) files = JNode.ParseFile(p)["files"];
            }
            catch (Exception ex) { Log.Warn("资源索引损坏，已忽略：" + ex.Message); }
            return new ContentIndex(dir, files != null && files.IsObj ? files : JNode.Obj());
        }

        /// <summary>保存。会先合并磁盘上别的安装任务新写入的条目，避免并发安装互相覆盖。</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var p = Path.Combine(Dir, FileName);
                lock (Lock)
                {
                    try
                    {
                        if (File.Exists(p))
                            foreach (var kv in JNode.ParseFile(p)["files"].Props)
                                if (!_files.Has(kv.Key) && !_removed.Contains(kv.Key)) _files.Set(kv.Key, kv.Value);
                    }
                    catch { /* 旧文件坏了就直接覆盖 */ }
                    JNode.Obj().Set("version", 1).Set("files", _files).SaveFile(p);
                }
            }
            catch (Exception ex) { Log.Warn("保存资源索引失败：" + ex.Message); }
        }

        public static string Key(ContentSource s, string projectId) => (s == ContentSource.CurseForge ? "curseforge:" : "modrinth:") + projectId;

        public void Set(string fileName, ContentFile f, string title = null)
        {
            var o = JNode.Obj()
                .Set("source", f.Source == ContentSource.CurseForge ? "curseforge" : "modrinth")
                .Set("project", f.ProjectId)
                .Set("file", f.FileId)
                .Set("sha1", f.Sha1);
            if (title != null) o.Set("title", title);
            _files.Set(fileName, o);
            _removed.Remove(fileName);
        }

        public void Remove(string fileName)
        {
            fileName = StripDisabled(fileName);
            _files.Remove(fileName);
            _removed.Add(fileName);
        }

        /// <summary>某个文件记录的项目，格式为 Key(...)；没有记录返回 null</summary>
        public string ProjectOf(string fileName)
        {
            var n = _files[StripDisabled(fileName)];
            if (n.IsNull || n["project"].Str == null) return null;
            return (n["source"].Str == "curseforge" ? "curseforge:" : "modrinth:") + n["project"].Str;
        }

        /// <summary>文件夹里实际存在的、属于该项目的文件（含 .disabled）</summary>
        public List<string> ExistingFilesOf(ContentSource s, string projectId)
        {
            var key = Key(s, projectId);
            var list = new List<string>();
            foreach (var kv in _files.Props)
            {
                if (ProjectOf(kv.Key) != key) continue;
                var p = Path.Combine(Dir, kv.Key);
                if (File.Exists(p)) list.Add(p);
                else if (File.Exists(p + ".disabled")) list.Add(p + ".disabled");
            }
            return list;
        }

        /// <summary>索引里记录的、文件还在的项目</summary>
        public HashSet<string> PresentProjects()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _files.Props)
            {
                var p = Path.Combine(Dir, kv.Key);
                if ((File.Exists(p) || File.Exists(p + ".disabled")) && ProjectOf(kv.Key) is string k) set.Add(k);
            }
            return set;
        }

        static string StripDisabled(string name)
            => name != null && name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 9) : name;
    }

    /// <summary>路径穿越防护：外部数据（整合包、API 返回的文件名）给出的路径必须落在指定目录内</summary>
    public static class PathGuard
    {
        /// <summary>把相对路径解析到 baseDir 内；越界、绝对路径、非法字符一律返回 null</summary>
        public static string Resolve(string baseDir, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            try
            {
                var rel = relative.Replace('/', '\\').TrimStart('\\');
                if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Contains(":")) return null;
                foreach (var part in rel.Split('\\'))
                    if (part == ".." || part.Trim().Length == 0 && part.Length > 0) return null;
                var root = Path.GetFullPath(baseDir).TrimEnd('\\') + "\\";
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length <= root.Length) return null;
                return full;
            }
            catch { return null; }
        }

        /// <summary>只取文件名部分并去掉非法字符，再放进 dir</summary>
        /// <summary>取路径最后一段并替换掉非法字符（不会因为奇怪的字符抛异常）</summary>
        public static string SafeName(string fileName)
        {
            var raw = (fileName ?? "").Replace('/', '\\');
            var i = raw.LastIndexOf('\\');
            return Util.SafeFileName(i >= 0 ? raw.Substring(i + 1) : raw);
        }

        /// <summary>明显的越界企图：..、绝对路径、盘符 / 备用数据流（冒号）</summary>
        public static bool IsEscaping(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return false;
            var r = relative.Replace('\\', '/');
            if (r.StartsWith("/") || r.Contains(":")) return true;
            foreach (var part in r.Split('/'))
                if (part.Contains("..") && part.Trim().Trim('.').Length == 0) return true; // ".."、"... "、".. " 等
            return false;
        }

        public static string FileIn(string dir, string fileName)
        {
            var name = SafeName(fileName);
            if (name.Length == 0 || name == "." || name == "..") throw new InvalidDataException("文件名无效：" + fileName);
            return Resolve(dir, name) ?? throw new InvalidDataException("文件名无效：" + fileName);
        }
    }
}
