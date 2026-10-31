using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LunoLauncher.Core;

namespace LunoLauncher.UI
{
    /// <summary>
    /// 结构标记的图标：cubiomes-viewer 的同款 PNG（GPL-3.0，来源见 NOTICE）。
    ///
    /// 文件名对应 cubiomes finders.h 的 StructureType 顺序；cubiomes-viewer 里下界传送门
    /// 和废弃传送门共用一张图（icons[D_PORTALN] = icons[D_PORTAL]），这里也一样。
    ///
    /// 有 4 种结构 viewer 没有图标（旧版特征点 / 下界化石 / 末地小岛 / 废弃营地），
    /// 它们用的是本仓库自己画的 20×20 像素图（tools/icons/gen-structure-icons.py 生成，
    /// 风格对齐 viewer 那套：深色 1px 描边 + 扁平色块），同样 GPL-3.0。
    /// </summary>
    public static class StructIcons
    {
        static readonly string[] Files =
        {
            "feature",          // 0  Feature（自己画的：石板 + 目标点）
            "desert",           // 1  Desert_Pyramid
            "jungle",           // 2  Jungle_Temple
            "hut",              // 3  Swamp_Hut
            "igloo",            // 4  Igloo
            "village",          // 5  Village
            "ruins",            // 6  Ocean_Ruin
            "shipwreck",        // 7  Shipwreck
            "monument",         // 8  Monument
            "mansion",          // 9  Mansion
            "outpost",          // 10 Outpost
            "portal",           // 11 Ruined_Portal
            "portal",           // 12 Ruined_Portal_N（viewer 共用同一张）
            "ancient_city",     // 13 Ancient_City
            "treasure",         // 14 Treasure
            "mineshaft",        // 15 Mineshaft
            "well",             // 16 Desert_Well
            "geode",            // 17 Geode
            "fortress",         // 18 Fortress
            "bastion",          // 19 Bastion
            "nether_fossil",    // 20 Nether_Fossil（自己画的：骨头）
            "endcity",          // 21 End_City
            "gateway",          // 22 End_Gateway
            "end_island",       // 23 End_Island（自己画的：浮空末地小岛）
            "trails",           // 24 Trail_Ruins
            "chambers",         // 25 Trial_Chambers
            "abandoned_camp",   // 26 Abandoned_Camp（自己画的：破帐篷 + 余烬）
            "stronghold",       // 27 Stronghold
        };

        static readonly ImageSource[] Cache = new ImageSource[Files.Length];

        /// <summary>取图标；这个类型没有图标就返回 null</summary>
        public static ImageSource Get(int type)
        {
            if (type < 0 || type >= Files.Length) return null;
            if (Cache[type] != null) return Cache[type];
            string file = Files[type];
            if (string.IsNullOrEmpty(file)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/Assets/structure-icons/{file}.png", UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Cache[type] = bmp;
            }
            catch (Exception ex)
            {
                Log.Warn($"结构图标 {file}.png 加载失败：{ex.Message}");
                return null;
            }
            return Cache[type];
        }
    }
}
