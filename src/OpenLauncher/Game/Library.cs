using System;
using System.IO;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    /// <summary>Maven 坐标 group:artifact:version[:classifier][@ext]</summary>
    public class Maven
    {
        public string Group, Artifact, Version, Classifier, Ext = "jar";

        public static Maven Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var m = new Maven();
            var at = name.IndexOf('@');
            if (at >= 0) { m.Ext = name.Substring(at + 1); name = name.Substring(0, at); }
            var p = name.Split(':');
            if (p.Length < 3) return null;
            m.Group = p[0]; m.Artifact = p[1]; m.Version = p[2];
            if (p.Length > 3) m.Classifier = p[3];
            return m;
        }

        /// <summary>相对路径，用 / 分隔（拼 URL 用）</summary>
        public string RelPath => $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{FileName}";
        public string FileName => $"{Artifact}-{Version}{(string.IsNullOrEmpty(Classifier) ? "" : "-" + Classifier)}.{Ext}";
        /// <summary>同一个库的不同版本视为同一个（合并继承版本时去重用）</summary>
        public string Key => $"{Group}:{Artifact}:{Classifier}";
        public string LocalPath(string librariesDir) => Path.Combine(librariesDir, RelPath.Replace('/', '\\'));
    }

    /// <summary>对 libraries 里一项的统一封装</summary>
    public class Library
    {
        public JNode Json { get; }
        public string Name => Json["name"].Str;
        public Maven Maven { get; }

        public Library(JNode json)
        {
            Json = json;
            Maven = Maven.Parse(Name);
        }

        public bool Allowed => Rules.Allowed(Json["rules"]);

        /// <summary>旧格式的原生库：natives 字段 + classifiers</summary>
        public bool IsLegacyNative => !Json["natives"].IsNull;

        /// <summary>1.19+ 格式：原生库作为独立条目，classifier 形如 natives-windows</summary>
        public bool IsNativeClassifier => Maven?.Classifier != null && Maven.Classifier.StartsWith("natives-", StringComparison.OrdinalIgnoreCase);

        /// <summary>需要解压的原生库 classifier（旧格式）；arch 为 Java 的架构，空则用系统架构</summary>
        public string NativeClassifierFor(string arch = null)
        {
            var c = Json["natives"]["windows"].Str;
            if (c == null) return null;
            return c.Replace("${arch}", (arch ?? Util.OsArch) == "x86" ? "32" : "64");
        }

        public string NativeClassifier => NativeClassifierFor();

        /// <summary>主构件（放进 classpath 的那个 jar）</summary>
        public DownloadItem Artifact(string librariesDir)
        {
            if (Maven == null) return null;
            var art = Json["downloads"]["artifact"];
            if (!art.IsNull)
            {
                var path = art["path"].Str != null ? Path.Combine(librariesDir, art["path"].Str.Replace('/', '\\')) : Maven.LocalPath(librariesDir);
                var url = art["url"].Str;
                return new DownloadItem(string.IsNullOrEmpty(url) ? null : url, path, art["sha1"].Str, art["size"].Long);
            }
            // 旧格式原生库没有主构件
            if (IsLegacyNative && Json["downloads"].IsNull == false) return null;
            if (IsLegacyNative && Json["url"].IsNull) return null;

            var baseUrl = Json["url"].Str;
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://libraries.minecraft.net/";
            if (baseUrl.StartsWith("http://")) baseUrl = "https://" + baseUrl.Substring(7);
            if (!baseUrl.EndsWith("/")) baseUrl += "/";
            return new DownloadItem(baseUrl + Maven.RelPath, Maven.LocalPath(librariesDir), Json["sha1"].Str ?? Json["checksums"][0].Str, Json["size"].Long);
        }

        /// <summary>旧格式原生库对应的下载项</summary>
        public DownloadItem NativeArtifact(string librariesDir, string arch = null)
        {
            var cls = NativeClassifierFor(arch);
            if (cls == null) return null;
            var c = Json["downloads"]["classifiers"][cls];
            if (!c.IsNull)
            {
                var path = c["path"].Str != null ? Path.Combine(librariesDir, c["path"].Str.Replace('/', '\\'))
                                                 : new Maven { Group = Maven.Group, Artifact = Maven.Artifact, Version = Maven.Version, Classifier = cls }.LocalPath(librariesDir);
                return new DownloadItem(c["url"].Str, path, c["sha1"].Str, c["size"].Long);
            }
            var m = new Maven { Group = Maven.Group, Artifact = Maven.Artifact, Version = Maven.Version, Classifier = cls };
            var baseUrl = Json["url"].Str ?? "https://libraries.minecraft.net/";
            if (!baseUrl.EndsWith("/")) baseUrl += "/";
            return new DownloadItem(baseUrl + m.RelPath, m.LocalPath(librariesDir));
        }
    }
}
