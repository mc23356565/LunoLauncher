using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenLauncher.Core
{
    public class HttpStatusException : Exception
    {
        public int Status { get; }
        public string Body { get; }
        public string Url { get; }
        public HttpStatusException(string url, int status, string body)
            : base($"HTTP {status}：{Shorten(url)}") { Url = url; Status = status; Body = body; }
        static string Shorten(string u) => u.Length > 120 ? u.Substring(0, 120) + "…" : u;
    }

    public static class Http
    {
        public static readonly HttpClient Client;
        public static string UserAgent => $"{Branding.AppName}/{AppInfo.VersionString} (+{Branding.RepoUrl})";

        static Http()
        {
            // .NET Framework 4.8 默认跟随系统 TLS；老系统兜底打开 1.2
            if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 256;
            ServicePointManager.Expect100Continue = false;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                UseCookies = false,
            };
            Client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        static HttpRequestMessage Build(HttpMethod method, string url, IDictionary<string, string> headers, HttpContent content = null)
        {
            var req = new HttpRequestMessage(method, url) { Content = content };
            if (headers != null)
                foreach (var kv in headers)
                {
                    if (!req.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                        req.Content?.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            return req;
        }

        static async Task<string> SendOnce(HttpRequestMessage req, CancellationToken ct, int timeoutSec)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                try
                {
                    using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        var text = Encoding.UTF8.GetString(bytes);
                        if (!resp.IsSuccessStatusCode) throw new HttpStatusException(req.RequestUri.ToString(), (int)resp.StatusCode, text);
                        return text;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"请求超时（{timeoutSec} 秒）：{req.RequestUri.Host}");
                }
            }
        }

        /// <summary>多个来源错峰竞速：前一个 2.5 秒没回应就同时请求下一个，谁先成功用谁</summary>
        const int StaggerMs = 2500;

        /// <summary>GET 文本。mirror=true 时按镜像表错峰竞速，自动避开连不上的源。</summary>
        public static Task<string> GetStringAsync(string url, CancellationToken ct = default, IDictionary<string, string> headers = null, bool mirror = true, int timeoutSec = 20)
            => GetStringAsync(url, ct, headers, mirror, timeoutSec, null);

        /// <param name="validate">内容校验：不通过视为这个来源失败（镜像出错时常返回 200 + 网页）</param>
        public static async Task<string> GetStringAsync(string url, CancellationToken ct, IDictionary<string, string> headers, bool mirror, int timeoutSec, Func<string, bool> validate)
        {
            var candidates = mirror ? Mirrors.Expand(url) : new List<string> { url };
            using (var race = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var pending = new Dictionary<Task<string>, string>();
                int next = 0;
                Exception last = null;
                void launch()
                {
                    var u = candidates[next++];
                    var t = SendOnce(Build(HttpMethod.Get, u, headers), race.Token, timeoutSec);
                    t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    pending[t] = u;
                }
                launch();
                Task delay = null;
                while (pending.Count > 0)
                {
                    if (delay == null && next < candidates.Count) delay = Task.Delay(StaggerMs, race.Token);
                    var waitList = pending.Keys.Cast<Task>().ToList();
                    if (delay != null) waitList.Add(delay);
                    var done = await Task.WhenAny(waitList).ConfigureAwait(false);
                    if (done == delay)
                    {
                        delay = null;
                        if (next < candidates.Count) launch();
                        continue;
                    }
                    var task = (Task<string>)done;
                    var u = pending[task];
                    pending.Remove(task);
                    if (task.Status == TaskStatus.RanToCompletion && (validate == null || validate(task.Result)))
                    {
                        Mirrors.ReportSuccess(u);
                        race.Cancel();
                        return task.Result;
                    }
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        last = new FormatException("下载源返回的内容格式不对（可能是错误网页）");
                        Mirrors.ReportFailure(u);
                        Log.Warn($"请求 {u} 返回的内容格式不对，换下一个来源");
                        if (pending.Count == 0 && next < candidates.Count) { delay = null; launch(); }
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    var ex = task.Exception?.GetBaseException() ?? new TimeoutException("请求被取消");
                    last = ex;
                    if (!(ex is HttpStatusException hs && hs.Status == 404)) Mirrors.ReportFailure(u);
                    Log.Warn($"请求失败 {u}：{ex.Message}");
                    if (pending.Count == 0 && next < candidates.Count) { delay = null; launch(); }
                }
                throw last ?? new Exception("没有可用的下载地址");
            }
        }

        public static async Task<JNode> GetJsonAsync(string url, CancellationToken ct = default, IDictionary<string, string> headers = null, bool mirror = true, int timeoutSec = 20)
            => JNode.Parse(await GetStringAsync(url, ct, headers, mirror, timeoutSec, LooksLikeJson).ConfigureAwait(false));

        static bool LooksLikeJson(string s)
        {
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch) || ch == '\uFEFF') continue;
                return ch == '{' || ch == '[';
            }
            return false;
        }

        public static async Task<JNode> PostJsonAsync(string url, JNode body, IDictionary<string, string> headers = null, CancellationToken ct = default, bool mirror = false, int timeoutSec = 30)
        {
            var candidates = mirror ? Mirrors.Expand(url) : new List<string> { url };
            Exception last = null;
            foreach (var u in candidates)
            {
                try
                {
                    var content = new StringContent(body.ToJson(false), Encoding.UTF8, "application/json");
                    var req = Build(HttpMethod.Post, u, headers, content);
                    req.Headers.Accept.ParseAdd("application/json");
                    var s = await SendOnce(req, ct, timeoutSec).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(s) ? JNode.Obj() : JNode.Parse(s);
                }
                catch (HttpStatusException ex) when (ex.Status >= 400 && ex.Status < 500) { throw; }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested)) { last = ex; Mirrors.ReportFailure(u); }
            }
            throw last ?? new Exception("请求失败");
        }

        public static async Task<JNode> PostFormAsync(string url, IDictionary<string, string> form, CancellationToken ct = default, int timeoutSec = 30)
        {
            var content = new FormUrlEncodedContent(form);
            var s = await SendOnce(Build(HttpMethod.Post, url, null, content), ct, timeoutSec).ConfigureAwait(false);
            return JNode.Parse(s);
        }

        /// <summary>只取最终跳转后的地址（用于 CurseForge 公开下载链接）</summary>
        public static async Task<string> ResolveRedirectAsync(string url, CancellationToken ct = default)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using (var resp = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    return resp.RequestMessage.RequestUri.ToString();
            }
        }

        public static string UrlEncode(string s) => Uri.EscapeDataString(s ?? "");

        public static string Query(params (string k, string v)[] kv)
            => string.Join("&", kv.Where(p => p.v != null).Select(p => p.k + "=" + UrlEncode(p.v)));
    }

    public static class AppInfo
    {
        public static readonly Version Version = typeof(AppInfo).Assembly.GetName().Version;
        public static string VersionString => $"{Version.Major}.{Version.Minor}.{Version.Build}";
    }
}
