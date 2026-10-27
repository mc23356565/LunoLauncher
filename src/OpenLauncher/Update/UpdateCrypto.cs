using System;
using System.Security.Cryptography;
using System.Text;
using OpenLauncher.Core;

namespace OpenLauncher.Update
{
    /// <summary>
    /// 更新清单的签名格式（启动器和发布工具共用这一份代码）：
    /// {
    ///   "format": 1,
    ///   "payload": "&lt;base64(UTF-8 JSON)&gt;",
    ///   "signatures": [ { "key": "&lt;公钥指纹&gt;", "sig": "&lt;base64 RSA-SHA256-PKCS1&gt;" } ]
    /// }
    /// 签的是 payload 的原始字节，所以清单放在哪、被谁转发、被 CDN 怎么压缩都不影响验证。
    /// </summary>
    public static class UpdateCrypto
    {
        public static RSA LoadKey(string xml)
        {
            var rsa = new RSACng();
            rsa.FromXmlString(xml.Trim());
            return rsa;
        }

        /// <summary>公钥指纹：SHA-256(模数 || 指数) 的前 16 个十六进制字符</summary>
        public static string Fingerprint(string publicXml)
        {
            using (var rsa = LoadKey(publicXml))
            {
                var p = rsa.ExportParameters(false);
                var buf = new byte[p.Modulus.Length + p.Exponent.Length];
                Buffer.BlockCopy(p.Modulus, 0, buf, 0, p.Modulus.Length);
                Buffer.BlockCopy(p.Exponent, 0, buf, p.Modulus.Length, p.Exponent.Length);
                return Util.Sha256(buf).Substring(0, 16);
            }
        }

        public static string Sign(byte[] payload, string privateXml)
        {
            using (var rsa = LoadKey(privateXml))
                return Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        public static bool Verify(byte[] payload, string sigBase64, string publicXml)
        {
            try
            {
                using (var rsa = LoadKey(publicXml))
                    return rsa.VerifyData(payload, Convert.FromBase64String(sigBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch { return false; }
        }

        public static JNode MakeEnvelope(JNode payload, string privateXml, string publicXml)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToJson(true));
            return JNode.Obj()
                .Set("format", 1)
                .Set("payload", Convert.ToBase64String(bytes))
                .Set("signatures", JNode.Arr().Add(JNode.Obj().Set("key", Fingerprint(publicXml)).Set("sig", Sign(bytes, privateXml))));
        }

        /// <summary>验证信封；成功返回 payload 与签名者指纹，失败返回 null</summary>
        public static (JNode payload, string signer)? Open(JNode envelope, System.Collections.Generic.IEnumerable<string> trustedPublicXmls)
        {
            var b64 = envelope["payload"].Str;
            if (b64 == null) return null;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); } catch { return null; }
            foreach (var key in trustedPublicXmls)
            {
                string fp;
                try { fp = Fingerprint(key); } catch { continue; }
                foreach (var s in envelope["signatures"])
                {
                    if (s["key"].Str != null && s["key"].Str != fp) continue;
                    if (Verify(bytes, s["sig"].Str, key))
                        return (JNode.Parse(Encoding.UTF8.GetString(bytes)), fp);
                }
            }
            return null;
        }

        public static (string privateXml, string publicXml) GenerateKey(int bits = 3072)
        {
            using (var rsa = new RSACng(bits))
                return (rsa.ToXmlString(true), rsa.ToXmlString(false));
        }
    }
}
