using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LunoLauncher.Core;

namespace LunoLauncher.Game
{
    public class OptiFineEntry
    {
        public string Mc { get; set; }
        /// <summary>HD_U_I6</summary>
        public string Name { get; set; }
        public string FileName { get; set; }
        /// <summary>兼容的 Forge 版本（"47.2.18"），null 表示不兼容 Forge</summary>
        public string Forge { get; set; }
        public bool Preview { get; set; }
        public string Version => Name.Replace('_', ' ') + (Preview ? "（预览版）" : "");
        public string Tag => Preview ? "预览版" : Forge != null ? "兼容 Forge " + Forge : "";
        public override string ToString() => Version;

        /// <summary>落盘用的安全文件名（文件名来自网页 / 第三方接口，不能直接拼路径）</summary>
        public string SafeFileName
        {
            get
            {
                var n = Util.SafeFileName(System.IO.Path.GetFileName(FileName ?? ""));
                return n.Length == 0 || n == "." || n == ".." ? "OptiFine.jar" : n;
            }
        }
    }

    /// <summary>
    /// OptiFine：版本列表优先解析 optifine.net 官网（失败再用 BMCLAPI）。
    /// 独立安装不运行它的图形安装器，而是调用包里的 optifine.Patcher 生成补丁库，再用 launchwrapper 启动；
    /// 和 Forge 一起用时直接作为 Mod 放进 mods。
    /// </summary>
    public static class OptiFine
    {
        public static async Task<List<OptiFineEntry>> ListAsync(string mc, CancellationToken ct = default)
        {
            try { return await ListOfficialAsync(mc, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn("OptiFine 官网列表获取失败，改用 BMCLAPI：" + ex.Message);
                return await ListBmclAsync(mc, ct).ConfigureAwait(false);
            }
        }

        static async Task<List<OptiFineEntry>> ListOfficialAsync(string mc, CancellationToken ct)
        {
            var html = await Http.GetStringAsync("https://optifine.net/downloads", ct, mirror: false, timeoutSec: 25).ConfigureAwait(false);
            var list = new List<OptiFineEntry>();
            // 按 <h2>Minecraft x</h2> 分段
            var sections = Regex.Split(html, @"<h2>Minecraft ");
            foreach (var sec in sections.Skip(1))
            {
                var ver = sec.Substring(0, sec.IndexOf('<')).Trim();
                if (ver != mc) continue;
                foreach (Match row in Regex.Matches(sec, @"<tr class='downloadLine[^']*'>(.*?)</tr>", RegexOptions.Singleline))
                {
                    var r = row.Groups[1].Value;
                    var file = Regex.Match(r, @"adloadx\?f=([^""'&]+)").Groups[1].Value;
                    if (string.IsNullOrEmpty(file)) continue;
                    file = WebUtility.HtmlDecode(file);
                    var forge = Regex.Match(r, @"<td class='colForge'>([^<]*)</td>").Groups[1].Value.Trim();
                    forge = forge.StartsWith("Forge ") ? forge.Substring(6).Trim() : null;
                    if (forge == "N/A" || string.IsNullOrEmpty(forge)) forge = null;
                    var name = Regex.Match(file, @"OptiFine_" + Regex.Escape(mc) + @"_(.+)\.jar$").Groups[1].Value;
                    if (string.IsNullOrEmpty(name)) continue;
                    list.Add(new OptiFineEntry { Mc = mc, Name = name, FileName = file, Forge = forge, Preview = file.StartsWith("preview_", StringComparison.OrdinalIgnoreCase) });
                }
            }
            return list.GroupBy(e => e.FileName).Select(g => g.First()).ToList();
        }

        static async Task<List<OptiFineEntry>> ListBmclAsync(string mc, CancellationToken ct)
        {
            var json = await Http.GetJsonAsync("https://bmclapi2.bangbang93.com/optifine/" + Uri.EscapeDataString(mc), ct, mirror: false).ConfigureAwait(false);
            return json.Select(n => new OptiFineEntry
            {
                Mc = mc,
                Name = n["type"].Str + "_" + n["patch"].Str,
                FileName = n["filename"].Str,
                Forge = n["forge"].Str?.Replace("Forge ", "").Trim() is string f && f.Length > 0 && f != "N/A" ? f : null,
                Preview = (n["patch"].Str ?? "").StartsWith("pre", StringComparison.OrdinalIgnoreCase),
            }).Reverse().ToList();
        }

        /// <summary>下载 OptiFine jar：官网镜像页 → downloadx 链接；失败用 BMCLAPI</summary>
        public static async Task DownloadAsync(OptiFineEntry e, string dest, LauncherTask task, double from, double to)
        {
            var ct = task?.Token ?? default;
            var urls = new List<string>();
            try
            {
                var page = await Http.GetStringAsync("https://optifine.net/adloadx?f=" + Uri.EscapeDataString(e.FileName), ct, mirror: false, timeoutSec: 20).ConfigureAwait(false);
                var link = Regex.Match(page, @"downloadx\?f=[^'""]+").Value;
                if (!string.IsNullOrEmpty(link)) urls.Add("https://optifine.net/" + WebUtility.HtmlDecode(link));
            }
            catch (Exception ex) { Log.Warn("OptiFine 官网下载页获取失败：" + ex.Message); }
            var parts = e.Name.Split('_');
            if (parts.Length >= 3)
                urls.Add($"https://bmclapi2.bangbang93.com/optifine/{e.Mc}/{string.Join("_", parts.Take(parts.Length - 1))}/{parts.Last()}");
            if (urls.Count == 0) throw new Exception("拿不到 OptiFine 的下载地址");
            var item = new DownloadItem { Url = urls[0], AltUrls = urls.Skip(1).ToList(), Path = dest, UseMirror = false };
            await Downloader.DownloadFileAsync(item, task, from, to).ConfigureAwait(false);
            using (var z = ZipFile.OpenRead(dest))
                if (z.GetEntry("optifine/Patcher.class") == null && z.GetEntry("Config.class") == null && z.GetEntry("net/optifine/Config.class") == null)
                    throw new InvalidDataException("下载到的文件不是 OptiFine（可能是广告页），请稍后重试");
        }

        /// <summary>独立安装：生成补丁库，改写版本 JSON（调用方负责保存 JSON）</summary>
        public static async Task InstallStandaloneAsync(GameRepo repo, string versionId, JNode json, OptiFineEntry e, LauncherTask task, double from, double to)
        {
            var ct = task?.Token ?? default;
            var ofJar = Path.Combine(Paths.Temp, e.SafeFileName);
            await DownloadAsync(e, ofJar, task, from, from + (to - from) * 0.5).ConfigureAwait(false);

            var libVer = e.Mc + "_" + e.Name;
            var libName = $"optifine:OptiFine:{libVer}";
            var libPath = repo.LibraryPath(libName);
            Util.EnsureParent(libPath);

            // launchwrapper：新版 OptiFine 自带 launchwrapper-of，老版本用 Mojang 的 launchwrapper 1.12
            string lwName = "net.minecraft:launchwrapper:1.12";
            JNode lwLib = JNode.Obj().Set("name", lwName);
            using (var z = ZipFile.OpenRead(ofJar))
            {
                var lwTxt = Util.ReadZipText(z, "launchwrapper-of.txt")?.Trim();
                if (!string.IsNullOrEmpty(lwTxt))
                {
                    lwName = $"optifine:launchwrapper-of:{lwTxt}";
                    var lwPath = repo.LibraryPath(lwName);
                    Util.EnsureParent(lwPath);
                    z.GetEntry($"launchwrapper-of-{lwTxt}.jar")?.ExtractToFile(lwPath, true);
                    lwLib = Generated(lwName, repo);
                }
            }

            task?.Report(from + (to - from) * 0.6, "生成 OptiFine 补丁…");
            var info = VersionInfo.FromJson(versionId, json);
            var java = await JavaManager.EnsureAsync(info.JavaMajor, info.JavaComponent, false, task, from + (to - from) * 0.6, from + (to - from) * 0.7).ConfigureAwait(false);
            if (java == null) throw new Exception("没有可用的 Java，无法安装 OptiFine");
            var javaExe = Path.Combine(Path.GetDirectoryName(java.JavawPath), "java.exe");
            if (!File.Exists(javaExe)) javaExe = java.JavawPath;
            var (code, output) = await RunJava(javaExe, new List<string> { "-cp", ofJar, "optifine.Patcher", repo.VersionJar(versionId), ofJar, libPath }, repo.Root, ct).ConfigureAwait(false);
            if (code != 0 || !File.Exists(libPath))
                throw new Exception("OptiFine 补丁生成失败：\n" + string.Join("\n", output.Split('\n').Reverse().Take(10).Reverse()));

            // 改写版本 JSON
            var libs = JNode.Arr();
            libs.Add(Generated(libName, repo));
            libs.Add(lwLib);
            foreach (var l in json["libraries"]) libs.Add(l);
            json.Set("libraries", libs);
            json.Set("mainClass", "net.minecraft.launchwrapper.Launch");
            if (json["arguments"]["game"].IsArr)
            {
                json["arguments"]["game"].Add("--tweakClass");
                json["arguments"]["game"].Add("optifine.OptiFineTweaker");
            }
            else json.Set("minecraftArguments", (json["minecraftArguments"].Str ?? "") + " --tweakClass optifine.OptiFineTweaker");
            Util.TryDelete(ofJar);
            task?.Report(to);
        }

        /// <summary>本地生成、没有下载地址的库</summary>
        static JNode Generated(string name, GameRepo repo)
        {
            var m = Maven.Parse(name);
            return JNode.Obj().Set("name", name).Set("downloads", JNode.Obj().Set("artifact",
                JNode.Obj().Set("path", m.RelPath).Set("url", "").Set("size", new FileInfo(repo.LibraryPath(name)).Exists ? new FileInfo(repo.LibraryPath(name)).Length : 0)));
        }

        static Task<(int, string)> RunJava(string java, List<string> args, string dir, CancellationToken ct)
            => Installer.RunJavaPublicAsync(java, args, dir, ct);
    }
}
