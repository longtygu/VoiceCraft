using System;
using System.IO;

namespace VoiceCraft.App_Core.Services
{
    public static class Logger
    {
        public static readonly string ProjectRoot;
        private static readonly string LogFilePath;
        private static readonly object _lock = new object();

        static Logger()
        {
            ProjectRoot = ResolveProjectRoot();
            string logDir = Path.Combine(ProjectRoot, "Logs");
            try { if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir); } catch { }
            LogFilePath = Path.Combine(logDir, $"VoiceCraft_{DateTime.Now:yyyyMMdd}.log");
        }

        private static string ResolveProjectRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Neu dang chay trong Visual Studio (bin\Debug hoac bin\Release nhung KHONG phai thu muc publish)
            if (!baseDir.EndsWith(@"\publish", StringComparison.OrdinalIgnoreCase) &&
                (baseDir.Contains(@"\bin\Debug", StringComparison.OrdinalIgnoreCase) ||
                 baseDir.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase)))
            {
                DirectoryInfo dir = new DirectoryInfo(baseDir);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "VoiceCraft.csproj")) ||
                        (File.Exists(Path.Combine(dir.FullName, "Engine", "main.py")) && Directory.Exists(Path.Combine(dir.FullName, "Models"))))
                    {
                        return dir.FullName;
                    }
                    dir = dir.Parent;
                }
            }

            // Khi chay ban dong goi Release (publish) hoac tren may tinh khac: luon lay thu muc chua VoiceCraft.exe
            return baseDir;
        }

        public static string ResolveModelsDirectory()
        {
            string localModels = Path.Combine(ProjectRoot, "Models");
            try
            {
                if (!Directory.Exists(localModels))
                    Directory.CreateDirectory(localModels);
            }
            catch { }
            return localModels;
        }

        public static void Info(string message) => WriteLog("INFO", message);
        public static void Error(string message, Exception ex = null) =>
            WriteLog("ERROR", ex != null ? $"{message} | Exception: {ex.Message}\n{ex.StackTrace}" : message);

        private static void WriteLog(string level, string message)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}