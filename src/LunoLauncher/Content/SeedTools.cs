using System;

namespace LunoLauncher.Content
{
    /// <summary>
    /// 种子相关的「纯算法」部分：不依赖世界生成，结果精确。
    ///
    /// 史莱姆区块只取决于世界种子和区块坐标的线性组合，与生物群系、地形无关，
    /// 所以可以完全离线复刻原版。公式同 Minecraft Wiki（Slime#Slime_chunks）与 cubiomes 的 isSlimeChunk。
    /// </summary>
    public static class SeedTools
    {
        /// <summary>把用户输入的种子转成世界种子；非数字按 Java 的 String.hashCode()</summary>
        public static bool TryParseSeed(string text, out long seed)
        {
            seed = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            long n;
            if (long.TryParse(text, out n)) { seed = n; return true; }
            int h = 0;
            foreach (char c in text) h = unchecked(32 * h + c);
            seed = h;
            return true;
        }

        /// <summary>java.util.Random 的 48 位 LCG</summary>
        sealed class JavaRandom
        {
            long _state;
            const long Mask = (1L << 48) - 1;

            public JavaRandom(long seed) { _state = (seed ^ 0x5DEECE66DL) & Mask; }

            int Next(int bits)
            {
                _state = (_state * 0x5DEECE66DL + 0xBL) & Mask;
                return (int)((ulong)_state >> (48 - bits));
            }

            public int NextInt(int bound)
            {
                if (bound <= 0) return 0;
                if ((bound & -bound) == bound) return (int)((bound * (long)Next(31)) >> 31);
                int bits, val;
                do
                {
                    bits = Next(31);
                    val = bits % bound;
                } while (bits - val + (bound - 1) < 0);
                return val;
            }
        }

        /// <summary>这个区块是不是史莱姆区块（区块坐标，不是方块坐标）</summary>
        public static bool IsSlimeChunk(long worldSeed, int chunkX, int chunkZ)
        {
            long a = (long)chunkX * chunkX * 0x4c1906L;
            long b = (long)chunkX * 0x5ac0dbL;
            long c = (long)chunkZ * chunkZ * 0x4307a7L;
            long d = (long)chunkZ * 0x5f24fL;
            long seed = (worldSeed + a + b + c + d) ^ 0x3ad8025fL;
            return new JavaRandom(seed).NextInt(10) == 0;
        }
    }
}
