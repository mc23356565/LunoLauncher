using System;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    public class InstallRequest
    {
        public GameRepo Repo { get; set; }
        /// <summary>原版版本号，如 1.20.1 / 26.3</summary>
        public string McVersion { get; set; }
        public LoaderType Loader { get; set; } = LoaderType.None;
        /// <summary>加载器版本：Forge "47.2.0"、NeoForge "21.1.77"、Fabric/Quilt 加载器版本</summary>
        public string LoaderVersion { get; set; }
        /// <summary>可选：同时安装 OptiFine（如 "HD_U_I6"）</summary>
        public string OptiFineVersion { get; set; }
        /// <summary>版本文件夹名；为空时自动生成</summary>
        public string Name { get; set; }
    }

    public static partial class Installer
    {
        /// <summary>
        /// 安装一个版本（原版 + 可选加载器），返回最终的版本名（versions 下的文件夹名）。
        /// 生成的版本 JSON 是合并后的独立 JSON（不依赖 inheritsFrom），版本文件夹可以随意改名、拷走。
        /// </summary>
        public static Task<string> InstallAsync(InstallRequest req, LauncherTask task, double from = 0, double to = 1)
            => InstallCoreAsync(req, task, from, to);
    }
}
