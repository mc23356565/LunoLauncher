#include <stdint.h>
#include "generator.h"
#include "finders.h"

static Generator g;
static uint64_t gSeed = 0;

__declspec(dllexport) int luno_mc_newest(void) { return (int)MC_NEWEST; }
__declspec(dllexport) int luno_setup(int mc, int large) { setupGenerator(&g, mc, large ? LARGE_BIOMES : 0); return 0; }
__declspec(dllexport) int luno_apply_seed(long long seed, int dim) { gSeed = (uint64_t)seed; applySeed(&g, dim, (uint64_t)seed); return 0; }
__declspec(dllexport) int luno_biome_at(int scale, int x, int y, int z) { return getBiomeAt(&g, scale, x, y, z); }
__declspec(dllexport) int luno_slime_chunk(int cx, int cz) { return isSlimeChunk(gSeed, cx, cz); }
__declspec(dllexport) int luno_structure_pos(int type, int mc, int regX, int regZ, int *ox, int *oz) {
    Pos p; int ok = getStructurePos(type, mc, gSeed, regX, regZ, &p);
    if (ok) { *ox = p.x; *oz = p.z; } return ok;
}

/* 群系 id -> 英文名（cubiomes 的 biome2str，可能返回 "n/a"） */
extern const char *biome2str(int mc, int id);

__declspec(dllexport) const char *luno_biome_name(int mc, int id)
{
    return biome2str(mc, id);
}

/* 结构的区域尺寸（多少区块一个区域），结构标记要靠它换算 regX/regZ */
__declspec(dllexport) int luno_struct_region_size(int type, int mc)
{
    StructureConfig sc;
    if (!getStructureConfig(type, mc, &sc)) return 0;
    return sc.regionSize;
}

/* 这个尝试点是否真的会生成结构（要先用 luno_setup/luno_apply_seed 准备好生成器） */
__declspec(dllexport) int luno_struct_viable(int type, int x, int z)
{
    return isViableStructurePos(type, &g, x, z, 0);
}

/* 要塞不是按 region 网格生成的（getStructureConfig 对它返回 0），
   所以单独用 StrongholdIter 迭代。
   注意：initFirstStronghold 返回的是"近似位置"（±112 方块），必须再调一次
   nextStronghold 才拿到第一个要塞的准确位置，所以这里不用它的返回值。
   out 里存 [x0,z0,x1,z1,...]（方块坐标），返回个数（最多 max 个）。
   调用前要先 luno_setup + luno_apply_seed（主世界）。 */
__declspec(dllexport) int luno_strongholds(int mc, int max, int *out)
{
    if (!out || max <= 0) return 0;
    StrongholdIter sh;
    initFirstStronghold(&sh, mc, gSeed);
    int n = 0;
    while (n < max)
    {
        if (!nextStronghold(&sh, &g)) break;
        out[n * 2] = sh.pos.x;
        out[n * 2 + 1] = sh.pos.z;
        n++;
    }
    return n;
}

/* ---------------------------------------------------------------------------
 * 版本枚举
 *
 * 下拉列表必须跟着 cubiomes 的 enum MCVersion 走，所以名单放在 DLL 里：
 * 以后把 cubiomes 升到新版本，只要同步这张表，C# 那边什么都不用改。
 *
 * id    = 传给 luno_setup 的第一个参数（就是 MCVersion 的值）
 * name  = 这个枚举项实际代表的最新补丁号（cubiomes 的约定：每个大版本放最新的那个补丁）
 * range = 界面显示用的覆盖区间（"1.16.2-1.16.5"）。这一段是人写的，升 cubiomes 时要一起核对。
 * group = 下拉分组：0 Beta / 1 远古(1.0-1.6) / 2 1.7-1.12 / 3 1.13-1.17 / 4 1.18+ / 5 26.x
 * ------------------------------------------------------------------------- */
typedef struct { int id; const char *name; const char *range; int group; } LunoMcVersion;

static const LunoMcVersion gMcVersions[] = {
    { MC_B1_7,   "Beta 1.7",  "Beta 1.7",        0 },
    { MC_B1_8,   "Beta 1.8",  "Beta 1.8",        0 },
    { MC_1_0_0,  "1.0.0",     "1.0.0",           1 },
    { MC_1_1_0,  "1.1.0",     "1.1.0",           1 },
    { MC_1_2_5,  "1.2.5",     "1.2.1-1.2.5",     1 },
    { MC_1_3_2,  "1.3.2",     "1.3.1-1.3.2",     1 },
    { MC_1_4_7,  "1.4.7",     "1.4.2-1.4.7",     1 },
    { MC_1_5_2,  "1.5.2",     "1.5.1-1.5.2",     1 },
    { MC_1_6_4,  "1.6.4",     "1.6.1-1.6.4",     1 },
    { MC_1_7_10, "1.7.10",    "1.7.2-1.7.10",    2 },
    { MC_1_8_9,  "1.8.9",     "1.8-1.8.9",       2 },
    { MC_1_9_4,  "1.9.4",     "1.9-1.9.4",       2 },
    { MC_1_10_2, "1.10.2",    "1.10-1.10.2",     2 },
    { MC_1_11_2, "1.11.2",    "1.11-1.11.2",     2 },
    { MC_1_12_2, "1.12.2",    "1.12-1.12.2",     2 },
    { MC_1_13_2, "1.13.2",    "1.13-1.13.2",     3 },
    { MC_1_14_4, "1.14.4",    "1.14-1.14.4",     3 },
    { MC_1_15_2, "1.15.2",    "1.15-1.15.2",     3 },
    { MC_1_16_1, "1.16.1",    "1.16-1.16.1",     3 },
    { MC_1_16_5, "1.16.5",    "1.16.2-1.16.5",   3 },
    { MC_1_17_1, "1.17.1",    "1.17-1.17.1",     3 },
    { MC_1_18_2, "1.18.2",    "1.18-1.18.2",     4 },
    { MC_1_19_2, "1.19.2",    "1.19-1.19.2",     4 },
    { MC_1_19_4, "1.19.4",    "1.19.3-1.19.4",   4 },
    { MC_1_20_6, "1.20.6",    "1.20-1.20.6",     4 },
    { MC_1_21_1, "1.21.1",    "1.21-1.21.1",     4 },
    { MC_1_21_3, "1.21.3",    "1.21.2-1.21.3",   4 },
    { MC_1_21_WD,"1.21.4",    "1.21.4",          4 },
    { MC_1_21_5, "1.21.5",    "1.21.5",          4 },
    { MC_1_21_6, "1.21.6",    "1.21.6",          4 },
    { MC_1_21_9, "1.21.9",    "1.21.7-1.21.9",   4 },
    { MC_1_21_11,"1.21.11",   "1.21.10-1.21.11", 4 },
    { MC_26_1,   "26.1",      "26.1",            5 },
    { MC_26_2,   "26.2",      "26.2",            5 },
    { MC_26_3,   "26.3",      "26.3",            5 },
};

#define LUNO_MC_COUNT ((int)(sizeof(gMcVersions) / sizeof(gMcVersions[0])))

__declspec(dllexport) int luno_mc_count(void) { return LUNO_MC_COUNT; }

/* 下标 -> 版本 id（就是 MCVersion 的值，可以直接喂 luno_setup） */
__declspec(dllexport) int luno_mc_id(int index)
{
    if (index < 0 || index >= LUNO_MC_COUNT) return -1;
    return gMcVersions[index].id;
}

/* 下标 -> 显示名（最新补丁号） */
__declspec(dllexport) const char *luno_mc_name(int index)
{
    if (index < 0 || index >= LUNO_MC_COUNT) return 0;
    return gMcVersions[index].name;
}

/* 下标 -> 覆盖区间，例如 "1.16.2-1.16.5" */
__declspec(dllexport) const char *luno_mc_range(int index)
{
    if (index < 0 || index >= LUNO_MC_COUNT) return 0;
    return gMcVersions[index].range;
}

/* 下标 -> 分组（0 Beta / 1 远古 / 2 1.7-1.12 / 3 1.13-1.17 / 4 1.18+ / 5 26.x） */
__declspec(dllexport) int luno_mc_group(int index)
{
    if (index < 0 || index >= LUNO_MC_COUNT) return -1;
    return gMcVersions[index].group;
}
