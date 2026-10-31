using System.Collections.Generic;

namespace LunoLauncher.Core
{
    /// <summary>
    /// 群系英文名 → 中文名。键是 cubiomes 的 biome2str 输出。
    ///
    /// 同一个群系在 1.18 前后有两个英文名（比如 mountains → windswept_hills），
    /// 两个键都要留，否则切到旧版本就会显示英文。
    /// 表里没有的（新版本加的群系、模组群系）直接回退显示英文，不会变成空。
    /// </summary>
    public static class BiomeNames
    {
        static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            // ---------- 海洋与河流 ----------
            ["ocean"] = "海洋",
            ["deep_ocean"] = "深海",
            ["warm_ocean"] = "暖水海洋",
            ["lukewarm_ocean"] = "温水海洋",
            ["cold_ocean"] = "冷水海洋",
            ["frozen_ocean"] = "冻洋",
            ["deep_warm_ocean"] = "深暖水海洋",
            ["deep_lukewarm_ocean"] = "深温水海洋",
            ["deep_cold_ocean"] = "深冷水海洋",
            ["deep_frozen_ocean"] = "深冻洋",
            ["river"] = "河流",
            ["frozen_river"] = "冻河",
            ["beach"] = "沙滩",
            ["snowy_beach"] = "积雪的沙滩",
            ["stone_shore"] = "石岸",
            ["stony_shore"] = "石岸",

            // ---------- 平原与森林 ----------
            ["plains"] = "平原",
            ["sunflower_plains"] = "向日葵平原",
            ["forest"] = "森林",
            ["flower_forest"] = "繁花森林",
            ["birch_forest"] = "桦木森林",
            ["birch_forest_hills"] = "桦木森林丘陵",
            ["tall_birch_forest"] = "原始桦木森林",
            ["old_growth_birch_forest"] = "原始桦木森林",
            ["tall_birch_hills"] = "高桦木森林丘陵",
            ["dark_forest"] = "黑森林",
            ["dark_forest_hills"] = "黑森林丘陵",
            ["dappled_forest"] = "斑驳森林",
            ["cherry_grove"] = "樱花树林",
            ["pale_garden"] = "苍白花园",

            // ---------- 针叶林 ----------
            ["taiga"] = "针叶林",
            ["taiga_hills"] = "针叶林丘陵",
            ["taiga_mountains"] = "针叶林山地",
            ["snowy_taiga"] = "积雪的针叶林",
            ["snowy_taiga_hills"] = "积雪的针叶林丘陵",
            ["snowy_taiga_mountains"] = "积雪的针叶林山地",
            ["giant_tree_taiga"] = "原始松木针叶林",
            ["old_growth_pine_taiga"] = "原始松木针叶林",
            ["giant_tree_taiga_hills"] = "原始松木针叶林丘陵",
            ["giant_spruce_taiga"] = "原始云杉针叶林",
            ["old_growth_spruce_taiga"] = "原始云杉针叶林",
            ["giant_spruce_taiga_hills"] = "原始云杉针叶林丘陵",
            ["grove"] = "雪林",

            // ---------- 雪原与山地 ----------
            ["snowy_tundra"] = "雪原",
            ["snowy_plains"] = "雪原",
            ["snowy_mountains"] = "雪山",
            ["ice_spikes"] = "冰刺之地",
            ["mountains"] = "风袭丘陵",
            ["windswept_hills"] = "风袭丘陵",
            ["gravelly_mountains"] = "风袭沙砾丘陵",
            ["windswept_gravelly_hills"] = "风袭沙砾丘陵",
            ["modified_gravelly_mountains"] = "沙砾山地变种",
            ["wooded_mountains"] = "风袭森林",
            ["windswept_forest"] = "风袭森林",
            ["mountain_edge"] = "山地边缘",
            ["meadow"] = "草甸",
            ["snowy_slopes"] = "积雪山坡",
            ["jagged_peaks"] = "尖峭山峰",
            ["frozen_peaks"] = "冰封山峰",
            ["stony_peaks"] = "裸岩山峰",

            // ---------- 丛林 ----------
            ["jungle"] = "丛林",
            ["jungle_hills"] = "丛林丘陵",
            ["modified_jungle"] = "丛林变种",
            ["jungle_edge"] = "稀疏丛林",
            ["sparse_jungle"] = "稀疏丛林",
            ["modified_jungle_edge"] = "稀疏丛林变种",
            ["bamboo_jungle"] = "竹林",
            ["bamboo_jungle_hills"] = "竹林丘陵",

            // ---------- 沼泽 ----------
            ["swamp"] = "沼泽",
            ["swamp_hills"] = "沼泽丘陵",
            ["mangrove_swamp"] = "红树林沼泽",

            // ---------- 干旱地带 ----------
            ["desert"] = "沙漠",
            ["desert_hills"] = "沙漠丘陵",
            ["desert_lakes"] = "沙漠湖泊",
            ["savanna"] = "热带草原",
            ["savanna_plateau"] = "热带高原",
            ["shattered_savanna"] = "风袭热带草原",
            ["windswept_savanna"] = "风袭热带草原",
            ["shattered_savanna_plateau"] = "破碎热带高原",
            ["badlands"] = "恶地",
            ["eroded_badlands"] = "风蚀恶地",
            ["wooded_badlands_plateau"] = "疏林恶地",
            ["wooded_badlands"] = "疏林恶地",
            ["modified_wooded_badlands_plateau"] = "疏林恶地变种",
            ["badlands_plateau"] = "恶地高原",
            ["modified_badlands_plateau"] = "恶地高原变种",

            // ---------- 丘陵（旧版细分） ----------
            ["wooded_hills"] = "疏林丘陵",

            // ---------- 特殊 / 洞穴 ----------
            ["mushroom_fields"] = "蘑菇岛",
            ["mushroom_field_shore"] = "蘑菇岛岸",
            ["dripstone_caves"] = "滴水石洞",
            ["lush_caves"] = "繁茂洞穴",
            ["deep_dark"] = "深暗之域",
            ["sulfur_caves"] = "硫磺洞穴",
            ["the_void"] = "虚空",

            // ---------- 下界 ----------
            ["nether_wastes"] = "下界荒地",
            ["soul_sand_valley"] = "灵魂沙峡谷",
            ["crimson_forest"] = "绯红森林",
            ["warped_forest"] = "诡异森林",
            ["basalt_deltas"] = "玄武岩三角洲",

            // ---------- 末地 ----------
            ["the_end"] = "末地",
            ["end_barrens"] = "末地荒岛",
            ["end_highlands"] = "末地高地",
            ["end_midlands"] = "末地内陆",
            ["small_end_islands"] = "末地小岛",

            // ---------- Alpha / Beta 老群系（只在很老的版本里出现） ----------
            ["seasonal_forest"] = "季节性森林",
            ["rainforest"] = "雨林",
            ["shrubland"] = "灌木丛地",
        };

        /// <summary>英文名转中文；没有对应项时原样返回</summary>
        public static string Zh(string english)
        {
            if (string.IsNullOrEmpty(english)) return "";
            return Map.TryGetValue(english, out var zh) ? zh : english;
        }
    }
}
