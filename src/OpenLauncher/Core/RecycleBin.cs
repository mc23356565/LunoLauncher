using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace OpenLauncher.Core
{
    /// <summary>删除到回收站（给用户反悔的机会）。用 .NET Framework 自带的 Microsoft.VisualBasic，32/64 位都稳。</summary>
    public static class RecycleBin
    {
        public static void Delete(string path)
        {
            if (File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else if (Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
    }
}
