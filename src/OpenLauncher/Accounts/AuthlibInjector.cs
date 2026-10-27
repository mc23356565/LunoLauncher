using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Accounts
{
    /// <summary>
    /// authlib-injector：外置登录启动游戏时挂到 JVM 上的 Java agent。
    /// jar 放在 Paths.Tools 下，文件名带版本号（游戏运行时旧 jar 被占用也不影响更新）。
    /// 元数据 / 下载都走 Http / Downloader，镜像（BMCLAPI）自动生效。
    /// </summary>
    public static class AuthlibInjector
    {
        const string LatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
        const string FilePrefix = "authlib-injector";
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        static string _cachedJar;
        static DateTime _cachedAt;

        /// <summary>
        /// 确保本地有可用的 authlib-injector jar 并返回路径。
        /// 每次进程运行最多 12 小时检查一次新版本；查询失败（离线）时直接用本地已有的 jar。
        /// </summary>
        public static async Task<string> EnsureJarAsync(LauncherTask task, CancellationToken ct)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cachedJar != null && File.Exists(_cachedJar) && DateTime.UtcNow - _cachedAt < TimeSpan.FromHours(12)) return _cachedJar;

                JNode latest = null;
                Exception metaError = null;
                try
                {
                    SetDetail(task, "检查 authlib-injector 版本…");
                    latest = await Http.GetJsonAsync(LatestUrl, ct, null, mirror: true, timeoutSec: 15).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    metaError = ex;
                    Log.Warn("获取 authlib-injector 版本信息失败：" + AccountUtil.Short(ex));
                }

                var version = latest?["version"].Str;
                var url = latest?["download_url"].Str;
                var sha256 = latest?["checksums"]["sha256"].Str;
                if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url))
                {
                    var local = FindLocal();
                    if (local != null)
                    {
                        Log.Info("使用本地已有的 authlib-injector：" + Path.GetFileName(local));
                        return Remember(local);
                    }
                    var reason = metaError != null ? AccountUtil.Short(metaError) : "版本信息不完整";
                    throw new AuthException("无法获取 authlib-injector（外置登录必需）：" + reason, false, metaError);
                }

                var path = Path.Combine(Paths.Tools, $"{FilePrefix}-{Util.SafeFileName(version)}.jar");
                if (File.Exists(path) && Matches(path, sha256)) return Remember(path);
                Util.TryDelete(path);

                // 先按镜像设置下载，校验不过再强制走官方源
                foreach (var useMirror in new[] { true, false })
                {
                    try
                    {
                        var item = new DownloadItem(url, path) { UseMirror = useMirror };
                        var p = task?.Progress ?? -1;
                        await Downloader.RunAsync(new[] { item }, task ?? Detached(ct), p, p, "下载 authlib-injector").ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        Log.Warn($"下载 authlib-injector 失败（{(useMirror ? "镜像优先" : "官方源")}）：{AccountUtil.Short(ex)}");
                        Util.TryDelete(path);
                        continue;
                    }
                    if (Matches(path, sha256))
                    {
                        Log.Info($"authlib-injector {version} 已就绪");
                        CleanupOld(path);
                        return Remember(path);
                    }
                    Log.Warn("authlib-injector 校验失败，" + (useMirror ? "改用官方源重新下载" : "放弃"));
                    Util.TryDelete(path);
                }

                var fallback = FindLocal();
                if (fallback != null)
                {
                    Log.Warn("下载新版 authlib-injector 失败，继续使用本地旧版：" + Path.GetFileName(fallback));
                    return Remember(fallback);
                }
                throw new AuthException("下载 authlib-injector 失败，请检查网络后重试（外置登录必需）");
            }
            finally { Gate.Release(); }
        }

        /// <summary>
        /// 外置登录需要追加的 JVM 参数：
        /// -javaagent:jar=api、-Dauthlibinjector.side=client、-Dauthlibinjector.yggdrasil.prefetched=元数据的 Base64（取不到元数据时省略，由 agent 自己去取）。
        /// </summary>
        public static async Task<List<string>> JvmArgsAsync(string api, LauncherTask task, CancellationToken ct)
        {
            api = (api ?? "").TrimEnd('/');
            if (string.IsNullOrEmpty(api)) throw new AuthException("外置登录账户缺少认证服务器地址，请删除后重新添加");
            var jar = await EnsureJarAsync(task, ct).ConfigureAwait(false);
            var args = new List<string>
            {
                $"-javaagent:{jar}={api}",
                "-Dauthlibinjector.side=client",
            };
            try
            {
                var meta = await YggdrasilAuth.GetMetadataAsync(api, ct).ConfigureAwait(false);
                args.Add("-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(meta.ToJson(false))));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                Log.Warn("预取外置登录服务器元数据失败，将由 authlib-injector 自行获取：" + AccountUtil.Short(ex));
            }
            return args;
        }

        static string Remember(string path)
        {
            _cachedJar = path;
            _cachedAt = DateTime.UtcNow;
            return path;
        }

        static bool Matches(string path, string sha256)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
                if (string.IsNullOrEmpty(sha256)) return LooksLikeJar(path);
                return string.Equals(Util.Sha256File(path), sha256.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static bool LooksLikeJar(string path)
        {
            using (var fs = File.OpenRead(path))
                return fs.Length > 4 && fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
        }

        static readonly Regex VersionRe = new Regex(@"^authlib-injector-(.+)\.jar$", RegexOptions.IgnoreCase);

        /// <summary>本地已有的 jar 里版本最高的一个</summary>
        static string FindLocal()
        {
            try
            {
                return Directory.GetFiles(Paths.Tools, FilePrefix + "*.jar")
                    .Where(f => new FileInfo(f).Length > 0 && LooksLikeJar(f))
                    .OrderByDescending(f => VersionRe.Match(Path.GetFileName(f)) is var m && m.Success ? m.Groups[1].Value : "0", Comparer<string>.Create(Util.CompareVersion))
                    .ThenByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        static void CleanupOld(string keep)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Paths.Tools, FilePrefix + "*.jar"))
                    if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                        Util.TryDelete(f); // 正在被游戏占用的会删除失败，下次再说
            }
            catch { }
        }

        static void SetDetail(LauncherTask task, string text)
        {
            if (task != null) task.Detail = text;
        }

        /// <summary>调用方没给任务对象时，临时造一个只用来传递取消信号的</summary>
        static LauncherTask Detached(CancellationToken ct)
        {
            var t = new LauncherTask { Title = "authlib-injector" };
            if (ct.CanBeCanceled) ct.Register(t.Cancel);
            return t;
        }
    }
}
