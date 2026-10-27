using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OpenLauncher.Core;
using OpenLauncher.Update;

namespace OpenLauncher.Core
{
    // UpdateCrypto 依赖 Util.Sha256，这里给一个最小实现，避免把整个启动器的 Util 拉进来
    static class Util
    {
        public static string Sha256(byte[] data)
        {
            using (var a = SHA256.Create()) return string.Concat(a.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }
}

namespace OpenLauncher.Release
{
    static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0) return Help();
            try
            {
                switch (args[0])
                {
                    case "keygen": return KeyGen(args.Skip(1).ToArray());
                    case "publish": return Publish(Opts(args.Skip(1)));
                    case "verify": return Verify(Opts(args.Skip(1)));
                    case "fingerprint": Console.WriteLine(UpdateCrypto.Fingerprint(File.ReadAllText(args[1]))); return 0;
                    default: return Help();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        static int Help()
        {
            Console.WriteLine(@"ol-release —— OpenLauncher 发布工具

  ol-release keygen <输出目录>
      生成一对 RSA-3072 发布密钥：private-key.xml（绝对保密，丢了就不能再发版）和 public-key.xml（写进 Branding.cs）

  ol-release publish --exe <OpenLauncher.exe> --version <0.1.1> --key <private-key.xml> --pub <public-key.xml>
                     --url <下载地址1> [--url <下载地址2> ...] [--notes <更新说明.md>] [--config <config.json>]
                     [--add-key <新公钥.xml>] [--revoke <指纹>] --out <release/update.json>
      计算 exe 的 SHA-256，生成并签名更新清单。--config 可附带下发镜像表 / 更新源 / 中文词表。

  ol-release verify --in <update.json> --pub <public-key.xml> [--exe <OpenLauncher.exe>]
      校验清单签名（以及 exe 哈希）

  ol-release fingerprint <public-key.xml>");
            return 2;
        }

        static Dictionary<string, List<string>> Opts(IEnumerable<string> args)
        {
            var d = new Dictionary<string, List<string>>();
            string key = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--")) { key = a.Substring(2); if (!d.ContainsKey(key)) d[key] = new List<string>(); }
                else if (key != null) d[key].Add(a);
            }
            return d;
        }

        static string One(Dictionary<string, List<string>> o, string k, bool required = true)
        {
            if (o.TryGetValue(k, out var v) && v.Count > 0) return v[0];
            if (required) throw new Exception($"缺少参数 --{k}");
            return null;
        }

        static int KeyGen(string[] args)
        {
            var dir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(dir);
            var priv = Path.Combine(dir, "private-key.xml");
            if (File.Exists(priv)) throw new Exception(priv + " 已存在，拒绝覆盖（防止误删旧钥匙）");
            var (p, pub) = UpdateCrypto.GenerateKey();
            File.WriteAllText(priv, p);
            File.WriteAllText(Path.Combine(dir, "public-key.xml"), pub);
            Console.WriteLine("已生成：" + Path.GetFullPath(dir));
            Console.WriteLine("公钥指纹：" + UpdateCrypto.Fingerprint(pub));
            Console.WriteLine("把 public-key.xml 的内容粘贴进 src/OpenLauncher/Branding.cs 的 TrustedKeys；private-key.xml 离线保存好、多备份，不要提交到仓库。");
            return 0;
        }

        static int Publish(Dictionary<string, List<string>> o)
        {
            var exe = One(o, "exe");
            var version = One(o, "version");
            var privXml = File.ReadAllText(One(o, "key"));
            var pubXml = File.ReadAllText(One(o, "pub"));
            var outPath = One(o, "out");
            var urls = o.TryGetValue("url", out var u) ? u : new List<string>();
            if (urls.Count == 0) throw new Exception("至少给一个 --url（建议 GitHub Release + 国内镜像各一个）");

            // 只读 PE 里的版本资源，不加载程序集（开了“智能应用控制”的电脑会拦截加载未签名程序集）
            var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            var exeVerStr = $"{fv.FileMajorPart}.{fv.FileMinorPart}.{fv.FileBuildPart}";
            if (exeVerStr != version) throw new Exception($"exe 内部版本是 {exeVerStr}，与 --version {version} 不一致（改 csproj 的 <Version> 后重新编译）");

            byte[] bytes = File.ReadAllBytes(exe);
            string sha;
            using (var a = SHA256.Create()) sha = string.Concat(a.ComputeHash(bytes).Select(b => b.ToString("x2")));

            var payload = JNode.Obj()
                .Set("app", "OpenLauncher")
                .Set("version", version)
                .Set("published", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
                .Set("notes", One(o, "notes", false) is string n ? File.ReadAllText(n, Encoding.UTF8) : "")
                .Set("file", JNode.Obj()
                    .Set("name", Path.GetFileName(exe))
                    .Set("size", bytes.LongLength)
                    .Set("sha256", sha)
                    .Set("urls", JNode.Arr(urls.Select(x => (JNode)x))));
            var cfg = One(o, "config", false);
            if (cfg != null) payload.Set("config", JNode.ParseFile(cfg));
            var keys = JNode.Obj();
            if (o.TryGetValue("add-key", out var addKeys) && addKeys.Count > 0)
                keys.Set("add", JNode.Arr(addKeys.Select(f => (JNode)File.ReadAllText(f).Trim())));
            if (o.TryGetValue("revoke", out var rev) && rev.Count > 0)
                keys.Set("revoke", JNode.Arr(rev.Select(x => (JNode)x)));
            if (keys.Count > 0) payload.Set("keys", keys);

            var env = UpdateCrypto.MakeEnvelope(payload, privXml, pubXml);
            env.SaveFile(outPath);
            Console.WriteLine($"已生成 {Path.GetFullPath(outPath)}");
            Console.WriteLine($"版本 {version}  大小 {bytes.Length}  SHA-256 {sha}");
            Console.WriteLine("把 update.json 提交到仓库 release/ 目录（以及所有镜像），exe 上传到上面的下载地址。");
            return 0;
        }

        static int Verify(Dictionary<string, List<string>> o)
        {
            var env = JNode.ParseFile(One(o, "in"));
            var pub = File.ReadAllText(One(o, "pub"));
            var opened = UpdateCrypto.Open(env, new[] { pub });
            if (opened == null) { Console.WriteLine("签名无效！"); return 1; }
            var p = opened.Value.payload;
            Console.WriteLine($"签名有效（{opened.Value.signer}）：版本 {p["version"].Str}，发布于 {p["published"].Str}");
            var exe = One(o, "exe", false);
            if (exe != null)
            {
                string sha;
                using (var a = SHA256.Create()) sha = string.Concat(a.ComputeHash(File.ReadAllBytes(exe)).Select(b => b.ToString("x2")));
                bool ok = string.Equals(sha, p["file"]["sha256"].Str, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine(ok ? "exe 哈希一致" : "exe 哈希不一致！");
                if (!ok) return 1;
            }
            return 0;
        }
    }
}
