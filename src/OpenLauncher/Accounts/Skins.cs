using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenLauncher.Core;

namespace OpenLauncher.Accounts
{
    /// <summary>
    /// 玩家头像：取皮肤里的脸（8x8）+ 帽子层，最近邻放大到指定尺寸。
    /// 皮肤 PNG 缓存在 Paths.Cache\skins；联网失败时用上次缓存的皮肤。
    /// 返回的位图已 Freeze，可以在任意线程使用（包括直接给界面）。拿不到时返回 null，不抛异常。
    /// </summary>
    public static class Skins
    {
        const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
        const string MojangSessionUrl = "https://sessionserver.mojang.com/session/minecraft/profile/";

        static readonly ConcurrentDictionary<string, (BitmapSource bmp, DateTime time)> Memory = new ConcurrentDictionary<string, (BitmapSource, DateTime)>();
        static readonly ConcurrentDictionary<string, Task<byte[]>> InFlight = new ConcurrentDictionary<string, Task<byte[]>>();
        static readonly TimeSpan MemoryTtl = TimeSpan.FromMinutes(15);

        static string Dir => Paths.Ensure(Path.Combine(Paths.Cache, "skins"));

        public static async Task<BitmapSource> GetHeadAsync(Account acc, int size = 64)
        {
            if (acc == null || acc.Type == AccountType.Offline || string.IsNullOrEmpty(acc.Uuid)) return null;
            size = size <= 0 ? 64 : Math.Min(size, 512);
            var key = AccountKey(acc);
            var memKey = key + "@" + size;
            if (Memory.TryGetValue(memKey, out var hit) && DateTime.UtcNow - hit.time < MemoryTtl) return hit.bmp;

            try
            {
                // 同一个账户同时被多处请求时只下载一次
                var task = InFlight.GetOrAdd(key, _ => Task.Run(() => LoadSkinBytesAsync(acc, key)));
                byte[] png;
                try { png = await task.ConfigureAwait(false); }
                finally { InFlight.TryRemove(key, out _); }
                if (png == null) return null;
                var bmp = await Task.Run(() => RenderHead(png, size)).ConfigureAwait(false);
                if (bmp != null) Memory[memKey] = (bmp, DateTime.UtcNow);
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Warn($"获取头像失败（{acc.Name}）：{AccountUtil.Short(ex)}");
                return null;
            }
        }

        /// <summary>丢掉内存里的头像缓存（换皮肤后调用）</summary>
        public static void Invalidate(Account acc = null)
        {
            if (acc == null) { Memory.Clear(); return; }
            var prefix = AccountKey(acc) + "@";
            foreach (var k in Memory.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal)) Memory.TryRemove(k, out _);
        }

        static string AccountKey(Account acc)
        {
            var k = acc.Type + "-" + AccountUtil.NormalizeUuid(acc.Uuid);
            if (acc.Type == AccountType.Yggdrasil) k += "-" + Util.Sha256(Encoding.UTF8.GetBytes((acc.ServerUrl ?? "").ToLowerInvariant())).Substring(0, 8);
            return Util.SafeFileName(k);
        }

        // ---------- 取皮肤 ----------
        static async Task<byte[]> LoadSkinBytesAsync(Account acc, string key)
        {
            var lastKnown = Path.Combine(Dir, key + ".png");
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try
                {
                    var url = await ResolveSkinUrlAsync(acc, cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(url))
                    {
                        // 服务器明确说没有皮肤：用默认头像
                        Util.TryDelete(lastKnown);
                        return null;
                    }
                    if (url.StartsWith("http://textures.minecraft.net/", StringComparison.OrdinalIgnoreCase))
                        url = "https://" + url.Substring("http://".Length);
                    var file = Path.Combine(Dir, Util.Sha256(Encoding.UTF8.GetBytes(url)).Substring(0, 24) + ".png");
                    byte[] data;
                    if (File.Exists(file)) data = File.ReadAllBytes(file);
                    else
                    {
                        data = await DownloadAsync(url, cts.Token).ConfigureAwait(false);
                        WriteQuiet(file, data);
                    }
                    WriteQuiet(lastKnown, data);
                    return data;
                }
                catch (Exception ex)
                {
                    if (File.Exists(lastKnown))
                    {
                        Log.Info($"无法在线获取 {acc.Name} 的皮肤（{AccountUtil.Short(ex)}），使用缓存");
                        return File.ReadAllBytes(lastKnown);
                    }
                    throw;
                }
            }
        }

        static async Task<string> ResolveSkinUrlAsync(Account acc, CancellationToken ct)
        {
            var uuid = AccountUtil.NormalizeUuid(acc.Uuid);
            if (acc.Type == AccountType.Microsoft)
            {
                if (!string.IsNullOrEmpty(acc.AccessToken) && !acc.IsExpired)
                {
                    try
                    {
                        var p = await Http.GetJsonAsync(McProfileUrl, ct,
                            new Dictionary<string, string> { ["Authorization"] = "Bearer " + acc.AccessToken }, mirror: false, timeoutSec: 15).ConfigureAwait(false);
                        var u = MicrosoftAuth.ActiveSkin(p);
                        if (u != null) return u;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        Log.Warn("从 Minecraft 服务读取皮肤失败，改用会话服务器：" + AccountUtil.Short(ex));
                    }
                }
                try
                {
                    return await TexturesFromSessionAsync(MojangSessionUrl + uuid, ct).ConfigureAwait(false);
                }
                catch (Exception) when (!string.IsNullOrEmpty(acc.SkinUrl)) { return acc.SkinUrl; }
            }
            if (acc.Type == AccountType.Yggdrasil)
            {
                if (string.IsNullOrEmpty(acc.ServerUrl)) return null;
                return await TexturesFromSessionAsync(acc.ServerUrl.TrimEnd('/') + "/sessionserver/session/minecraft/profile/" + uuid, ct).ConfigureAwait(false);
            }
            return null;
        }

        /// <summary>会话服务器档案里 textures 属性（Base64 JSON）中的皮肤地址</summary>
        static async Task<string> TexturesFromSessionAsync(string url, CancellationToken ct)
        {
            var p = await Http.GetJsonAsync(url, ct, null, mirror: false, timeoutSec: 15).ConfigureAwait(false);
            foreach (var prop in p["properties"])
            {
                if (prop["name"].Str != "textures") continue;
                var b64 = prop["value"].Str;
                if (string.IsNullOrEmpty(b64)) continue;
                var tex = JNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                return tex["textures"]["SKIN"]["url"].Str;
            }
            return null;
        }

        static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using (var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) throw new HttpStatusException(url, (int)resp.StatusCode, null);
                    var data = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (data.Length > 4 * 1024 * 1024) throw new InvalidDataException("皮肤文件过大");
                    return data;
                }
            }
        }

        static void WriteQuiet(string path, byte[] data)
        {
            try
            {
                var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex) { Log.Warn("缓存皮肤失败：" + ex.Message); }
        }

        // ---------- 渲染 ----------
        /// <summary>把皮肤 PNG 渲染成 size x size 的头像（脸 + 帽子层），返回已 Freeze 的位图</summary>
        public static BitmapSource RenderHead(byte[] png, int size)
        {
            BitmapSource src;
            using (var ms = new MemoryStream(png))
            {
                var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                src = dec.Frames[0];
            }
            if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w < 64 || w % 64 != 0 || (h != w && h != w / 2)) throw new InvalidDataException($"皮肤尺寸不正确：{w}x{h}");
            int s = w / 64;              // 高清皮肤的倍率
            bool legacy = h == w / 2;    // 1.8 之前的 64x32 格式
            int stride = w * 4;
            var px = new byte[stride * h];
            src.CopyPixels(px, stride, 0);

            int fw = 8 * s;
            var face = new byte[fw * fw * 4];
            var hat = new byte[fw * fw * 4];
            CopyRect(px, stride, 8 * s, 8 * s, fw, face);
            CopyRect(px, stride, 40 * s, 8 * s, fw, hat);

            // 脸是不透明的
            for (int i = 3; i < face.Length; i += 4) face[i] = 255;

            // 旧格式皮肤的帽子层如果完全不透明，游戏里会当作没有帽子（与原版处理一致）
            if (legacy)
            {
                bool anyTransparent = false;
                for (int i = 3; i < hat.Length; i += 4) if (hat[i] < 128) { anyTransparent = true; break; }
                if (!anyTransparent) Array.Clear(hat, 0, hat.Length);
            }

            // 帽子层叠加到脸上（Alpha 混合；Bgra32 是非预乘的）
            for (int i = 0; i < face.Length; i += 4)
            {
                int a = hat[i + 3];
                if (a == 0) continue;
                for (int c = 0; c < 3; c++)
                    face[i + c] = (byte)((hat[i + c] * a + face[i + c] * (255 - a) + 127) / 255);
            }

            // 最近邻放大
            var outPx = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                int sy = y * fw / size;
                for (int x = 0; x < size; x++)
                {
                    int sx = x * fw / size;
                    Buffer.BlockCopy(face, (sy * fw + sx) * 4, outPx, (y * size + x) * 4, 4);
                }
            }
            var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, outPx, size * 4);
            bmp.Freeze();
            return bmp;
        }

        static void CopyRect(byte[] px, int stride, int x0, int y0, int n, byte[] dst)
        {
            for (int y = 0; y < n; y++)
                Buffer.BlockCopy(px, (y0 + y) * stride + x0 * 4, dst, y * n * 4, n * 4);
        }
    }
}
