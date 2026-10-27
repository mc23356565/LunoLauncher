using System;
using OpenLauncher.Core;

namespace OpenLauncher.Accounts
{
    /// <summary>离线账户：只有名字和 UUID，不联网</summary>
    public static class OfflineAuth
    {
        /// <summary>
        /// 新建离线账户。uuid 为空时使用与服务端 offline 模式一致的 UUID（"OfflinePlayer:" + 名字 的 v3 UUID）。
        /// 名字不合法（见 IsValidName）或 uuid 格式不对时抛 ArgumentException。
        /// </summary>
        public static Account Create(string name, string uuid = null)
        {
            if (!Branding.AllowOfflineAccount)
                throw new InvalidOperationException("这个发行版不提供离线账号。请使用微软正版账号或外置登录（authlib-injector）。");
            name = name?.Trim();
            if (!IsValidName(name, out var reason)) throw new ArgumentException(reason, nameof(name));
            string id;
            if (string.IsNullOrWhiteSpace(uuid)) id = Util.OfflineUuid(name);
            else
            {
                id = AccountUtil.NormalizeUuid(uuid);
                if (!AccountUtil.IsHex32(id)) throw new ArgumentException("UUID 格式不正确：应为 32 位十六进制（可以带横线）", nameof(uuid));
            }
            return new Account
            {
                Type = AccountType.Offline,
                Name = name,
                Uuid = id,
            };
        }

        /// <summary>
        /// 检查离线名字。只有为空、超过 16 个字符、含空白字符时返回 false（reason 为原因）。
        /// 返回 true 时 reason 也可能不为空：表示可以用但不推荐（推荐 3~16 个英文字母、数字或下划线）。
        /// </summary>
        public static bool IsValidName(string name, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(name)) { reason = "名字不能为空"; return false; }
            if (name.Length > 16) { reason = "名字不能超过 16 个字符"; return false; }
            foreach (var c in name)
                if (char.IsWhiteSpace(c)) { reason = "名字不能包含空格"; return false; }

            bool standard = true;
            foreach (var c in name)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) { standard = false; break; }

            if (!standard)
                reason = "名字含有英文字母、数字、下划线以外的字符，部分服务器、模组可能无法正常处理";
            else if (name.Length < 3)
                reason = "名字少于 3 个字符，部分服务器可能会拒绝";
            return true;
        }
    }
}
