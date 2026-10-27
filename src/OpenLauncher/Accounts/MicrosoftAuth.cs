using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Accounts
{
    /// <summary>设备代码登录的第一步结果：让用户打开 VerificationUri 输入 UserCode</summary>
    public class DeviceCodeInfo
    {
        public string UserCode { get; set; }
        public string VerificationUri { get; set; }
        public string DeviceCode { get; set; }
        /// <summary>微软返回的提示语（英文或本地化文本）</summary>
        public string Message { get; set; }
        /// <summary>轮询间隔（秒）</summary>
        public int Interval { get; set; }
        /// <summary>有效期（秒）</summary>
        public int ExpiresIn { get; set; }
        /// <summary>过期时间（UTC）</summary>
        public DateTime ExpiresAtUtc { get; set; }
        /// <summary>请求时使用的 Client ID（轮询时必须用同一个）</summary>
        public string ClientId { get; set; }
    }

    /// <summary>
    /// 微软正版登录：OAuth2 设备代码流程（consumers 租户）→ Xbox Live → XSTS → Minecraft 服务。
    /// 不使用任何镜像；不在日志里记录任何令牌。
    /// </summary>
    public static class MicrosoftAuth
    {
        const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        const string Scope = "XboxLive.signin offline_access";
        const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
        const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
        const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
        const string EntitlementsUrl = "https://api.minecraftservices.com/entitlements/mcstore";
        const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

        /// <summary>Mojang 的 Minecraft API 权限申请表</summary>
        public const string ReviewFormUrl = "https://aka.ms/mce-reviewappid";

        public static bool HasClientId => !string.IsNullOrWhiteSpace(Settings.MicrosoftClientId);

        public static string NoClientIdMessage =>
            "还没有配置微软登录用的 Client ID。\n" +
            $"{Branding.DisplayName} 是去中心化的开源项目，不内置任何人的 Client ID，需要你自己申请一个：\n" +
            "1. 打开 Azure 门户（portal.azure.com）→“应用注册”→ 新注册，受支持的账户类型选“个人 Microsoft 账户”；\n" +
            "2. 在该应用的“身份验证”页面把“允许公共客户端流”设为“是”（公共客户端）；\n" +
            $"3. 到 Mojang 的申请表（{ReviewFormUrl}）为这个应用申请 Minecraft API 访问权限，等待审核通过；\n" +
            "4. 把“应用程序(客户端) ID”粘贴到 设置 → 账户 里。\n" +
            "本发行版只支持微软正版登录，请先配置一个已获批的 Client ID。";

        // ---------- 第一步：申请设备代码 ----------
        public static async Task<DeviceCodeInfo> StartAsync(CancellationToken ct)
        {
            if (!HasClientId) throw new AuthException(NoClientIdMessage);
            var clientId = Settings.MicrosoftClientId.Trim();
            JNode r;
            try
            {
                r = await AccountUtil.RetryStale(() => Http.PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["scope"] = Scope,
                }, ct)).ConfigureAwait(false);
            }
            catch (HttpStatusException ex) { throw OAuthError(ex); }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex)) { throw new AuthException("无法连接到微软登录服务器：" + AccountUtil.Short(ex), false, ex); }

            var info = new DeviceCodeInfo
            {
                UserCode = r["user_code"].Str,
                VerificationUri = r["verification_uri"].Str ?? "https://www.microsoft.com/link",
                DeviceCode = r["device_code"].Str,
                Message = r["message"].Str,
                Interval = Math.Max(1, r["interval"].IntOr(5)),
                ExpiresIn = Math.Max(30, r["expires_in"].IntOr(900)),
                ClientId = clientId,
            };
            info.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(info.ExpiresIn);
            if (string.IsNullOrEmpty(info.DeviceCode) || string.IsNullOrEmpty(info.UserCode))
                throw new AuthException("微软登录服务器返回了无法识别的结果");
            Log.Info("微软登录：已获取设备代码，等待用户在浏览器中确认");
            return info;
        }

        // ---------- 第二步：轮询，拿到令牌后走完整条链 ----------
        public static async Task<Account> CompleteAsync(DeviceCodeInfo info, CancellationToken ct)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            var clientId = info.ClientId ?? Settings.MicrosoftClientId?.Trim();
            int interval = Math.Max(1, info.Interval);
            var deadline = info.ExpiresAtUtc == default ? DateTime.UtcNow.AddSeconds(Math.Max(30, info.ExpiresIn)) : info.ExpiresAtUtc;
            int netFailures = 0;
            JNode token = null;

            while (token == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
                if (DateTime.UtcNow > deadline) throw new AuthException("验证码已过期，请重新登录");
                try
                {
                    token = await AccountUtil.RetryStale(() => Http.PostFormAsync(TokenUrl, new Dictionary<string, string>
                    {
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                        ["client_id"] = clientId,
                        ["device_code"] = info.DeviceCode,
                    }, ct)).ConfigureAwait(false);
                    netFailures = 0;
                }
                catch (HttpStatusException ex) when (ex.Status >= 400 && ex.Status < 500)
                {
                    var err = ParseOAuthError(ex.Body, out var desc);
                    switch (err)
                    {
                        case "authorization_pending": continue;
                        case "slow_down": interval += 5; continue;
                        case "expired_token":
                        case "code_expired": throw new AuthException("验证码已过期，请重新登录");
                        case "authorization_declined":
                        case "access_denied": throw new AuthException("你在浏览器中拒绝了授权，登录已取消");
                        case "bad_verification_code": throw new AuthException("设备代码无效，请重新登录");
                        default: throw OAuthError(ex);
                    }
                }
                catch (Exception ex) when (AccountUtil.IsNetworkError(ex))
                {
                    // 偶发的网络抖动不中断登录，连续失败太多次再放弃
                    if (++netFailures >= 6) throw new AuthException("与微软登录服务器的连接不稳定：" + AccountUtil.Short(ex), false, ex);
                    Log.Warn("微软登录轮询失败，稍后重试：" + AccountUtil.Short(ex));
                }
            }

            var msAccess = token["access_token"].Str;
            if (string.IsNullOrEmpty(msAccess)) throw new AuthException("微软登录服务器没有返回令牌");
            var acc = new Account { Type = AccountType.Microsoft, RefreshToken = token["refresh_token"].Str };
            await LoginMinecraftAsync(acc, msAccess, ct).ConfigureAwait(false);
            Log.Info($"微软登录成功：{acc.Name}");
            return acc;
        }

        // ---------- 刷新 ----------
        /// <summary>用保存的 refresh token 重新走一遍整条链，原地更新 acc（Id 不变）</summary>
        public static async Task RefreshAsync(Account acc, CancellationToken ct)
        {
            if (acc == null) throw new ArgumentNullException(nameof(acc));
            if (acc.Type != AccountType.Microsoft) throw new ArgumentException("不是微软账户");
            if (string.IsNullOrEmpty(acc.RefreshToken)) throw new AuthException("微软账户的登录信息不完整，请重新登录", true);
            if (!HasClientId) throw new AuthException("刷新微软账户需要 Client ID，但当前没有配置。\n\n" + NoClientIdMessage, true);

            JNode token;
            try
            {
                token = await AccountUtil.RetryStale(() => Http.PostFormAsync(TokenUrl, new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = Settings.MicrosoftClientId.Trim(),
                    ["refresh_token"] = acc.RefreshToken,
                    ["scope"] = Scope,
                }, ct)).ConfigureAwait(false);
            }
            catch (HttpStatusException ex) when (ex.Status >= 400 && ex.Status < 500)
            {
                var err = ParseOAuthError(ex.Body, out _);
                if (err == "invalid_grant" || err == "interaction_required" || err == "consent_required")
                    throw new AuthException("微软账户的登录已失效（可能修改过密码或撤销了授权），请重新登录", true, ex);
                throw OAuthError(ex);
            }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex))
            {
                throw new AuthException("无法连接到微软登录服务器：" + AccountUtil.Short(ex), false, ex);
            }

            var msAccess = token["access_token"].Str;
            if (string.IsNullOrEmpty(msAccess)) throw new AuthException("微软登录服务器没有返回令牌");
            var tmp = acc.Clone();
            var newRefresh = token["refresh_token"].Str;
            if (!string.IsNullOrEmpty(newRefresh)) tmp.RefreshToken = newRefresh;
            await LoginMinecraftAsync(tmp, msAccess, ct).ConfigureAwait(false);
            // 整条链都成功了才写回，避免中途失败留下半新半旧的状态
            acc.RefreshToken = tmp.RefreshToken;
            acc.AccessToken = tmp.AccessToken;
            acc.ExpiresAt = tmp.ExpiresAt;
            acc.Xuid = tmp.Xuid;
            acc.Name = tmp.Name;
            acc.Uuid = tmp.Uuid;
            acc.SkinUrl = tmp.SkinUrl;
            Log.Info($"微软账户已刷新：{acc.Name}");
        }

        // ---------- Xbox Live → XSTS → Minecraft ----------
        static async Task LoginMinecraftAsync(Account acc, string msAccessToken, CancellationToken ct)
        {
            // 1. Xbox Live
            JNode xbl;
            try
            {
                xbl = await AccountUtil.RetryStale(() => Http.PostJsonAsync(XblUrl, JNode.Obj()
                    .Set("Properties", JNode.Obj()
                        .Set("AuthMethod", "RPS")
                        .Set("SiteName", "user.auth.xboxlive.com")
                        .Set("RpsTicket", "d=" + msAccessToken))
                    .Set("RelyingParty", "http://auth.xboxlive.com")
                    .Set("TokenType", "JWT"), null, ct, mirror: false)).ConfigureAwait(false);
            }
            catch (HttpStatusException ex) { throw new AuthException($"Xbox Live 登录失败（HTTP {ex.Status}）" + Detail(ex), false, ex); }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex)) { throw new AuthException("无法连接到 Xbox Live：" + AccountUtil.Short(ex), false, ex); }
            var xblToken = xbl["Token"].Str;
            var uhs = xbl["DisplayClaims"]["xui"][0]["uhs"].Str;
            if (string.IsNullOrEmpty(xblToken) || string.IsNullOrEmpty(uhs)) throw new AuthException("Xbox Live 返回了无法识别的结果");

            // 2. XSTS
            JNode xsts;
            try
            {
                xsts = await AccountUtil.RetryStale(() => Http.PostJsonAsync(XstsUrl, JNode.Obj()
                    .Set("Properties", JNode.Obj()
                        .Set("SandboxId", "RETAIL")
                        .Set("UserTokens", JNode.Arr().Add(xblToken)))
                    .Set("RelyingParty", "rp://api.minecraftservices.com/")
                    .Set("TokenType", "JWT"), null, ct, mirror: false)).ConfigureAwait(false);
            }
            catch (HttpStatusException ex) when (ex.Status == 401 || ex.Status == 403) { throw XstsError(ex); }
            catch (HttpStatusException ex) { throw new AuthException($"XSTS 授权失败（HTTP {ex.Status}）" + Detail(ex), false, ex); }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex)) { throw new AuthException("无法连接到 Xbox Live：" + AccountUtil.Short(ex), false, ex); }
            var xstsToken = xsts["Token"].Str;
            if (string.IsNullOrEmpty(xstsToken)) throw new AuthException("XSTS 返回了无法识别的结果");
            var xstsUhs = xsts["DisplayClaims"]["xui"][0]["uhs"].Str ?? uhs;
            var xuid = xsts["DisplayClaims"]["xui"][0]["xid"].Str;

            // 3. Minecraft 服务
            JNode mc;
            try
            {
                mc = await AccountUtil.RetryStale(() => Http.PostJsonAsync(McLoginUrl, JNode.Obj()
                    .Set("identityToken", $"XBL3.0 x={xstsUhs};{xstsToken}"), null, ct, mirror: false)).ConfigureAwait(false);
            }
            catch (HttpStatusException ex)
            {
                var text = AccountUtil.ErrorText(ex.Body) ?? "";
                if (ex.Status == 403 && text.IndexOf("app registration", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new AuthException($"当前 Client ID 还没有获得 Mojang 的 Minecraft API 使用许可。请到 {ReviewFormUrl} 提交申请，审核通过后再登录。", false, ex);
                if (ex.Status == 429) throw new AuthException("登录请求过于频繁，请稍等几分钟再试", false, ex);
                throw new AuthException($"Minecraft 服务登录失败（HTTP {ex.Status}）" + Detail(ex), false, ex);
            }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex)) { throw new AuthException("无法连接到 Minecraft 服务：" + AccountUtil.Short(ex), false, ex); }
            var mcToken = mc["access_token"].Str;
            if (string.IsNullOrEmpty(mcToken)) throw new AuthException("Minecraft 服务没有返回令牌");
            var expiresIn = mc["expires_in"].LongOr(86400);
            var auth = new Dictionary<string, string> { ["Authorization"] = "Bearer " + mcToken };

            // 4. 是否拥有游戏（XGP 用户这里可能是空的，所以不在这一步直接判死，以档案为准）
            bool? owns = null;
            try
            {
                var ent = await AccountUtil.RetryStale(() => Http.GetJsonAsync(EntitlementsUrl, ct, auth, mirror: false)).ConfigureAwait(false);
                owns = ent["items"].Count > 0;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                Log.Warn("查询 Minecraft 购买记录失败（不影响登录）：" + AccountUtil.Short(ex));
            }

            // 5. 档案
            JNode profile;
            try
            {
                profile = await AccountUtil.RetryStale(() => Http.GetJsonAsync(ProfileUrl, ct, auth, mirror: false)).ConfigureAwait(false);
            }
            catch (HttpStatusException ex) when (ex.Status == 404)
            {
                if (owns == true)
                    throw new AuthException("该账号已拥有 Minecraft，但还没有创建角色。请先在 minecraft.net 或官方启动器里设置角色名，然后再登录。", false, ex);
                throw new AuthException("该账号没有购买 Minecraft Java 版", false, ex);
            }
            catch (HttpStatusException ex) { throw new AuthException($"获取 Minecraft 档案失败（HTTP {ex.Status}）" + Detail(ex), false, ex); }
            catch (Exception ex) when (AccountUtil.IsNetworkError(ex)) { throw new AuthException("无法连接到 Minecraft 服务：" + AccountUtil.Short(ex), false, ex); }

            var id = AccountUtil.NormalizeUuid(profile["id"].Str);
            var name = profile["name"].Str;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) throw new AuthException("该账号没有购买 Minecraft Java 版");

            acc.Type = AccountType.Microsoft;
            acc.Uuid = id;
            acc.Name = name;
            acc.AccessToken = mcToken;
            acc.ExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn));
            acc.Xuid = !string.IsNullOrEmpty(xuid) ? xuid : JwtClaim(mcToken, "xuid") ?? acc.Xuid;
            acc.SkinUrl = ActiveSkin(profile) ?? acc.SkinUrl;
        }

        internal static string ActiveSkin(JNode profile)
        {
            string first = null;
            foreach (var s in profile["skins"])
            {
                var url = s["url"].Str;
                if (string.IsNullOrEmpty(url)) continue;
                if (string.Equals(s["state"].Str, "ACTIVE", StringComparison.OrdinalIgnoreCase)) return url;
                first = first ?? url;
            }
            return first;
        }

        /// <summary>从 JWT 的 payload 里读一个字段（只解码，不校验签名；仅用于取 xuid 这类展示信息）</summary>
        static string JwtClaim(string jwt, string claim)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                var p = parts[1].Replace('-', '+').Replace('_', '/');
                switch (p.Length % 4) { case 2: p += "=="; break; case 3: p += "="; break; }
                var json = JNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
                return json[claim].Str;
            }
            catch { return null; }
        }

        // ---------- 错误翻译 ----------
        static string ParseOAuthError(string body, out string description)
        {
            description = null;
            if (!JNode.TryParse(body ?? "", out var j) || !j.IsObj) return null;
            description = j["error_description"].Str;
            return j["error"].Str;
        }

        static AuthException OAuthError(HttpStatusException ex)
        {
            var err = ParseOAuthError(ex.Body, out var desc);
            desc = desc ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(desc, @"AADSTS(\d+)");
            var code = m.Success ? m.Groups[1].Value : "";
            // 700016：找不到该应用；700038：不是有效的应用 ID；7000218 / 70002 / unauthorized_client：没开公共客户端流
            if (code == "700016" || code == "700038" || (err == "invalid_client" && desc.Contains("not found")))
                return new AuthException("Client ID 无效：在 Azure 里找不到这个应用，请检查 设置 → 账户 里填写的 Client ID", false, ex);
            if (code == "7000218" || code == "70002" || err == "unauthorized_client" || err == "invalid_client")
                return new AuthException("这个 Client ID 不允许设备代码登录：请在 Azure 应用的“身份验证”页面开启“允许公共客户端流”，并确认受支持的账户类型包含个人 Microsoft 账户", false, ex);
            if (err == "invalid_scope")
                return new AuthException("这个 Client ID 无法申请 Xbox Live 权限，请确认应用支持个人 Microsoft 账户", false, ex);
            if (err == "invalid_grant")
                return new AuthException("微软账户的登录已失效，请重新登录", true, ex);
            var first = FirstLine(desc);
            return new AuthException($"微软登录失败（{err ?? "HTTP " + ex.Status}）" + (string.IsNullOrEmpty(first) ? "" : "：" + first), false, ex);
        }

        static AuthException XstsError(HttpStatusException ex)
        {
            long xerr = 0;
            if (JNode.TryParse(ex.Body ?? "", out var j)) xerr = j["XErr"].Long;
            string msg;
            switch (xerr)
            {
                case 2148916227: msg = "该账号已被 Xbox Live 封禁"; break;
                case 2148916229: msg = "该账号受家长控制限制，需要家长在 Xbox 家庭设置中允许多人游戏"; break;
                case 2148916233: msg = "该微软账号没有 Xbox 账户，请先去 xbox.com 创建"; break;
                case 2148916234: msg = "该账号还没有接受 Xbox 服务条款，请先登录 xbox.com 完成设置"; break;
                case 2148916235: msg = "该账号所在的国家或地区不能使用 Xbox Live"; break;
                case 2148916236:
                case 2148916237: msg = "该账号需要完成成人验证（韩国地区），请先在 xbox.com 完成验证"; break;
                case 2148916238: msg = "这是未成年人账号，需要由家长将其加入 Microsoft 家庭组后才能登录"; break;
                default: msg = xerr != 0 ? $"Xbox Live 拒绝了授权（XErr {xerr}）" : $"Xbox Live 拒绝了授权（HTTP {ex.Status}）"; break;
            }
            return new AuthException(msg, false, ex);
        }

        static string Detail(HttpStatusException ex)
        {
            var t = FirstLine(AccountUtil.ErrorText(ex.Body));
            return string.IsNullOrEmpty(t) ? "" : "：" + t;
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            var i = s.IndexOfAny(new[] { '\r', '\n' });
            if (i > 0) s = s.Substring(0, i);
            return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
        }
    }
}
