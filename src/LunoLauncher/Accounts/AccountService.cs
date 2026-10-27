using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LunoLauncher.Core;

namespace LunoLauncher.Accounts
{
    /// <summary>启动前的账户准备：按需刷新令牌、准备 authlib-injector，生成启动参数需要的身份信息</summary>
    public static class AccountService
    {
        /// <summary>微软令牌离过期不足这么久就提前刷新</summary>
        static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);
        // 同一个账户的刷新串行进行（同时启动两个实例时不会各刷一遍、互相把令牌顶掉）
        static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// 为启动游戏准备身份信息。会原地更新 acc（新令牌、角色名等）并保存到 AccountStore。
        /// 认证服务器连不上时不阻止启动（沿用缓存的令牌，单人游戏不受影响）；
        /// 登录信息明确失效时抛 NeedsRelogin=true 的 AuthException。
        /// 只修改 task.Detail，不改动进度。
        /// </summary>
        public static async Task<LaunchAuth> PrepareAsync(Account acc, LauncherTask task, CancellationToken ct)
        {
            if (acc == null) throw new AuthException("还没有选择账户，请先在“账户”页面添加一个账户");
            if (string.IsNullOrEmpty(acc.Name)) throw new AuthException("账户信息不完整，请删除后重新添加");

            if (acc.Type == AccountType.Offline)
            {
                if (!Branding.AllowOfflineAccount)
                    throw new AuthException("这个发行版不提供离线账号，无法启动。请添加微软正版账号或使用外置登录。");
                var uuid = AccountUtil.NormalizeUuid(acc.Uuid);
                if (!AccountUtil.IsHex32(uuid)) uuid = Util.OfflineUuid(acc.Name);
                Touch(acc);
                return new LaunchAuth
                {
                    PlayerName = acc.Name,
                    Uuid = uuid,
                    AccessToken = AccountUtil.RandomHex32(),
                    UserType = "legacy",
                    Xuid = "0",
                    ClientId = "0",
                };
            }

            var gate = Gates.GetOrAdd(acc.Id ?? "", _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (acc.Type == AccountType.Microsoft) return await PrepareMicrosoftAsync(acc, task, ct).ConfigureAwait(false);
                if (!Branding.AllowThirdPartyLogin)
                    throw new AuthException("这个发行版只支持微软正版登录，不支持外置登录。请添加微软正版账号。");
                return await PrepareYggdrasilAsync(acc, task, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        static async Task<LaunchAuth> PrepareMicrosoftAsync(Account acc, LauncherTask task, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(acc.AccessToken) || DateTime.UtcNow + RefreshMargin >= acc.ExpiresAt)
            {
                SetDetail(task, "正在刷新微软账户登录…");
                try
                {
                    await MicrosoftAuth.RefreshAsync(acc, ct).ConfigureAwait(false);
                    Skins.Invalidate(acc);
                }
                catch (Exception ex) when (IsTransient(ex) && !string.IsNullOrEmpty(acc.AccessToken))
                {
                    // 离线模式已在本发行版关闭
                    Log.Warn("刷新微软账户失败（网络问题），使用缓存的登录信息启动：" + AccountUtil.Short(ex));
                    SetDetail(task, "无法连接微软服务器，使用缓存的登录信息");
                }
            }
            Touch(acc);
            return new LaunchAuth
            {
                PlayerName = acc.Name,
                Uuid = AccountUtil.NormalizeUuid(acc.Uuid),
                AccessToken = acc.AccessToken,
                UserType = "msa",
                Xuid = string.IsNullOrEmpty(acc.Xuid) ? "0" : acc.Xuid,
                ClientId = string.IsNullOrWhiteSpace(Settings.MicrosoftClientId) ? "0" : Settings.MicrosoftClientId.Trim(),
            };
        }

        static async Task<LaunchAuth> PrepareYggdrasilAsync(Account acc, LauncherTask task, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(acc.ServerUrl)) throw new AuthException("外置登录账户缺少认证服务器地址，请删除后重新添加");

            if (acc.ProfilePending)
            {
                SetDetail(task, "正在选择外置登录角色…");
                await YggdrasilAuth.SelectProfileAsync(acc, acc.Uuid, acc.Name, ct).ConfigureAwait(false);
            }
            else
            {
                SetDetail(task, "正在验证外置登录…");
                bool valid;
                try { valid = await YggdrasilAuth.ValidateAsync(acc, ct).ConfigureAwait(false); }
                catch (Exception ex) when (IsTransient(ex))
                {
                    Log.Warn("验证外置登录失败（网络问题），使用缓存的登录信息启动：" + AccountUtil.Short(ex));
                    SetDetail(task, "无法连接认证服务器，使用缓存的登录信息");
                    valid = true;
                }
                if (!valid)
                {
                    SetDetail(task, "正在刷新外置登录…");
                    try { await YggdrasilAuth.RefreshAsync(acc, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        Log.Warn("刷新外置登录失败（网络问题），使用缓存的登录信息启动：" + AccountUtil.Short(ex));
                    }
                }
            }

            SetDetail(task, "正在准备 authlib-injector…");
            var jvm = await AuthlibInjector.JvmArgsAsync(acc.ServerUrl, task, ct).ConfigureAwait(false);
            Touch(acc);
            return new LaunchAuth
            {
                PlayerName = acc.Name,
                Uuid = AccountUtil.NormalizeUuid(acc.Uuid),
                AccessToken = acc.AccessToken,
                UserType = "mojang",
                Xuid = "0",
                ClientId = "0",
                ExtraJvmArgs = jvm,
            };
        }

        /// <summary>
        /// 不启动游戏、只刷新一下登录状态（例如账户页面的“刷新”按钮）。
        /// 失败时抛 AuthException。
        /// </summary>
        public static async Task RefreshAsync(Account acc, CancellationToken ct)
        {
            if (acc == null || acc.Type == AccountType.Offline) return;
            var gate = Gates.GetOrAdd(acc.Id ?? "", _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (acc.Type == AccountType.Microsoft) await MicrosoftAuth.RefreshAsync(acc, ct).ConfigureAwait(false);
                else await YggdrasilAuth.RefreshAsync(acc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is AuthException) && !(ex is OperationCanceledException))
            {
                throw new AuthException("刷新登录失败：" + AccountUtil.Short(ex), false, ex);
            }
            finally { gate.Release(); }
            Skins.Invalidate(acc);
            if (AccountStore.Find(acc.Id) != null) AccountStore.AddOrUpdate(acc);
        }

        /// <summary>网络层面的失败（可以先用缓存令牌顶着），而不是服务器明确拒绝</summary>
        public static bool IsTransient(Exception ex)
        {
            if (ex is AuthException ae) return !ae.NeedsRelogin && ae.InnerException != null && AccountUtil.IsNetworkError(ae.InnerException);
            return AccountUtil.IsNetworkError(ex);
        }

        static void Touch(Account acc)
        {
            acc.LastUsed = DateTime.UtcNow;
            // 只有已保存的账户才写回；临时账户（还没添加进列表的）不自动保存
            if (AccountStore.Find(acc.Id) != null) AccountStore.AddOrUpdate(acc);
        }

        static void SetDetail(LauncherTask task, string text)
        {
            if (task != null) task.Detail = text;
        }
    }
}
