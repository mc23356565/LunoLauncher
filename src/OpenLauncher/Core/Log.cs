using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenLauncher.Core
{
    public static class Log
    {
        static readonly object Lock = new object();
        static readonly string FilePath;
        static readonly LinkedList<string> Recent = new LinkedList<string>();

        static Log()
        {
            FilePath = Path.Combine(Paths.Logs, "launcher.log");
            try
            {
                // 保留上一次的日志
                if (File.Exists(FilePath))
                {
                    var prev = Path.Combine(Paths.Logs, "launcher.prev.log");
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(FilePath, prev);
                }
            }
            catch { }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", ex == null ? msg : $"{msg}\n{ex}");

        static void Write(string level, string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
            lock (Lock)
            {
                Recent.AddLast(line);
                while (Recent.Count > 500) Recent.RemoveFirst();
                try { File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
            System.Diagnostics.Debug.WriteLine(line);
        }

        public static string RecentText()
        {
            lock (Lock) return string.Join(Environment.NewLine, Recent);
        }
    }
}
