using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace VoiceCraft.App_Core.Services
{
    public class EngineManager : IDisposable
    {
        private Process _engineProcess;
        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private const string PingUrl = "http://127.0.0.1:8000/ping";

        public async Task<bool> IsServerRunningAsync()
        {
            try
            {
                var res = await _httpClient.GetAsync(PingUrl);
                return res.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> StartEngineAsync()
        {
            if (await IsServerRunningAsync())
            {
                Logger.Info("FastAPI Engine đã hoạt động sẵn tại 127.0.0.1:8000.");
                return true;
            }

            try
            {
                string root = Logger.ProjectRoot;
                string modelsDir = Logger.ResolveModelsDirectory();
                string outputDir = Path.Combine(root, "Output");
                Directory.CreateDirectory(outputDir);

                // Tìm main.py tại thư mục hiện tại, ProjectRoot hoặc thư mục gốc
                string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engine", "main.py");
                if (!File.Exists(scriptPath))
                    scriptPath = Path.Combine(root, "Engine", "main.py");
                if (!File.Exists(scriptPath))
                    scriptPath = @"E:\WORK\Microsoft Visual Studio\VoiceCraft\Engine\main.py";

                if (!File.Exists(scriptPath))
                {
                    Logger.Error($"Không tìm thấy Engine\\main.py tại: {scriptPath}");
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(scriptPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                psi.EnvironmentVariables["VOICECRAFT_MODELS_DIR"] = modelsDir;
                psi.EnvironmentVariables["VOICECRAFT_OUTPUT_DIR"] = outputDir;
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                _engineProcess = Process.Start(psi);
                if (_engineProcess != null)
                {
                    _engineProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                            Logger.Info($"[Engine] {e.Data}");
                    };
                    _engineProcess.BeginErrorReadLine();
                }

                for (int i = 0; i < 40; i++)
                {
                    if (await IsServerRunningAsync())
                        return true;
                    await Task.Delay(350);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi khởi động Python Engine", ex);
            }

            return false;
        }

        public void Dispose()
        {
            try
            {
                if (_engineProcess != null && !_engineProcess.HasExited)
                {
                    _engineProcess.Kill(true);
                    _engineProcess.Dispose();
                }
            }
            catch { }
            _httpClient?.Dispose();
        }
    }
}