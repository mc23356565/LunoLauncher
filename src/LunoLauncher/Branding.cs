namespace LunoLauncher
{
    /// <summary>
    /// 换名字、换仓库、换维护者，只改这一个文件。
    /// 本文件当前为 LunoLauncher —— OpenLauncher 的独立分支（GPL-3.0）。
    /// 改名只需改这里的 AppName / DisplayName / Tagline / RepoUrl，再改 README 与 docs/index.html。
    /// </summary>
    public static class Branding
    {
        public const string AppName = "LunoLauncher";
        public const string DisplayName = "LunoLauncher";
        public const string Tagline = "轻量、开放的 Minecraft 启动器";

        /// <summary>
        /// 是否允许离线账号。**官方发行版保持 false**：
        /// 离线账号等于"绕过正版验证的途径"，而 Mojang 的第三方 API 白名单审核最在意这一点。
        /// 关闭后：账号类型里没有「离线」，任何入口都无法创建离线账号，已存在的离线账号也无法启动。
        /// 只在本地自用、不对外分发时，可以临时改成 true 再编译。
        /// </summary>
        public static readonly bool AllowOfflineAccount = false;

        /// <summary>
        /// 是否允许外置登录（authlib-injector / Yggdrasil 皮肤站）。**官方发行版保持 false**：
        /// 外置登录验证的是第三方皮肤站账号，**不验证玩家是否拥有 Minecraft: Java Edition**，
        /// 而官方审核明确要求 "verify the user's game ownership"。
        /// 关闭后：账号类型里没有「外置登录」，旧的 Yggdrasil 账号也无法启动。
        /// </summary>
        public static readonly bool AllowThirdPartyLogin = false;

        /// <summary>
        /// 是否启用内置更新功能（签名清单 / 自更新）。
        /// 暂时关闭：这一版不对外提供更新，界面里的更新入口会一起隐藏。
        /// </summary>
        public static readonly bool EnableUpdater = false;

        /// <summary>主页 / 源码下载（仅用于"关于"页面和 User-Agent，不是更新的唯一来源）</summary>
        public const string RepoUrl = "https://github.com/mc23356565/LunoLauncher";

        /// <summary>
        /// 内置的更新清单地址。**发布自己的版本之前必须换成你自己的地址。**
        /// 留空 = 这个版本不接受任何自动更新（设置页会提示"没有内置发布公钥"），这是安全默认值：
        /// 不继承上游的更新通道，避免别人的清单、别人的钥匙出现在你的发行版里。
        /// 填法示例（顺序不代表优先级，会全部尝试并取版本最高的有效清单）：
        ///   "https://raw.githubusercontent.com/mc23356565/LunoLauncher/main/release/update.json",
        ///   "https://cdn.jsdelivr.net/gh/mc23356565/LunoLauncher@main/release/update.json",
        /// </summary>
        public static readonly string[] DefaultUpdateSources = { };

        /// <summary>
        /// 内置信任的发布公钥（RSA，.NET XML 格式）。用 tools/Release 的 keygen 生成。
        /// 含 "@@" 的占位会被忽略 —— 生成自己的钥匙后，把 public-key.xml 的内容替换掉下面这一行。
        /// 私钥离线保存，永远不要提交到仓库。
        /// </summary>
        public static readonly string[] TrustedKeys =
        {
            // @@ 把 public-key.xml 的内容粘贴到下面这一行的位置（保留引号）
            "@@REPLACE-WITH-YOUR-PUBLIC-KEY.XML@@",
        };

        /// <summary>
        /// 微软正版登录用的 Azure 应用 Client ID。
        /// 需要自己在 Azure 注册应用并向 Mojang 申请 Minecraft API 白名单后填入；
        /// 留空时用户可在 设置 → 账户 里填自己的（但同样需要该 ID 已被批准）。
        /// </summary>
        public const string DefaultMicrosoftClientId = "";

        /// <summary>CurseForge API Key（可选）。留空时自动走无需 Key 的镜像。</summary>
        public const string DefaultCurseForgeApiKey = "";
    }
}
