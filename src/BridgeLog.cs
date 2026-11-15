using System;
using System.IO;

namespace CSBridge
{
    public static class BridgeLog
    {
        private static readonly object Lock = new object();
        private static string path;

        public static void Init(string modFolder)
        {
            if (string.IsNullOrEmpty(modFolder))
            {
                modFolder = Path.Combine(
                    Environment.GetEnvironmentVariable("HOME") ?? "",
                    "Git/CSBridge");
            }
            path = Path.Combine(modFolder, "bridge.log");
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Error(string msg, Exception ex)
        {
            Write("ERROR", msg + (ex != null ? ": " + ex : ""));
        }

        private static void Write(string level, string msg)
        {
            try
            {
                if (path == null) Init(null);
                lock (Lock)
                {
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + msg + "\n");
                }
            }
            catch (Exception) { /* logging must never crash the game */ }
        }
    }
}
