using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace LunoLauncher.Core
{
    /// <summary>
    /// 第三方组件的出处与许可。文本随 exe 一起分发（LGPL-3.0 与 Wintun 许可都要求附上全文）。
    /// </summary>
    public static class ThirdParty
    {
        public const string EasyTierVersion = "2.6.4";
        public const string EasyTierRepo = "https://github.com/EasyTier/EasyTier";

        public static string Notice() => Read("Luno.License.THIRD-PARTY.txt");
        public static string EasyTierLicense() => Read("Luno.License.LGPL-3.0.txt");
        public static string WintunLicense() => Read("Luno.License.wintun.txt");

        /// <summary>说明 + 两份许可全文，直接丢给 Dialogs.Long。</summary>
        public static string Licenses()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Notice());
            sb.AppendLine();
            sb.AppendLine("================================================================");
            sb.AppendLine(" EasyTier 的许可全文：GNU LGPL v3.0");
            sb.AppendLine("================================================================");
            sb.AppendLine();
            sb.AppendLine(EasyTierLicense());
            sb.AppendLine();
            sb.AppendLine("================================================================");
            sb.AppendLine(" wintun.dll 的许可全文：Prebuilt Binaries License");
            sb.AppendLine("================================================================");
            sb.AppendLine();
            sb.AppendLine(WintunLicense());
            return sb.ToString();
        }

        static string Read(string name)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) return "（这个构建没有打包许可文本，见 " + Branding.RepoUrl + "/blob/main/NOTICE）";
                    using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd().TrimEnd();
                }
            }
            catch (Exception ex) { return "读取失败：" + ex.Message; }
        }
    }
}
