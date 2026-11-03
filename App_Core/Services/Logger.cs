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
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            LogFilePath = Path.Combine(logDir, $"VoiceCraft_{DateTime.Now:yyyyMMdd}.log");
        }

        private static string ResolveProjectRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. Kiểm tra nếu thư mục chứa cả Models (.onnx) và Engine\main.py (Ưu tiên thư mục gốc dự án khi chạy từ bin\Debug hoặc bin\Release\publish)
            DirectoryInfo dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string enginePy = Path.Combine(dir.FullName, "Engine", "main.py");
                string modelsPath = Path.Combine(dir.FullName, "Models");
                if (File.Exists(enginePy) && Directory.Exists(modelsPath))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            // 2. Nếu chạy bản đóng gói độc lập (chỉ có Engine\main.py cạnh file .exe)
            dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string enginePy = Path.Combine(dir.FullName, "Engine", "main.py");
                if (File.Exists(enginePy))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            // 3. Fallback an toàn
            string devFallback = @"E:\WORK\Microsoft Visual Studio\VoiceCraft";
            if (Directory.Exists(devFallback)) return devFallback;
            return baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string ResolveModelsDirectory()
        {
            string localModels = Path.Combine(ProjectRoot, "Models");
            if (Directory.Exists(localModels) && Directory.GetFiles(localModels, "*.onnx").Length > 0)
                return localModels;

            // Nếu chạy từ thư mục Publish độc lập mà chưa copy Models sang, tự động liên kết kho Models gốc (nếu có trên máy)
            string devModels = @"E:\WORK\Microsoft Visual Studio\VoiceCraft\Models";
            if (Directory.Exists(devModels) && Directory.GetFiles(devModels, "*.onnx").Length > 0)
                return devModels;

            if (!Directory.Exists(localModels)) Directory.CreateDirectory(localModels);
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