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
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl = "http://127.0.0.1:8000";

        public EngineManager()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        }

        private void KillZombieProcesses()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("TTS_Engine"))
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch (Exception ex) { Logger.Error("Lỗi dọn dẹp Zombie Process", ex); }
        }

        public async Task<bool> StartEngineAsync()
        {
            if (await CheckServerAliveAsync()) return true;

            KillZombieProcesses();

            string root = Logger.ProjectRoot;
            string exePath = Path.Combine(root, @"Engine\dist\TTS_Engine\TTS_Engine.exe");
            string pyScriptPath = Path.Combine(root, @"Engine\main.py");
            string modelsDir = Path.Combine(root, "Models");
            string outputDir = Path.Combine(root, "Output");

            Directory.CreateDirectory(modelsDir);
            Directory.CreateDirectory(outputDir);

            ProcessStartInfo psi = null;

            if (File.Exists(exePath))
            {
                Logger.Info($"Khởi chạy Engine từ file đóng gói: {exePath}");
                psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }
            else if (File.Exists(pyScriptPath))
            {
                Logger.Info($"Chưa có TTS_Engine.exe, tự động chạy trực tiếp bằng Python: {pyScriptPath}");
                psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{pyScriptPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(pyScriptPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
            }
            else
            {
                Logger.Error("Không tìm thấy cả TTS_Engine.exe lẫn Engine\\main.py!");
                return false;
            }

            psi.EnvironmentVariables["VOICECRAFT_MODELS_DIR"] = modelsDir;
            psi.EnvironmentVariables["VOICECRAFT_OUTPUT_DIR"] = outputDir;

            try
            {
                _engineProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Logger.Error("Không thể khởi chạy tiến trình Engine", ex);
                return false;
            }

            return await WaitForEngineReadyAsync();
        }

        private async Task<bool> CheckServerAliveAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/ping");
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async Task<bool> WaitForEngineReadyAsync()
        {
            for (int i = 0; i < 20; i++)
            {
                if (_engineProcess != null && _engineProcess.HasExited)
                {
                    Logger.Error($"Tiến trình Engine bị tắt đột ngột với mã thoát: {_engineProcess.ExitCode}");
                    return false;
                }

                if (await CheckServerAliveAsync()) return true;
                await Task.Delay(1000);
            }
            return false;
        }

        public void StopEngine()
        {
            if (_engineProcess != null && !_engineProcess.HasExited)
            {
                try
                {
                    _engineProcess.Kill(true);
                    _engineProcess.WaitForExit(2000);
                }
                catch { }
            }
            KillZombieProcesses();
        }

        public void Dispose()
        {
            StopEngine();
            _httpClient?.Dispose();
            _engineProcess?.Dispose();
        }
    }
}