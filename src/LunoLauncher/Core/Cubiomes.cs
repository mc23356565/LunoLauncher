using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace LunoLauncher.Core
{
    /// <summary>
    /// cubiomes 原生库的调用层（x64 的 cubiomes.dll）。
    ///
    /// 这个 C 库负责真正的世界生成算法 —— 生物群系、结构位置、要塞，
    /// 和 cubiomes-viewer 用的是同一个库。启动器自己只做界面和缓存。
    ///
    /// DLL 是**内嵌在 exe 里**的（单文件发布）：第一次用到时把它解压到
    /// %LOCALAPPDATA%\LunoLauncher\native\&lt;内容哈希&gt;\cubiomes.dll，再用 LoadLibrary
    /// 按完整路径加载一次；之后 DllImport("cubiomes.dll") 会命中进程里已加载的同名模块。
    /// 如果 exe 旁边正好也有一份，就优先用那份（本地调试 / 手动替换）。
    ///
    /// 加载失败时 <see cref="Available"/> 为 false，
    /// 调用方要据此把功能标成「不可用」，而不是崩掉。
    /// </summary>
    public static class Cubiomes
    {
        const string Dll = "cubiomes.dll";
        const string ResName = "Luno.Native.cubiomes.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);

        static bool _tried;
        static bool _available;

        /// <summary>原生库能不能用（第一次调用时尝试加载）</summary>
        public static bool Available
        {
            get
            {
                if (!_tried) Init();
                return _available;
            }
        }

        /// <summary>加载失败的原因，给界面显示用</summary>
        public static string LastError { get; private set; }

        /// <summary>实际用的 dll 路径（日志 / 诊断用）</summary>
        public static string LibraryPath { get; private set; }

        static void Init()
        {
            _tried = true;
            try
            {
                string path = EnsureNativeFile();
                if (path == null)
                {
                    Log.Warn("种子查询：" + LastError);
                    return;
                }
                LibraryPath = path;

                // 先按完整路径加载：DllImport("cubiomes.dll") 之后会按模块名找到它
                IntPtr handle = LoadLibraryW(path);
                if (handle == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    LastError = "cubiomes.dll 加载失败（Win32 错误 " + err + "，可能是 32/64 位不匹配或缺 VC 运行时）";
                    Log.Error("加载 " + path + " 失败，Win32 错误 " + err);
                    return;
                }

                var newest = Native.McNewest();
                _available = newest > 0;
                if (_available) Log.Info($"cubiomes 已加载（{path}），支持的最新版本编号 {newest}");
                else LastError = "cubiomes.dll 加载后没有返回版本信息";
            }
            catch (Exception ex)
            {
                // 常见原因：缓存目录不可写、内嵌资源缺失、缺 VC 运行时
                LastError = "cubiomes 初始化失败：" + ex.Message;
                Log.Error("初始化 cubiomes 失败", ex);
                _available = false;
            }
        }

        /// <summary>
        /// 拿到一份可加载的 dll 文件：exe 旁边有就用它，否则把内嵌的那份解压到本地缓存。
        /// 缓存路径带内容哈希，升级启动器后不会用到旧 dll。
        /// </summary>
        static string EnsureNativeFile()
        {
            var beside = Path.Combine(Paths.ExeDir, Dll);
            if (File.Exists(beside)) return beside;

            using (var stream = typeof(Cubiomes).Assembly.GetManifestResourceStream(ResName))
            {
                if (stream == null)
                {
                    LastError = "程序里没有内置 cubiomes 原生库，种子查询用不了";
                    return null;
                }

                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                string hash = Util.Sha256(bytes).Substring(0, 16);
                string dir = Path.Combine(LocalDir(), "native", hash);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, Dll);

                if (!File.Exists(file) || new FileInfo(file).Length != bytes.Length)
                {
                    // 先写临时文件再改名：并发启动时不会读到写了一半的 dll
                    string tmp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                    Log.Info("已把内置 cubiomes 解压到 " + file);
                }
                return file;
            }
        }

        /// <summary>本机可写目录（%LOCALAPPDATA%\LunoLauncher；拿不到就退回临时目录）</summary>
        static string LocalDir()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Branding.AppName);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    return dir;
                }
            }
            catch { }
            return Path.GetTempPath();
        }

        /// <summary>库内置的最新版本编号（对应 biomes.h 的 MC_NEWEST）</summary>
        public static int NewestVersion => Available ? Native.McNewest() : 0;

        /// <summary>一个 Minecraft 版本条目（名单来自 DLL，C# 里不硬编码）</summary>
        public sealed class McVersion
        {
            /// <summary>传给 Setup / ApplySeed 的版本编号（cubiomes 的 MCVersion）</summary>
            public int Id;
            /// <summary>显示名，一般是这个枚举项代表的最新补丁号</summary>
            public string Name;
            /// <summary>它实际覆盖的区间，例如 "1.16.2-1.16.5"</summary>
            public string Range;
            /// <summary>分组：0 Beta / 1 远古 / 2 1.7-1.12 / 3 1.13-1.17 / 4 1.18+ / 5 26.x</summary>
            public int Group;
            /// <summary>下拉分组标题</summary>
            public string GroupName => VersionGroupName(Group);
            /// <summary>下拉里显示的一行（名字 + 覆盖区间）</summary>
            public string Display => string.IsNullOrEmpty(Range) || Range == Name ? Name : $"{Name}  ·  {Range}";
        }

        static McVersion[] _versions;

        /// <summary>
        /// cubiomes 支持的全部版本，第一次用到时从 DLL 读出来（luno_mc_count / luno_mc_id /
        /// luno_mc_name / luno_mc_range / luno_mc_group）。以后换新版 cubiomes 只要重编 DLL，
        /// 下拉列表自动跟着变，不用改 C#。
        /// </summary>
        public static McVersion[] Versions
        {
            get
            {
                if (_versions != null) return _versions;
                var list = new List<McVersion>();
                if (Available)
                {
                    try
                    {
                        int n = Native.McCount();
                        for (int i = 0; i < n; i++)
                        {
                            int id = Native.McId(i);
                            if (id <= 0) continue;
                            list.Add(new McVersion
                            {
                                Id = id,
                                Name = PtrToStr(Native.McName(i)),
                                Range = PtrToStr(Native.McRange(i)),
                                Group = Native.McGroup(i),
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("读取 cubiomes 版本列表失败：" + ex.Message);
                    }
                }
                _versions = list.ToArray();
                return _versions;
            }
        }

        /// <summary>分组下标 → 分组标题</summary>
        public static string VersionGroupName(int group)
        {
            switch (group)
            {
                case 0: return "Beta";
                case 1: return "远古（1.0 - 1.6）";
                case 2: return "1.7 - 1.12";
                case 3: return "1.13 - 1.17";
                case 4: return "1.18 - 1.21";
                case 5: return "26.x";
                default: return "其它";
            }
        }

        /// <summary>
        /// 能力探测：这个版本能不能生成（setup 失败就说明 cubiomes 没编进去）。
        /// 只用来判断"支不支持"，不会留下副作用（调用方本来就要再 Setup 一次）。
        /// </summary>
        public static bool CanSetup(int mcVersion)
        {
            if (!Available) return false;
            try { lock (Sync) return Native.Setup(mcVersion, 0) == 0; }
            catch { return false; }
        }

        /// <summary>这个版本有没有 3D 群系（1.18 起才有洞穴三层）</summary>
        public static bool Has3D(int mcVersion) => mcVersion >= 22;

        static string PtrToStr(IntPtr p)
            => p == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(p) ?? "");

        /// <summary>初始化某个版本的生成器；largeBiomes 只对 1.18 以前有意义</summary>
        public static bool Setup(int mcVersion, bool largeBiomes)
            => Available && Native.Setup(mcVersion, largeBiomes ? 1 : 0) == 0;

        /// <summary>世界种子 + 维度（0 主世界 / -1 下界 / 1 末地）</summary>
        public static bool ApplySeed(long seed, int dim)
            => Available && Native.ApplySeed(seed, dim) == 0;

        /// <summary>
        /// 取某个坐标的生物群系 id。scale 是 1:1 / 1:4 / 1:16 / 1:64 的倒数（1~4）。
        /// 返回 -1 表示失败。
        /// </summary>
        public static int BiomeAt(int scale, int x, int y, int z)
            => Available ? Native.BiomeAt(scale, x, y, z) : -1;

        /// <summary>这个区块是不是史莱姆区块</summary>
        public static bool IsSlimeChunk(int chunkX, int chunkZ)
            => Available && Native.SlimeChunk(chunkX, chunkZ) != 0;

        /// <summary>群系 id → 英文名（cubiomes 的 biome2str，例如 plains / windswept_hills）</summary>
        public static string BiomeName(int mcVersion, int id)
        {
            if (!Available) return "";
            try
            {
                var p = Native.BiomeName(mcVersion, id);
                if (p == IntPtr.Zero) return "";
                return Marshal.PtrToStringAnsi(p) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>某个区域内找结构尝试点；返回 false 表示这个区域没有</summary>
        public static bool StructurePos(int structureType, int mcVersion, int regX, int regZ, out int x, out int z)
        {
            x = z = 0;
            if (!Available) return false;
            return Native.StructurePos(structureType, mcVersion, regX, regZ, out x, out z) != 0;
        }

        /// <summary>某个结构的区域尺寸（多少区块一个区域）。0 = 这个版本没有它，或者它不能按区域遍历</summary>
        public static int StructureRegionSize(int structureType, int mcVersion)
            => Available ? Native.StructureRegionSize(structureType, mcVersion) : 0;

        /// <summary>
        /// 这个尝试点是否真的会生成结构（要塞 / 生物群系条件都在这步过滤）。
        /// 调用前必须先 Setup() + ApplySeed()，而且维度要和结构所在维度一致。
        /// </summary>
        public static bool IsViableStructure(int structureType, int x, int z)
            => Available && Native.StructureViable(structureType, x, z) != 0;

        /// <summary>
        /// 要塞坐标。要塞不像其它结构那样有 region 网格（getStructureConfig 对它返回 0），
        /// 只能靠 DLL 里的 StrongholdIter 一个个迭代出来。返回个数，buffer 里是 [x0,z0,x1,z1,…]。
        /// 调用前要先 Setup() + ApplySeed(seed, 0)。
        /// </summary>
        public static int Strongholds(int mcVersion, int maxCount, int[] buffer)
        {
            if (!Available || buffer == null || maxCount <= 0) return 0;
            try
            {
                int cap = Math.Min(maxCount, buffer.Length / 2);
                return cap <= 0 ? 0 : Native.Strongholds(mcVersion, cap, buffer);
            }
            catch (Exception ex)
            {
                Log.Warn("读取要塞坐标失败：" + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// 原生库里的生成器是全局单例：设置版本、设种子、取群系、判结构全在同一个对象上。
        /// 后台渲染线程在跑的时候，别的地方还去动它就会取到错的数据，所以调用方要串行化。
        /// </summary>
        public static readonly object Sync = new object();

        /// <summary>原生接口声明</summary>
        static class Native
        {
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_mc_newest")]
            public static extern int McNewest();

            /* 版本枚举（名单在 DLL 里，升级 cubiomes 不用改 C#） */
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_mc_count")]
            public static extern int McCount();

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_mc_id")]
            public static extern int McId(int index);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_mc_name")]
            public static extern IntPtr McName(int index);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_mc_range")]
            public static extern IntPtr McRange(int index);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_mc_group")]
            public static extern int McGroup(int index);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_setup")]
            public static extern int Setup(int mc, int large);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_apply_seed")]
            public static extern int ApplySeed(long seed, int dim);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_biome_at")]
            public static extern int BiomeAt(int scale, int x, int y, int z);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_slime_chunk")]
            public static extern int SlimeChunk(int cx, int cz);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_biome_name", CharSet = CharSet.Ansi)]
            public static extern IntPtr BiomeName(int mc, int id);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_structure_pos")]
            public static extern int StructurePos(int type, int mc, int regX, int regZ, out int ox, out int oz);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_struct_region_size")]
            public static extern int StructureRegionSize(int type, int mc);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_struct_viable")]
            public static extern int StructureViable(int type, int x, int z);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "luno_strongholds")]
            public static extern int Strongholds(int mc, int max, int[] outv);
        }
    }
}
