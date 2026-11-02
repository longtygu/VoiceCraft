using System;
using System.IO;

namespace VoiceCraft.App_Core.Services
{
    public static class Logger
    {
        public static readonly string ProjectRoot;
        private static readonly string LogDir;
        private static readonly string LogFile;

        static Logger()
        {
            ProjectRoot = FindProjectRoot();
            LogDir = Path.Combine(ProjectRoot, "Logs");
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            LogFile = Path.Combine(LogDir, $"VoiceCraft_{DateTime.Now:yyyyMMdd}.log");
        }

        private static string FindProjectRoot()
        {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                if (Directory.Exists(Path.Combine(currentDir, "Engine")) || File.Exists(Path.Combine(currentDir, "VoiceCraft.csproj")))
                    return Path.GetFullPath(currentDir);

                var parent = Directory.GetParent(currentDir);
                if (parent == null) break;
                currentDir = parent.FullName;
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception ex = null)
            => Write("ERROR", $"{message}{(ex != null ? $" | Chi tiết: {ex.Message}" : "")}");

        private static void Write(string level, string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFile, logEntry);
            }
            catch { }
        }
    }
}