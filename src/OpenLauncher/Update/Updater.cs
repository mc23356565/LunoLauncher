using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Update
{
    public class UpdateInfo
    {
        public string Version { get; set; }
        public string Notes { get; set; }
        public string Published { get; set; }
        public string FileName { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        public List<string> Urls { get; set; } = new List<string>();
        public string Signer { get; set; }
        public string FoundAt { get; set; }
        public bool IsNewer => Util.CompareVersion(Version, AppInfo.VersionString) > 0;
    }

    /// <summary>
    /// 去中心化自更新：
    /// 1. 从多个地址（GitHub / jsDelivr / Gitee / 自建镜像 / 用户自己加的）并行拉取签名清单；
    /// 2. 只认被信任公钥签过名的清单，取其中版本最高的；
    /// 3. 新版 exe 可以从清单里任意一个地址下载，SHA-256 对得上就替换自身。
    /// 没有任何一个服务器是必需的；原维护者跑路后，社区换一把新钥匙继续发版，用户手动信任即可。
    /// </summary>
    public static class Updater
    {
        public static List<string> Sources
        {
            get
            {
                var list = new List<string>();
                list.AddRange(Settings.ExtraList("update.sources"));
                list.AddRange(Settings.ExtraList("update.remoteSources"));
                list.AddRange(Branding.DefaultUpdateSources);
                return list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public static HashSet<string> Revoked => new HashSet<string>(Settings.ExtraList("update.revoked"), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 信任的公钥：内置 + 用户手动添加 + 被“仍受信任的钥匙”签名轮换进来的；去掉被吊销的。
        /// 轮换进来的钥匙记录了签发者，签发者被吊销后它也随之失效。
        /// </summary>
        public static List<string> TrustedKeys
        {
            get
            {
                var revoked = Revoked;
                var trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 指纹 → XML
                bool add(string xml)
                {
                    if (string.IsNullOrWhiteSpace(xml) || xml.Contains("@@")) return false;
                    try
                    {
                        var fp = UpdateCrypto.Fingerprint(xml);
                        if (revoked.Contains(fp) || trusted.ContainsKey(fp)) return false;
                        trusted[fp] = xml;
                        return true;
                    }
                    catch { return false; }
                }
                foreach (var k in Branding.TrustedKeys) add(k);
                foreach (var k in Settings.ExtraList("update.userKeys")) add(k);
                var rotated = Settings.GetNode("update.rotated");
                for (bool changed = true; changed;)
                {
                    changed = false;
                    foreach (var r in rotated)
                        if (r["issuer"].Str != null && trusted.ContainsKey(r["issuer"].Str) && add(r["key"].Str)) changed = true;
                }
                return trusted.Values.ToList();
            }
        }

        public static string LastCheckMessage { get; private set; }

        /// <summary>检查更新。返回最新的有效清单（即使不比当前新也返回，便于显示"已是最新"）；全部失败返回 null。</summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            var keys = TrustedKeys;
            if (keys.Count == 0)
            {
                LastCheckMessage = "这个版本没有内置发布公钥，无法验证更新。请在 设置 → 更新 里添加你信任的发布者公钥。";
                return null;
            }
            var sources = Sources;
            async Task<(JNode payload, string signer, string src)?> Fetch(string src)
            {
                try
                {
                    var text = await Http.GetStringAsync(src + (src.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute, ct, mirror: false, timeoutSec: 15).ConfigureAwait(false);
                    var opened = UpdateCrypto.Open(JNode.Parse(text), keys);
                    if (opened == null) { Log.Warn($"更新源 {src} 的清单签名无效，已忽略"); return null; }
                    return (opened.Value.payload, opened.Value.signer, src);
                }
                catch (Exception ex) { Log.Warn($"更新源 {src} 不可用：{ex.Message}"); return null; }
            }
            var tasks = sources.Select(Fetch).ToList();
            var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).Where(r => r != null).Select(r => r.Value).ToList();
            if (results.Count == 0)
            {
                LastCheckMessage = $"{sources.Count} 个更新源都没拿到有效清单（网络问题，或还没有发布过版本）";
                return null;
            }

            // 先应用所有有效清单里的吊销（不只是版本最高的那份：泄露的旧钥匙可能签一个超高版本号来压过吊销），
            // 再丢掉签名者已被吊销的结果。两把钥匙互相吊销时宁可停更，也不放过可疑更新。
            var newRevokes = results.SelectMany(r => r.payload["keys"]["revoke"].StrList()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (newRevokes.Count > 0)
            {
                var revokedList = Settings.ExtraList("update.revoked");
                revokedList.AddRange(newRevokes.Where(r => !revokedList.Contains(r, StringComparer.OrdinalIgnoreCase)));
                Settings.SetList("update.revoked", revokedList);
            }
            var revoked = Revoked;
            results = results.Where(r => !revoked.Contains(r.signer)).ToList();
            if (results.Count == 0)
            {
                LastCheckMessage = "拿到的更新清单都由已吊销的钥匙签名，已全部忽略";
                return null;
            }

            var best = results.Where(r => r.payload["app"].Str == null || r.payload["app"].Str == Branding.AppName)
                              .OrderByDescending(r => r.payload["version"].Str, Comparer<string>.Create(Util.CompareVersion)).FirstOrDefault();
            if (best.payload == null) return null;
            ApplyRemoteConfig(best.payload, best.signer);

            var f = best.payload["file"];
            var info = new UpdateInfo
            {
                Version = best.payload["version"].Str,
                Notes = best.payload["notes"].Str,
                Published = best.payload["published"].Str,
                FileName = f["name"].StrOr(Branding.AppName + ".exe"),
                Size = f["size"].Long,
                Sha256 = f["sha256"].Str,
                Urls = f["urls"].StrList(),
                Signer = best.signer,
                FoundAt = best.src,
            };
            LastCheckMessage = info.IsNewer ? $"发现新版本 {info.Version}" : $"已是最新版本（{AppInfo.VersionString}）";
            return info;
        }

        /// <summary>签名清单里可以顺带下发：新镜像表、新更新源、钥匙轮换。只有签名有效的清单才会走到这里。</summary>
        static void ApplyRemoteConfig(JNode payload, string signer)
        {
            try
            {
                var cfg = payload["config"];
                if (cfg["mirrors"].IsArr) { Settings.Set("mirror.remote", cfg["mirrors"].Clone()); Mirrors.Reload(); }
                if (cfg["updateSources"].IsArr) Settings.SetList("update.remoteSources", cfg["updateSources"].StrList());
                if (cfg["zhNames"].IsObj) Settings.Set("content.remoteZhNames", cfg["zhNames"].Clone());

                // 钥匙轮换：记录是谁签发的，签发者被吊销时这把钥匙一起失效（吊销已在 CheckAsync 里处理）
                var add = payload["keys"]["add"].StrList();
                if (add.Count > 0)
                {
                    var rotated = Settings.GetNode("update.rotated").IsArr ? Settings.GetNode("update.rotated").Clone() : JNode.Arr();
                    foreach (var k in add)
                        if (!rotated.Any(r => r["key"].Str == k))
                            rotated.Add(JNode.Obj().Set("key", k).Set("issuer", signer));
                    Settings.Set("update.rotated", rotated);
                }
            }
            catch (Exception ex) { Log.Warn("应用远程配置失败：" + ex.Message); }
        }

        /// <summary>下载新版本并替换自身，成功后重启。</summary>
        public static async Task DownloadAndApplyAsync(UpdateInfo info, LauncherTask task)
        {
            if (info.Urls.Count == 0) throw new Exception("清单里没有下载地址");
            if (string.IsNullOrEmpty(info.Sha256)) throw new Exception("清单缺少 SHA-256，拒绝更新");
            var newPath = Paths.ExePath + ".new";
            Util.TryDelete(newPath);
            var item = new DownloadItem { Url = info.Urls[0], AltUrls = info.Urls.Skip(1).ToList(), Path = newPath, Size = info.Size, UseMirror = false };
            await Downloader.DownloadFileAsync(item, task, 0, 0.95).ConfigureAwait(false);
            var actual = Util.Sha256File(newPath);
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Util.TryDelete(newPath);
                throw new Exception("下载的文件 SHA-256 与签名清单不一致，已删除（可能被篡改或下载损坏）");
            }
            task.Report(0.97, "替换程序文件…");
            var oldPath = Paths.ExePath + ".old";
            Util.TryDelete(oldPath);
            // Windows 允许重命名正在运行的 exe
            File.Move(Paths.ExePath, oldPath);
            try { File.Move(newPath, Paths.ExePath); }
            catch { File.Move(oldPath, Paths.ExePath); throw; }
            Log.Info($"已更新到 {info.Version}，准备重启");
            Settings.SaveNow();
            try
            {
                Process.Start(new ProcessStartInfo(Paths.ExePath, "--updated " + AppInfo.VersionString) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                // 新版本被系统拦下（智能应用控制 / 杀毒软件）：换回旧版本，保证启动器还能用
                Log.Error("新版本无法启动，回滚到旧版本", ex);
                try
                {
                    var blocked = Paths.ExePath + ".blocked";
                    Util.TryDelete(blocked);
                    File.Move(Paths.ExePath, blocked);
                    File.Move(oldPath, Paths.ExePath);
                }
                catch (Exception rex) { Log.Error("回滚失败", rex); }
                throw new Exception("新版本被系统拦截无法启动（可能是 Windows 智能应用控制或杀毒软件），已恢复为旧版本。" + ex.Message);
            }
            TaskManager.OnUi(() => System.Windows.Application.Current?.Shutdown());
        }

        /// <summary>启动时清理上次更新留下的旧文件</summary>
        public static void CleanupOld()
        {
            var old = Paths.ExePath + ".old";
            for (int i = 0; i < 5 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(300); }
            }
        }
    }
}
