using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Accounts
{
    public enum AccountType { Offline, Microsoft, Yggdrasil }

    /// <summary>
    /// 一个账户。所有成员都是属性（而不是字段），方便 WPF 直接绑定。
    /// 时间统一存 UTC（DateTimeKind.Utc），界面显示时自己 ToLocalTime()。
    /// </summary>
    public class Account
    {
        /// <summary>稳定的随机 ID（Guid "N" 格式），与登录信息无关</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public AccountType Type { get; set; }
        /// <summary>角色名</summary>
        public string Name { get; set; }
        /// <summary>32 位十六进制，无横线，小写</summary>
        public string Uuid { get; set; }
        public string AccessToken { get; set; }
        /// <summary>微软：微软账号的 refresh token；外置登录不用</summary>
        public string RefreshToken { get; set; }
        /// <summary>Minecraft 令牌过期时间（微软账号，UTC）</summary>
        public DateTime ExpiresAt { get; set; }
        public string Xuid { get; set; }
        /// <summary>外置登录：经过 ALI 解析后的 API 根地址，如 https://littleskin.cn/api/yggdrasil</summary>
        public string ServerUrl { get; set; }
        /// <summary>外置登录：元数据里的 meta.serverName</summary>
        public string ServerName { get; set; }
        /// <summary>外置登录：登录用的邮箱 / 用户名</summary>
        public string LoginName { get; set; }
        /// <summary>外置登录：clientToken</summary>
        public string ClientToken { get; set; }
        /// <summary>可选：皮肤地址（用于头像）</summary>
        public string SkinUrl { get; set; }
        /// <summary>上次使用（UTC）</summary>
        public DateTime LastUsed { get; set; }
        /// <summary>
        /// 外置登录：账号下有多个角色、尚未绑定到具体角色（令牌还没有 selectedProfile）。
        /// 调用 YggdrasilAuth.SelectProfileAsync 后清除；PrepareAsync 也会自动补做。
        /// </summary>
        public bool ProfilePending { get; set; }

        public string TypeText
        {
            get
            {
                switch (Type)
                {
                    case AccountType.Offline: return "离线";
                    case AccountType.Microsoft: return "微软";
                    default: return string.IsNullOrWhiteSpace(ServerName) ? "外置登录" : ServerName;
                }
            }
        }

        public string Display => $"{Name} · {TypeText}";

        /// <summary>带横线的 UUID（8-4-4-4-12），格式不对时原样返回</summary>
        public string UuidDashed => AccountUtil.Dashed(Uuid);

        /// <summary>微软账号的 Minecraft 令牌是否已过期</summary>
        public bool IsExpired => Type == AccountType.Microsoft && DateTime.UtcNow >= ExpiresAt;

        public override string ToString() => Display;

        /// <summary>去重用的键：类型 + UUID（外置登录再加服务器地址）</summary>
        internal string DedupeKey
        {
            get
            {
                var k = Type + "|" + AccountUtil.NormalizeUuid(Uuid);
                if (Type == AccountType.Yggdrasil) k += "|" + (ServerUrl ?? "").TrimEnd('/').ToLowerInvariant();
                return k;
            }
        }

        public Account Clone() => (Account)MemberwiseClone();

        /// <summary>用另一个对象的登录信息覆盖自己（保留 Id）</summary>
        public void CopyFrom(Account o)
        {
            Type = o.Type; Name = o.Name; Uuid = o.Uuid; AccessToken = o.AccessToken; RefreshToken = o.RefreshToken;
            ExpiresAt = o.ExpiresAt; Xuid = o.Xuid; ServerUrl = o.ServerUrl; ServerName = o.ServerName; LoginName = o.LoginName;
            ClientToken = o.ClientToken; SkinUrl = o.SkinUrl; LastUsed = o.LastUsed; ProfilePending = o.ProfilePending;
        }

        // ---------- 序列化 ----------
        public JNode ToJson()
        {
            return JNode.Obj()
                .Set("id", Id)
                .Set("type", Type.ToString().ToLowerInvariant())
                .Set("name", Name)
                .Set("uuid", Uuid)
                .Set("accessToken", AccessToken)
                .Set("refreshToken", RefreshToken)
                .Set("expiresAt", AccountUtil.FormatTime(ExpiresAt))
                .Set("xuid", Xuid)
                .Set("serverUrl", ServerUrl)
                .Set("serverName", ServerName)
                .Set("loginName", LoginName)
                .Set("clientToken", ClientToken)
                .Set("skinUrl", SkinUrl)
                .Set("lastUsed", AccountUtil.FormatTime(LastUsed))
                .Set("profilePending", ProfilePending);
        }

        public static Account FromJson(JNode n)
        {
            if (n == null || !n.IsObj) return null;
            AccountType type;
            switch ((n["type"].Str ?? "").ToLowerInvariant())
            {
                case "offline": type = AccountType.Offline; break;
                case "microsoft": type = AccountType.Microsoft; break;
                case "yggdrasil": type = AccountType.Yggdrasil; break;
                default: return null;
            }
            var a = new Account
            {
                Type = type,
                Name = n["name"].Str,
                Uuid = AccountUtil.NormalizeUuid(n["uuid"].Str),
                AccessToken = n["accessToken"].Str,
                RefreshToken = n["refreshToken"].Str,
                ExpiresAt = AccountUtil.ParseTime(n["expiresAt"].Str),
                Xuid = n["xuid"].Str,
                ServerUrl = n["serverUrl"].Str,
                ServerName = n["serverName"].Str,
                LoginName = n["loginName"].Str,
                ClientToken = n["clientToken"].Str,
                SkinUrl = n["skinUrl"].Str,
                LastUsed = AccountUtil.ParseTime(n["lastUsed"].Str),
                ProfilePending = n["profilePending"].Bool,
            };
            var id = n["id"].Str;
            if (!string.IsNullOrEmpty(id)) a.Id = id;
            if (string.IsNullOrEmpty(a.Name)) return null;
            return a;
        }
    }

    /// <summary>启动游戏需要的身份信息，由 AccountService.PrepareAsync 生成</summary>
    public class LaunchAuth
    {
        public string PlayerName { get; set; }
        /// <summary>32 位十六进制，无横线</summary>
        public string Uuid { get; set; }
        public string AccessToken { get; set; }
        /// <summary>"msa" | "mojang" | "legacy"</summary>
        public string UserType { get; set; }
        public string Xuid { get; set; }
        public string ClientId { get; set; }
        public string UserProperties { get; set; } = "{}";
        /// <summary>额外的 JVM 参数（外置登录的 -javaagent 等），每项是一个独立参数，不要再拆分</summary>
        public List<string> ExtraJvmArgs { get; set; } = new List<string>();
    }

    /// <summary>
    /// 登录相关的错误。Message 是可以直接给用户看的中文说明。
    /// NeedsRelogin 为 true 时表示保存的登录信息已失效，界面应引导用户重新登录该账户。
    /// </summary>
    public class AuthException : Exception
    {
        public bool NeedsRelogin { get; }
        public AuthException(string message, bool needsRelogin = false, Exception inner = null) : base(message, inner)
        {
            NeedsRelogin = needsRelogin;
        }
    }

    internal static class AccountUtil
    {
        /// <summary>去掉横线、转小写；不是 32 位十六进制时返回去横线后的原文</summary>
        public static string NormalizeUuid(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return uuid;
            return uuid.Trim().Replace("-", "").ToLowerInvariant();
        }

        public static bool IsHex32(string s)
        {
            if (s == null || s.Length != 32) return false;
            foreach (var c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        public static string Dashed(string uuid)
        {
            var u = NormalizeUuid(uuid);
            if (!IsHex32(u)) return uuid;
            return $"{u.Substring(0, 8)}-{u.Substring(8, 4)}-{u.Substring(12, 4)}-{u.Substring(16, 4)}-{u.Substring(20)}";
        }

        public static string RandomHex32() => Guid.NewGuid().ToString("N");

        public static string FormatTime(DateTime t)
        {
            if (t == default) return null;
            if (t.Kind == DateTimeKind.Local) t = t.ToUniversalTime();
            else if (t.Kind == DateTimeKind.Unspecified) t = DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return t.ToString("o", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return default;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
                return t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return default;
        }

        /// <summary>从错误响应里挑一句能给人看的话（Yggdrasil 的 errorMessage / 微软的 error_description 等）</summary>
        public static string ErrorText(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            if (!JNode.TryParse(body, out var j) || !j.IsObj) return null;
            foreach (var k in new[] { "errorMessage", "error_description", "developerMessage", "message", "Message", "error" })
            {
                var s = j[k].Str;
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            return null;
        }

        /// <summary>是否是网络层面的失败（连不上 / 超时 / 5xx），而不是服务器明确拒绝</summary>
        public static bool IsNetworkError(Exception ex)
        {
            if (ex is AggregateException ag && ag.InnerExceptions.Count == 1) ex = ag.InnerExceptions[0];
            if (ex is HttpStatusException hs) return hs.Status >= 500 || hs.Status == 408 || hs.Status == 429;
            return ex is System.Net.Http.HttpRequestException || ex is TimeoutException || ex is System.Net.WebException
                || ex is System.IO.IOException || ex is System.Net.Sockets.SocketException;
        }

        /// <summary>
        /// .NET Framework 的连接池会复用已被服务器关掉的 keep-alive 连接，POST 时直接失败而不会自动重试。
        /// 遇到这种情况重试一次（请求实际上没有到达服务器）。
        /// </summary>
        public static async Task<T> RetryStale<T>(Func<Task<T>> op)
        {
            try { return await op().ConfigureAwait(false); }
            catch (Exception ex) when (IsStaleConnection(ex))
            {
                Log.Info("连接已被服务器关闭，重试一次");
                return await op().ConfigureAwait(false);
            }
        }

        static bool IsStaleConnection(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is System.Net.WebException we && (we.Status == System.Net.WebExceptionStatus.KeepAliveFailure || we.Status == System.Net.WebExceptionStatus.ConnectionClosed))
                    return true;
            return false;
        }

        public static string Short(Exception ex)
        {
            if (ex is AggregateException ag && ag.InnerExceptions.Count > 0) ex = ag.InnerExceptions[0];
            var m = ex.Message;
            if (ex is System.Net.Http.HttpRequestException && ex.InnerException != null) m += "（" + ex.InnerException.Message + "）";
            return m;
        }
    }
}
