using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Accounts
{
    /// <summary>
    /// 外置登录（Yggdrasil / authlib-injector 规范），例如 LittleSkin、各类 Blessing Skin 皮肤站。
    /// 认证相关请求一律不走镜像。
    /// </summary>
    public static class YggdrasilAuth
    {
        const string AliHeader = "X-Authlib-Injector-API-Location";
        static readonly ConcurrentDictionary<string, (JNode meta, DateTime time)> MetaCache = new ConcurrentDictionary<string, (JNode, DateTime)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 把用户输入的地址解析成 API 根地址：补 https://、跟随跳转、读取 ALI 响应头（可以是相对地址）。
        /// 首页没有 ALI 头（或首页暂时打不开）时，按 Blessing Skin 的惯例再试一次 {地址}/api/yggdrasil。
        /// 返回值不带末尾的 "/"。连不上时抛 AuthException。
        /// </summary>
        public static async Task<string> ResolveApiAsync(string inputUrl, CancellationToken ct)
        {
            var url = (inputUrl ?? "").Trim();
            if (url.Length == 0) throw new AuthException("请输入认证服务器地址");
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "https://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new AuthException("认证服务器地址格式不正确");

            Probe first = null;
            Exception error = null;
            try { first = await AccountUtil.RetryStale(() => ProbeAsync(uri, ct)).ConfigureAwait(false); }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex)) { error = ex; }

            if (first?.Ali != null)
            {
                if (!string.Equals(first.Ali, first.Final, StringComparison.OrdinalIgnoreCase))
                    Log.Info($"外置登录：{uri.Host} 指向 API {first.Ali}");
                return first.Ali;
            }
            if (first != null && first.IsMetadata) return first.Final;

            var baseUrl = first?.Final ?? TrimUrl(uri);
            if (!baseUrl.EndsWith("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var guess = await AccountUtil.RetryStale(() => ProbeAsync(new Uri(baseUrl + "/api/yggdrasil"), ct)).ConfigureAwait(false);
                    if (guess.Ali != null) return guess.Ali;
                    if (guess.IsMetadata)
                    {
                        Log.Info($"外置登录：{uri.Host} 没有提供 ALI，按惯例使用 {guess.Final}");
                        return guess.Final;
                    }
                }
                catch (Exception ex) when (AccountUtil.IsNetworkError(ex)) { error = error ?? ex; }
            }

            if (first == null)
                throw new AuthException($"无法连接到认证服务器 {uri.Host}：{AccountUtil.Short(error)}", false, error);
            // 交给 GetMetadataAsync 去报告具体问题
            return first.Final;
        }

        sealed class Probe
        {
            public string Final;      // 跟随跳转后的地址
            public string Ali;        // ALI 指向的 API 地址（没有为 null）
            public bool IsMetadata;   // 这个地址本身就是 API 根（返回了元数据）
        }

        static async Task<Probe> ProbeAsync(Uri uri, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    req.Headers.Accept.ParseAdd("application/json");
                    HttpResponseMessage resp;
                    try { resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("连接超时"); }
                    using (resp)
                    {
                        var finalUri = resp.RequestMessage?.RequestUri ?? uri;
                        var p = new Probe { Final = TrimUrl(finalUri) };
                        if (resp.Headers.TryGetValues(AliHeader, out var values))
                        {
                            var loc = values.FirstOrDefault()?.Trim();
                            if (!string.IsNullOrEmpty(loc) && Uri.TryCreate(finalUri, loc, out var target)
                                && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps))
                            {
                                var api = TrimUrl(target);
                                // ALI 指向自己时等同于没有跳转，继续检查是不是元数据
                                if (!string.Equals(api, p.Final, StringComparison.OrdinalIgnoreCase)) p.Ali = api;
                            }
                        }
                        if (p.Ali == null && resp.IsSuccessStatusCode)
                        {
                            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                            var len = resp.Content.Headers.ContentLength;
                            if (type.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 || (len.HasValue && len.Value < 256 * 1024))
                            {
                                // .NET Framework 的 ReadAsStringAsync 不能取消，用 WhenAny 限时
                                var read = resp.Content.ReadAsStringAsync();
                                var done = await Task.WhenAny(read, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
                                if (done != read)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    throw new TimeoutException("连接超时");
                                }
                                var body = await read.ConfigureAwait(false);
                                if (JNode.TryParse(body, out var meta) && IsMetadata(meta))
                                {
                                    p.IsMetadata = true;
                                    MetaCache[p.Final] = (meta, DateTime.UtcNow);
                                }
                            }
                        }
                        return p;
                    }
                }
            }
        }

        static bool IsMetadata(JNode m) => m != null && m.IsObj && (m.Has("meta") || m.Has("skinDomains") || m.Has("signaturePublickey"));

        static string TrimUrl(Uri u) => u.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');

        /// <summary>读取服务器元数据（API 根地址的 GET 结果）。10 分钟内的结果会复用。</summary>
        public static async Task<JNode> GetMetadataAsync(string api, CancellationToken ct)
        {
            api = (api ?? "").TrimEnd('/');
            if (MetaCache.TryGetValue(api, out var c) && DateTime.UtcNow - c.time < TimeSpan.FromMinutes(10)) return c.meta;
            JNode meta;
            try
            {
                meta = await AccountUtil.RetryStale(() => Http.GetJsonAsync(api, ct, null, mirror: false, timeoutSec: 15)).ConfigureAwait(false);
            }
            catch (FormatException ex) { throw new AuthException("这个地址不是有效的外置登录（Yggdrasil）服务器", false, ex); }
            catch (HttpStatusException ex) { throw new AuthException($"这个地址不是有效的外置登录服务器（HTTP {ex.Status}）", false, ex); }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex)) { throw new AuthException("无法连接到认证服务器：" + AccountUtil.Short(ex), false, ex); }
            if (!IsMetadata(meta))
                throw new AuthException("这个地址不是有效的外置登录（Yggdrasil）服务器");
            MetaCache[api] = (meta, DateTime.UtcNow);
            return meta;
        }

        /// <summary>服务器名称：meta.serverName，没有时用主机名</summary>
        public static string ServerNameOf(JNode meta, string api)
        {
            var n = meta?["meta"]["serverName"].Str;
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            try { return new Uri(api).Host; } catch { return null; }
        }

        /// <summary>
        /// 用账号密码登录。
        /// 已选定角色（或只有一个角色）时返回只含一个元素的列表，可以直接 AccountStore.AddOrUpdate；
        /// 有多个角色且服务器没有替你选时，每个角色返回一个 ProfilePending=true 的 Account，
        /// 让用户挑一个后调用 SelectProfileAsync（不要把其余的保存下来，它们共享同一个令牌）。
        /// </summary>
        public static async Task<List<Account>> LoginAsync(string api, string login, string password, CancellationToken ct)
        {
            api = (api ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(api)) throw new AuthException("请先填写认证服务器地址");
            if (string.IsNullOrWhiteSpace(login)) throw new AuthException("请输入账号（邮箱或用户名）");
            if (string.IsNullOrEmpty(password)) throw new AuthException("请输入密码");
            login = login.Trim();

            string serverName = null;
            try { serverName = ServerNameOf(await GetMetadataAsync(api, ct).ConfigureAwait(false), api); }
            catch (AuthException ex) { Log.Warn("读取外置登录服务器元数据失败：" + ex.Message); }
            serverName = serverName ?? ServerNameOf(null, api);

            var clientToken = AccountUtil.RandomHex32();
            var body = JNode.Obj()
                .Set("agent", JNode.Obj().Set("name", "Minecraft").Set("version", 1))
                .Set("username", login)
                .Set("password", password)
                .Set("clientToken", clientToken)
                .Set("requestUser", true);
            var r = await PostAsync(api + "/authserver/authenticate", body, ct, "登录失败").ConfigureAwait(false);

            var accessToken = r["accessToken"].Str;
            if (string.IsNullOrEmpty(accessToken)) throw new AuthException("认证服务器没有返回令牌");
            clientToken = r["clientToken"].Str ?? clientToken;

            Account Make(JNode profile, bool pending) => new Account
            {
                Type = AccountType.Yggdrasil,
                Name = profile["name"].Str,
                Uuid = AccountUtil.NormalizeUuid(profile["id"].Str),
                AccessToken = accessToken,
                ClientToken = clientToken,
                ServerUrl = api,
                ServerName = serverName,
                LoginName = login,
                ProfilePending = pending,
            };

            var result = new List<Account>();
            var selected = r["selectedProfile"];
            if (selected.IsObj && !string.IsNullOrEmpty(selected["id"].Str))
            {
                result.Add(Make(selected, false));
            }
            else
            {
                var profiles = r["availableProfiles"].Where(p => !string.IsNullOrEmpty(p["id"].Str) && !string.IsNullOrEmpty(p["name"].Str)).ToList();
                if (profiles.Count == 0) throw new AuthException("这个账号下还没有角色，请先到皮肤站网页上创建一个角色");
                if (profiles.Count == 1)
                {
                    var a = Make(profiles[0], true);
                    result.Add(await SelectProfileAsync(a, a.Uuid, a.Name, ct).ConfigureAwait(false));
                }
                else result.AddRange(profiles.Select(p => Make(p, true)));
            }
            Log.Info($"外置登录成功：{serverName}，{result.Count} 个角色");
            return result;
        }

        /// <summary>把令牌绑定到指定角色（/authserver/refresh 带 selectedProfile），原地更新并返回 acc</summary>
        public static async Task<Account> SelectProfileAsync(Account acc, string profileId, string profileName, CancellationToken ct)
        {
            if (acc == null) throw new ArgumentNullException(nameof(acc));
            profileId = AccountUtil.NormalizeUuid(profileId);
            var body = JNode.Obj()
                .Set("accessToken", acc.AccessToken)
                .Set("requestUser", true)
                .Set("selectedProfile", JNode.Obj().Set("id", profileId).Set("name", profileName));
            if (!string.IsNullOrEmpty(acc.ClientToken)) body.Set("clientToken", acc.ClientToken);
            var r = await PostAsync(acc.ServerUrl.TrimEnd('/') + "/authserver/refresh", body, ct, "选择角色失败", relogin: true).ConfigureAwait(false);
            Apply(acc, r);
            if (string.IsNullOrEmpty(acc.Uuid) || acc.ProfilePending)
            {
                // 个别服务器不回 selectedProfile，按请求的为准
                acc.Uuid = profileId;
                acc.Name = profileName ?? acc.Name;
            }
            acc.ProfilePending = false;
            return acc;
        }

        /// <summary>刷新令牌（原地更新 acc）。登录信息失效时抛 NeedsRelogin=true 的 AuthException</summary>
        public static async Task RefreshAsync(Account acc, CancellationToken ct)
        {
            if (acc == null) throw new ArgumentNullException(nameof(acc));
            if (acc.ProfilePending) { await SelectProfileAsync(acc, acc.Uuid, acc.Name, ct).ConfigureAwait(false); return; }
            var body = JNode.Obj()
                .Set("accessToken", acc.AccessToken)
                .Set("requestUser", true);
            if (!string.IsNullOrEmpty(acc.ClientToken)) body.Set("clientToken", acc.ClientToken);
            var r = await PostAsync(acc.ServerUrl.TrimEnd('/') + "/authserver/refresh", body, ct, "刷新登录失败", relogin: true).ConfigureAwait(false);
            Apply(acc, r);
            Log.Info($"外置登录已刷新：{acc.Name}");
        }

        /// <summary>令牌是否仍然有效（204 = 有效，403 = 无效）。网络错误时抛异常。</summary>
        public static async Task<bool> ValidateAsync(Account acc, CancellationToken ct)
        {
            if (acc == null) throw new ArgumentNullException(nameof(acc));
            if (string.IsNullOrEmpty(acc.AccessToken)) return false;
            var body = JNode.Obj().Set("accessToken", acc.AccessToken);
            if (!string.IsNullOrEmpty(acc.ClientToken)) body.Set("clientToken", acc.ClientToken);
            try
            {
                await AccountUtil.RetryStale(() => Http.PostJsonAsync(acc.ServerUrl.TrimEnd('/') + "/authserver/validate", body, null, ct, mirror: false, timeoutSec: 15)).ConfigureAwait(false);
                return true;
            }
            catch (HttpStatusException ex) when (ex.Status >= 400 && ex.Status < 500) { return false; }
            catch (FormatException) { return true; } // 2xx 但响应体不是 JSON：按有效处理
        }

        /// <summary>注销本次登录的令牌（删除账户时可选调用；失败不抛异常）</summary>
        public static async Task InvalidateAsync(Account acc, CancellationToken ct)
        {
            if (acc == null || string.IsNullOrEmpty(acc.AccessToken) || string.IsNullOrEmpty(acc.ServerUrl)) return;
            try
            {
                var body = JNode.Obj().Set("accessToken", acc.AccessToken);
                if (!string.IsNullOrEmpty(acc.ClientToken)) body.Set("clientToken", acc.ClientToken);
                await AccountUtil.RetryStale(() => Http.PostJsonAsync(acc.ServerUrl.TrimEnd('/') + "/authserver/invalidate", body, null, ct, mirror: false, timeoutSec: 10)).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Warn("注销外置登录令牌失败：" + AccountUtil.Short(ex)); }
        }

        static void Apply(Account acc, JNode r)
        {
            var token = r["accessToken"].Str;
            if (string.IsNullOrEmpty(token)) throw new AuthException("认证服务器没有返回令牌");
            acc.AccessToken = token;
            var ctk = r["clientToken"].Str;
            if (!string.IsNullOrEmpty(ctk)) acc.ClientToken = ctk;
            var sel = r["selectedProfile"];
            if (sel.IsObj && !string.IsNullOrEmpty(sel["id"].Str))
            {
                acc.Uuid = AccountUtil.NormalizeUuid(sel["id"].Str);
                acc.Name = sel["name"].Str ?? acc.Name;
                acc.ProfilePending = false;
            }
        }

        static async Task<JNode> PostAsync(string url, JNode body, CancellationToken ct, string what, bool relogin = false)
        {
            try
            {
                return await AccountUtil.RetryStale(() => Http.PostJsonAsync(url, body, null, ct, mirror: false, timeoutSec: 20)).ConfigureAwait(false);
            }
            catch (HttpStatusException ex) when (ex.Status >= 400 && ex.Status < 500)
            {
                var text = AccountUtil.ErrorText(ex.Body);
                if (relogin && (ex.Status == 401 || ex.Status == 403))
                    throw new AuthException("外置登录已失效，请重新输入密码登录" + (string.IsNullOrEmpty(text) ? "" : "（" + text + "）"), true, ex);
                if (ex.Status == 404) throw new AuthException($"{what}：服务器不支持该接口，请检查认证服务器地址", false, ex);
                if (ex.Status == 429) throw new AuthException($"{what}：请求过于频繁，请稍后再试", false, ex);
                throw new AuthException($"{what}：{text ?? "HTTP " + ex.Status}", false, ex);
            }
            catch (HttpStatusException ex)
            {
                throw new AuthException($"{what}：认证服务器出错（HTTP {ex.Status}）", false, ex);
            }
            catch (FormatException ex)
            {
                throw new AuthException($"{what}：认证服务器返回了无法识别的内容", false, ex);
            }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex))
            {
                throw new AuthException($"{what}：无法连接到认证服务器（{AccountUtil.Short(ex)}）", false, ex);
            }
        }
    }
}
