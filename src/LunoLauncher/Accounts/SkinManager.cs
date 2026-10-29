using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LunoLauncher.Core;

namespace LunoLauncher.Accounts
{
    public enum SkinModel
    {
        /// <summary>经典手臂（Steve）</summary>
        Classic = 0,
        /// <summary>细手臂（Alex）</summary>
        Slim = 1,
    }

    /// <summary>
    /// 换皮肤：走微软官方的 Minecraft Services 接口，和官方启动器是同一个。
    /// 披风没有对应接口 —— 第三方启动器只能显示账号已有的披风，不能设置。
    /// </summary>
    public static class SkinManager
    {
        const string SkinsUrl = "https://api.minecraftservices.com/minecraft/profile/skins";
        const string ResetUrl = "https://api.minecraftservices.com/minecraft/profile/skins/active";

        /// <summary>PNG 尺寸/格式校验。可以用返回 null，否则返回给用户看的错误说明。</summary>
        public static string Validate(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png == null || png.Length < 64) return "文件是空的。";
            if (png.Length > 4 * 1024 * 1024) return "图片太大了（超过 4 MB），皮肤文件一般只有几十 KB。";
            if (!(png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47)) return "这不是 PNG 图片。";
            // IHDR 的宽高在 16..23 字节
            width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            if (!((width == 64 && height == 64) || (width == 64 && height == 32)))
                return "尺寸是 " + width + "×" + height + "，必须是 64×64（新版）或 64×32（旧版）。";
            return null;
        }

        /// <summary>自动判断手臂模型：新版皮肤里手臂区域带透明像素就是细手臂。</summary>
        public static SkinModel DetectModel(byte[] png)
        {
            try
            {
                var bmp = Decode(png);
                if (bmp == null || bmp.PixelWidth != 64 || bmp.PixelHeight != 64) return SkinModel.Classic;
                BitmapSource src = bmp;
                if (bmp.Format != PixelFormats.Bgra32)
                    src = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                var px = new byte[4];
                src.CopyPixels(new Int32Rect(46, 26, 1, 1), px, 4, 0);
                return px[3] == 0 ? SkinModel.Slim : SkinModel.Classic;
            }
            catch { return SkinModel.Classic; }
        }

        /// <summary>解码成位图（不缩放；界面用最近邻放大，保持像素感）。</summary>
        public static BitmapImage Decode(byte[] png)
        {
            try
            {
                if (png == null || png.Length == 0) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>上传皮肤。失败会抛异常，请用 Explain() 转成人话。</summary>
        public static async Task UploadAsync(Account acc, byte[] png, SkinModel model, CancellationToken ct = default)
        {
            var token = await TokenAsync(acc, ct).ConfigureAwait(false);
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
            var fields = new Dictionary<string, string> { ["variant"] = model == SkinModel.Slim ? "slim" : "classic" };
            await Http.UploadFileAsync(SkinsUrl, png, "skin.png", "image/png", fields, headers, ct, 90).ConfigureAwait(false);
            Skins.Invalidate(acc);
        }

        /// <summary>恢复默认皮肤。</summary>
        public static async Task ResetAsync(Account acc, CancellationToken ct = default)
        {
            var token = await TokenAsync(acc, ct).ConfigureAwait(false);
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
            await Http.DeleteAsync(ResetUrl, headers, ct, 60).ConfigureAwait(false);
            Skins.Invalidate(acc);
        }

        static async Task<string> TokenAsync(Account acc, CancellationToken ct)
        {
            if (acc == null) throw new AuthException("没有账号。");
            if (acc.Type != AccountType.Microsoft)
                throw new AuthException("换皮肤只支持微软正版账号。");
            if (string.IsNullOrEmpty(acc.AccessToken) || acc.IsExpired)
                await AccountService.RefreshAsync(acc, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(acc.AccessToken))
                throw new AuthException("登录状态已失效，请在账号管理里重新登录。", true);
            return acc.AccessToken;
        }

        /// <summary>把异常翻译成用户能看懂的话。</summary>
        public static string Explain(Exception ex)
        {
            if (ex is HttpStatusException hs)
            {
                if (hs.Status == 401) return "登录状态失效了，请在账号管理里重新登录微软账号。\n\n接口返回：" + hs.Body;
                if (hs.Status == 403) return "这个账号没有换皮肤的权限（可能不是正版账号）。\n\n接口返回：" + hs.Body;
                if (hs.Status == 429) return "请求太频繁了，等一会儿再试。";
                return "接口返回 " + hs.Status + "：\n\n" + hs.Body;
            }
            if (ex is AuthException ae) return ae.Message;
            if (ex is TimeoutException) return "网络超时了，检查一下代理或稍后再试。";
            return ex.Message;
        }
    }
}
