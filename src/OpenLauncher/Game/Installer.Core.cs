using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    public static partial class Installer
    {
        public static string DefaultName(string mc, LoaderType loader, string loaderVersion)
        {
            if (loader == LoaderType.None || string.IsNullOrEmpty(loaderVersion)) return mc;
            return $"{mc}-{LoaderInfo.Name(loader)}_{loaderVersion}";
        }

        static async Task<string> InstallCoreAsync(InstallRequest req, LauncherTask task, double from, double to)
        {
            var repo = req.Repo ?? GameRepo.Current;
            var ct = task?.Token ?? default;
            double P(double x) => from + (to - from) * x;

            Directory.CreateDirectory(repo.Versions);
            repo.EnsureLauncherProfiles();
            var name = string.IsNullOrWhiteSpace(req.Name)
                ? DefaultName(req.McVersion, req.Loader, req.LoaderVersion) + (string.IsNullOrEmpty(req.OptiFineVersion) ? "" : "-OptiFine_" + req.OptiFineVersion)
                : req.Name.Trim();
            name = Util.SafeFileName(name);
            if (repo.HasVersion(name)) throw new Exception($"版本名 “{name}” 已存在，换个名字吧");
            var dir = repo.VersionDir(name);
            bool dirExisted = Directory.Exists(dir); // 可能是缺了 json 的旧版本，里面有存档，失败时不能删
            Log.Info($"开始安装 {name}：MC {req.McVersion} + {req.Loader} {req.LoaderVersion}");

            try
            {
                // 1. 原版 JSON
                task?.Report(P(0.01), $"获取 Minecraft {req.McVersion} 的版本信息…");
                var entry = await VersionList.FindAsync(req.McVersion, ct).ConfigureAwait(false);
                var vanillaText = await Http.GetStringAsync(entry.Url, ct).ConfigureAwait(false);
                var vanilla = JNode.Parse(vanillaText);
                Directory.CreateDirectory(dir);

                // 2. 加载器
                JNode merged = vanilla.Clone();
                ForgeContext forge = null;
                switch (req.Loader)
                {
                    case LoaderType.Fabric:
                    case LoaderType.Quilt:
                        {
                            task?.Report(P(0.03), $"获取 {LoaderInfo.Name(req.Loader)} 配置…");
                            var url = req.Loader == LoaderType.Fabric
                                ? $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(req.McVersion)}/{Uri.EscapeDataString(req.LoaderVersion)}/profile/json"
                                : $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(req.McVersion)}/{Uri.EscapeDataString(req.LoaderVersion)}/profile/json";
                            var profile = await Http.GetJsonAsync(url, ct).ConfigureAwait(false);
                            merged = VersionInfo.Merge(profile, vanilla);
                            break;
                        }
                    case LoaderType.Forge:
                    case LoaderType.NeoForge:
                        {
                            forge = await PrepareForgeAsync(repo, req, task, P(0.03), P(0.12)).ConfigureAwait(false);
                            merged = VersionInfo.Merge(forge.VersionJson, vanilla);
                            break;
                        }
                    case LoaderType.None:
                        break;
                    default:
                        throw new NotSupportedException($"暂不支持直接安装 {req.Loader}");
                }

                merged.Remove("inheritsFrom");
                merged.Remove("jar");
                merged.Set("id", name);
                var marker = JNode.Obj().Set("mc", req.McVersion).Set("installedBy", Branding.AppName + " " + AppInfo.VersionString)
                    .Set("installTime", DateTime.Now.ToString("s"));
                var loaders = JNode.Arr();
                if (req.Loader != LoaderType.None) loaders.Add(JNode.Obj().Set("type", req.Loader.ToString()).Set("version", req.LoaderVersion));
                if (!string.IsNullOrEmpty(req.OptiFineVersion))
                {
                    if (req.Loader != LoaderType.None && req.Loader != LoaderType.Forge)
                        throw new NotSupportedException("OptiFine 只能和原版或 Forge 一起安装（Fabric 请用 Sodium + Iris）");
                    loaders.Add(JNode.Obj().Set("type", LoaderType.OptiFine.ToString()).Set("version", req.OptiFineVersion));
                }
                marker.Set("loaders", loaders);
                merged.Set("openlauncher", marker);

                // 3. 下载文件
                var info = VersionInfo.FromJson(name, merged);
                var extraLibs = forge?.ProfileLibraries(repo) ?? new System.Collections.Generic.List<DownloadItem>();
                if (extraLibs.Count > 0)
                    await Downloader.RunAsync(extraLibs, task, P(0.12), P(0.2), "下载安装器依赖").ConfigureAwait(false);
                await GameFiles.CompleteAsync(repo, info, task, P(0.2), forge != null ? P(0.8) : P(0.9)).ConfigureAwait(false);

                // 4. Forge / NeoForge 安装器处理器（生成补丁后的 jar）
                if (forge != null)
                    await RunProcessorsAsync(repo, forge, info, req.McVersion, task, P(0.8), P(0.9)).ConfigureAwait(false);

                // 5. OptiFine：原版上独立安装（改写 JSON），Forge 上作为 Mod
                OptiFineEntry of = null;
                if (!string.IsNullOrEmpty(req.OptiFineVersion))
                {
                    task?.Report(P(0.9), "获取 OptiFine 信息…");
                    var ofList = await OptiFine.ListAsync(req.McVersion, ct).ConfigureAwait(false);
                    of = ofList.FirstOrDefault(e => e.Name == req.OptiFineVersion || e.FileName == req.OptiFineVersion)
                         ?? throw new Exception($"找不到 OptiFine {req.OptiFineVersion}");
                    if (req.Loader == LoaderType.None)
                        await OptiFine.InstallStandaloneAsync(repo, name, merged, of, task, P(0.9), P(0.99)).ConfigureAwait(false);
                }

                // 6. 最后再写 JSON：中途失败不会留下一个"看起来装好了"的坏版本
                merged.SaveFile(repo.VersionJson(name));
                if (of != null && req.Loader == LoaderType.Forge)
                {
                    var inst = new Instance(repo, name);
                    Directory.CreateDirectory(inst.ModsDir);
                    await OptiFine.DownloadAsync(of, Path.Combine(inst.ModsDir, of.SafeFileName), task, P(0.9), P(0.99)).ConfigureAwait(false);
                }
                forge?.Dispose();
                task?.Report(to, "安装完成");
                Log.Info($"安装完成 {name}");
                return name;
            }
            catch
            {
                // 安装失败：删掉半成品（库文件留着，下次能复用）
                if (!repo.HasVersion(name) && !dirExisted) Util.TryDeleteDir(dir);
                throw;
            }
        }
    }
}
