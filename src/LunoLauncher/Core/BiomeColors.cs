using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace LunoLauncher.Core
{
    /// <summary>
    /// 群系 id → 颜色。和结构标记的颜色（ToolboxPage.Seed.cs 里的 StructColors）是两套东西，
    /// 别混在一起：群系配色按 biome id，结构配色按 StructureType。
    ///
    /// 约定：
    ///  - 下界五个群系一系一色（荒地 / 灵魂沙峡谷 / 绯红森林 / 诡异森林 / 玄武岩三角洲），
    ///    以前它们全是同一个红褐色，图上完全分不出来；
    ///  - 末地五种（主岛 / 小岛 / 内陆 / 高地 / 荒岛）各自一色，
    ///    注意 biome id 42/43 是末地高地/荒岛，不是海洋（早期版本把它们写进海洋那一组了）；
    ///  - 表里没有的 id 用 id 散列出一个稳定颜色，不会变成黑色或透明。
    /// </summary>
    public static class BiomeColors
    {
        /// <summary>下界：一系一色</summary>
        public static readonly Color NetherWastes = Rgb(0x6B2C2C);      // 8
        public static readonly Color SoulSandValley = Rgb(0x6E6C7E);    // 170
        public static readonly Color CrimsonForest = Rgb(0x8E2422);     // 171
        public static readonly Color WarpedForest = Rgb(0x1E7066);      // 172
        public static readonly Color BasaltDeltas = Rgb(0x494954);      // 173

        /// <summary>末地：主岛 / 小岛 / 内陆 / 高地 / 荒岛</summary>
        public static readonly Color EndTheEnd = Rgb(0xD8CFA0);         // 9  主岛
        public static readonly Color EndSmallIslands = Rgb(0xA9A9A9);   // 40 小岛
        public static readonly Color EndMidlands = Rgb(0xCFC492);       // 41 内陆
        public static readonly Color EndHighlands = Rgb(0xE0D8AE);      // 42 高地
        public static readonly Color EndBarrens = Rgb(0xB8B080);        // 43 荒岛

        static Color Rgb(int v) => Color.FromRgb((byte)(v >> 16), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));

        static readonly Dictionary<int, Color> Map = Build();

        static Dictionary<int, Color> Build()
        {
            var m = new Dictionary<int, Color>();

            // ---------- 海洋 / 河流 ----------
            m[0] = Rgb(0x3B6EB5);    // ocean
            m[24] = Rgb(0x2A4F86);   // deep_ocean
            m[10] = Rgb(0x4A6FA5);   // frozen_ocean
            m[50] = Rgb(0x1E3A57);   // deep_frozen_ocean
            m[44] = Rgb(0x3FA9C9);   // warm_ocean
            m[47] = Rgb(0x2E7FA8);   // deep_warm_ocean
            m[45] = Rgb(0x3E8FC0);   // lukewarm_ocean
            m[48] = Rgb(0x2C6E99);   // deep_lukewarm_ocean
            m[46] = Rgb(0x35618F);   // cold_ocean
            m[49] = Rgb(0x274F77);   // deep_cold_ocean
            m[7] = Rgb(0x4A86C8);    // river
            m[11] = Rgb(0x6FA8DC);   // frozen_river

            // ---------- 平原 / 森林 ----------
            m[1] = Rgb(0x8FB75E);    // plains
            m[129] = Rgb(0xA6C46A);  // sunflower_plains
            m[4] = Rgb(0x4E8A3F);    // forest
            m[132] = Rgb(0x6FA64E);  // flower_forest
            m[27] = Rgb(0x6E9C4A);   // birch_forest
            m[28] = Rgb(0x638F42);   // birch_forest_hills
            m[155] = Rgb(0x5F8F45);  // tall_birch_forest / old_growth_birch_forest
            m[156] = Rgb(0x577F3E);  // tall_birch_hills
            m[29] = Rgb(0x2F5E2C);   // dark_forest
            m[157] = Rgb(0x2A5228);  // dark_forest_hills
            m[188] = Rgb(0x6A8E62);  // dappled_forest (26.3)
            m[186] = Rgb(0xCFCFC4);  // pale_garden (1.21.4)

            // ---------- 针叶林 ----------
            m[5] = Rgb(0x547F6B);    // taiga
            m[19] = Rgb(0x4E7563);   // taiga_hills
            m[133] = Rgb(0x47695A);  // taiga_mountains
            m[30] = Rgb(0x5C8A80);   // snowy_taiga
            m[31] = Rgb(0x54807A);   // snowy_taiga_hills
            m[158] = Rgb(0x4C7270);  // snowy_taiga_mountains
            m[32] = Rgb(0x4F7A5E);   // giant_tree_taiga / old_growth_pine_taiga
            m[33] = Rgb(0x4A7159);   // giant_tree_taiga_hills
            m[160] = Rgb(0x456B52);  // giant_spruce_taiga / old_growth_spruce_taiga
            m[161] = Rgb(0x40604B);  // giant_spruce_taiga_hills
            m[178] = Rgb(0x7C9B95);  // grove

            // ---------- 雪原 / 山地 ----------
            m[12] = Rgb(0xEAEEF2);   // snowy_tundra / snowy_plains
            m[13] = Rgb(0xDDE4EA);   // snowy_mountains
            m[140] = Rgb(0xCFE6F2);  // ice_spikes
            m[3] = Rgb(0x9A9A96);    // mountains / windswept_hills
            m[20] = Rgb(0xA8A8A2);   // mountain_edge
            m[131] = Rgb(0x8E8E8A);  // gravelly_mountains / windswept_gravelly_hills
            m[162] = Rgb(0x7F7F7B);  // modified_gravelly_mountains
            m[34] = Rgb(0x6E8F6A);   // wooded_mountains / windswept_forest
            m[177] = Rgb(0x8FC26F);  // meadow
            m[179] = Rgb(0xC8CED6);  // snowy_slopes
            m[180] = Rgb(0xB8BEC6);  // jagged_peaks
            m[181] = Rgb(0xDCE4EC);  // frozen_peaks
            m[182] = Rgb(0xA8AEB6);  // stony_peaks

            // ---------- 丛林 ----------
            m[21] = Rgb(0x2E7A35);   // jungle
            m[22] = Rgb(0x2A7031);   // jungle_hills
            m[149] = Rgb(0x347F38);   // modified_jungle
            m[23] = Rgb(0x3C8A40);   // jungle_edge / sparse_jungle
            m[151] = Rgb(0x3A833C);  // modified_jungle_edge
            m[168] = Rgb(0x5C9E3E);  // bamboo_jungle
            m[169] = Rgb(0x549237);  // bamboo_jungle_hills

            // ---------- 沼泽 ----------
            m[6] = Rgb(0x4C6B4A);    // swamp
            m[134] = Rgb(0x476244);  // swamp_hills
            m[184] = Rgb(0x3E5E4C);  // mangrove_swamp

            // ---------- 干旱 ----------
            m[2] = Rgb(0xD8C48A);    // desert
            m[17] = Rgb(0xCDB57A);   // desert_hills
            m[130] = Rgb(0xC9AE74);  // desert_lakes
            m[35] = Rgb(0xC2C05E);   // savanna
            m[36] = Rgb(0xB8B454);   // savanna_plateau
            m[163] = Rgb(0xB0B052);  // shattered_savanna / windswept_savanna
            m[164] = Rgb(0xA8A84C);  // shattered_savanna_plateau
            m[37] = Rgb(0xC28A4E);   // badlands
            m[165] = Rgb(0xB57C42);  // eroded_badlands
            m[38] = Rgb(0xA87A48);   // wooded_badlands_plateau / wooded_badlands
            m[166] = Rgb(0x9E7240);  // modified_wooded_badlands_plateau
            m[39] = Rgb(0xBE8449);   // badlands_plateau
            m[167] = Rgb(0xB07840);  // modified_badlands_plateau

            // ---------- 丘陵（旧版细分） ----------
            m[18] = Rgb(0x5E8F4A);   // wooded_hills

            // ---------- 特殊 / 洞穴 ----------
            m[14] = Rgb(0xB89EB8);   // mushroom_fields
            m[15] = Rgb(0xA892A8);   // mushroom_field_shore
            m[16] = Rgb(0xE0D5A8);   // beach
            m[26] = Rgb(0xE8EEF0);   // snowy_beach
            m[25] = Rgb(0xA0A09C);   // stone_shore / stony_shore
            m[174] = Rgb(0x8D7B6E);  // dripstone_caves
            m[175] = Rgb(0x5FA54A);  // lush_caves
            m[183] = Rgb(0x224A4A);  // deep_dark
            m[187] = Rgb(0xC8C03C);  // sulfur_caves (26.2)
            m[185] = Rgb(0xEFB0C4);  // cherry_grove
            m[127] = Rgb(0x101010);  // the_void

            // ---------- 下界（一系一色，别合并） ----------
            m[8] = NetherWastes;
            m[170] = SoulSandValley;
            m[171] = CrimsonForest;
            m[172] = WarpedForest;
            m[173] = BasaltDeltas;

            // ---------- 末地（五种分开；42/43 是高地/荒岛，不是海洋） ----------
            m[9] = EndTheEnd;
            m[40] = EndSmallIslands;
            m[41] = EndMidlands;
            m[42] = EndHighlands;
            m[43] = EndBarrens;

            // ---------- Alpha / Beta 老群系 ----------
            m[51] = Rgb(0x6C8C4A);   // seasonal_forest
            m[52] = Rgb(0x2F7A34);   // rainforest
            m[53] = Rgb(0x8FA256);   // shrubland

            return m;
        }

        /// <summary>取群系颜色；没登记的 id 用散列色，保证同一 id 每次都是同一个颜色</summary>
        public static Color Get(int id)
        {
            if (Map.TryGetValue(id, out var c)) return c;
            uint k = unchecked((uint)(id * 2654435761u));
            return Color.FromRgb(
                (byte)(0x70 + (k & 0x3F)),
                (byte)(0x90 + ((k >> 6) & 0x3F)),
                (byte)(0x70 + ((k >> 12) & 0x3F)));
        }

        public static bool Known(int id) => Map.ContainsKey(id);
    }
}
