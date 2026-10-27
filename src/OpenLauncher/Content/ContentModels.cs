using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Content
{
    public enum ContentType { Mod, Modpack, ResourcePack, Shader, DataPack }

    public enum ContentSource { Modrinth, CurseForge }

    /// <summary>社区资源（Mod / 整合包 / 资源包 / 光影 / 数据包）的一个项目</summary>
    public class ContentProject
    {
        public ContentSource Source { get; set; }
        public string Id { get; set; }
        public string Slug { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public string IconUrl { get; set; }
        public string WebUrl { get; set; }
        public long Downloads { get; set; }
        public DateTime Updated { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        /// <summary>小写加载器名：fabric / forge / neoforge / quilt（光影可能是 iris / optifine）</summary>
        public List<string> Loaders { get; set; } = new List<string>();
        public List<string> GameVersions { get; set; } = new List<string>();
        public ContentType Type { get; set; }

        public string SourceText => ContentService.SourceName(Source);
        public string DownloadsText => Util.FormatCount(Downloads);
        public string UpdatedText => Updated == DateTime.MinValue ? "" : Updated.ToString("yyyy-MM-dd");
        public string LoadersText => string.Join(" / ", Loaders.ConvertAll(ContentService.LoaderDisplayName));

        public override string ToString() => $"{Title} ({SourceText}:{Id})";
    }

    public class ContentDependency
    {
        public string ProjectId { get; set; }
        /// <summary>作者钉死的具体文件（Modrinth 的 version_id）；为空时按游戏版本 + 加载器挑最新的</summary>
        public string FileId { get; set; }
        public bool Required { get; set; }
        /// <summary>Modrinth 偶尔只给文件名（外部依赖）</summary>
        public string FileName { get; set; }
    }

    /// <summary>项目的一个可下载文件（Modrinth 的 version / CurseForge 的 file）</summary>
    public class ContentFile
    {
        public ContentSource Source { get; set; }
        public string ProjectId { get; set; }
        public string FileId { get; set; }
        public string DisplayName { get; set; }
        public string VersionNumber { get; set; }
        public string FileName { get; set; }
        public string Url { get; set; }
        public List<string> AltUrls { get; set; } = new List<string>();
        public string Sha1 { get; set; }
        public string Sha512 { get; set; }
        public long Size { get; set; }
        public List<string> GameVersions { get; set; } = new List<string>();
        public List<string> Loaders { get; set; } = new List<string>();
        /// <summary>release / beta / alpha</summary>
        public string ReleaseType { get; set; } = "release";
        public DateTime Published { get; set; }
        public List<ContentDependency> Dependencies { get; set; } = new List<ContentDependency>();

        public string ReleaseTypeText => ReleaseType switch
        {
            "beta" => "测试版",
            "alpha" => "早期版",
            _ => "正式版"
        };
        public string SizeText => Size > 0 ? Util.FormatSize(Size) : "";
        public string PublishedText => Published == DateTime.MinValue ? "" : Published.ToString("yyyy-MM-dd");
        public string GameVersionsText => ContentService.CompactVersions(GameVersions);
        public string LoadersText => string.Join(" / ", Loaders.ConvertAll(ContentService.LoaderDisplayName));
        public bool IsRelease => ReleaseType == "release";

        public override string ToString() => $"{DisplayName} [{FileName}]";
    }

    public class SearchQuery
    {
        public string Text { get; set; }
        public ContentType Type { get; set; } = ContentType.Mod;
        public string GameVersion { get; set; }
        /// <summary>"fabric" / "forge" / "neoforge" / "quilt" / null（不筛选）</summary>
        public string Loader { get; set; }
        /// <summary>relevance / downloads / updated / newest</summary>
        public string Sort { get; set; } = "relevance";
        public int Offset { get; set; }
        public int Limit { get; set; } = 20;

        public SearchQuery Clone() => (SearchQuery)MemberwiseClone();
    }

    public class SearchResult
    {
        public List<ContentProject> Items { get; set; } = new List<ContentProject>();
        public int Total { get; set; }
        /// <summary>中文翻译后实际发给服务器的关键词（没翻译时与输入相同）</summary>
        public string EffectiveText { get; set; }
    }

    public interface IContentProvider
    {
        ContentSource Source { get; }
        Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct);
        Task<ContentProject> GetProjectAsync(string id, CancellationToken ct);
        /// <summary>按游戏版本 / 加载器筛选（参数为空表示不筛选），新的在前</summary>
        Task<List<ContentFile>> GetFilesAsync(string projectId, string gameVersion, string loader, CancellationToken ct);
        Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct);
    }

    /// <summary>资源站请求失败时抛出，Message 是可以直接给用户看的中文说明</summary>
    public class ContentException : Exception
    {
        public int Status { get; }
        public ContentException(string message, Exception inner = null, int status = 0) : base(message, inner) { Status = status; }
        public bool IsNotFound => Status == 404;
    }

    static class ContentJson
    {
        public static DateTime Date(JNode n)
        {
            var s = n.Str;
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.LocalDateTime : DateTime.MinValue;
        }

        /// <summary>在 UI 线程上执行并等它完成（没有 UI 时直接执行），让调用方 await 之后看到的就是新状态</summary>
        public static Task OnUiAsync(Action a)
        {
            var d = TaskManager.Dispatcher;
            if (d == null || d.CheckAccess()) { a(); return Task.CompletedTask; }
            return d.InvokeAsync(a).Task;
        }

        public static JNode StrArr(IEnumerable<string> items)
        {
            var a = JNode.Arr();
            foreach (var s in items) a.Add(s);
            return a;
        }
    }
}
