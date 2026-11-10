using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                if (!res.IsSuccessStatusCode) return false;
                string body = await res.Content.ReadAsStringAsync();
                return body.Contains("\"ready\"") || body.Contains("4.0_capcut_dsp");
            }
            catch
            {
                return false;
            }
        }

        private void KillExistingPort8000Process()
        {
            try
            {
                if (_engineProcess != null && !_engineProcess.HasExited)
                {
                    _engineProcess.Kill(true);
                    _engineProcess.Dispose();
                    _engineProcess = null;
                }
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"for /f \"tokens=5\" %a in ('netstat -aon ^| findstr :8000 ^| findstr LISTENING') do taskkill /F /PID %a\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
            }
            catch { }
        }

        private static string FindEngineScriptPath(string root)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidates = new List<string>
            {
                Path.Combine(root, "Engine", "main.py"),
                Path.Combine(baseDir, "Engine", "main.py")
            };

            DirectoryInfo dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                candidates.Add(Path.Combine(dir.FullName, "Engine", "main.py"));
                dir = dir.Parent;
            }

            foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(c))
                {
                    // Tu dong dong bo ban sao Engine\main.py vao BaseDirectory\Engine\main.py
                    try
                    {
                        string localEngineDir = Path.Combine(baseDir, "Engine");
                        string localMainPy = Path.Combine(localEngineDir, "main.py");
                        if (!File.Exists(localMainPy))
                        {
                            Directory.CreateDirectory(localEngineDir);
                            File.Copy(c, localMainPy, true);
                        }
                    }
                    catch { }
                    return c;
                }
            }
            return null;
        }

        private static string FindBundledEngineExe(string root)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidates = new List<string>
            {
                Path.Combine(root, "Engine", "TTS_Engine.exe"),
                Path.Combine(root, "TTS_Engine.exe"),
                Path.Combine(baseDir, "Engine", "TTS_Engine.exe"),
                Path.Combine(baseDir, "TTS_Engine.exe")
            };

            DirectoryInfo dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                candidates.Add(Path.Combine(dir.FullName, "Engine", "TTS_Engine.exe"));
                candidates.Add(Path.Combine(dir.FullName, "TTS_Engine", "TTS_Engine.exe"));
                dir = dir.Parent;
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        private static string ResolvePythonExecutable(string root)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var venvCandidates = new List<string>
            {
                Path.Combine(root, ".venv", "Scripts", "python.exe"),
                Path.Combine(root, "venv", "Scripts", "python.exe"),
                Path.Combine(baseDir, ".venv", "Scripts", "python.exe"),
                Path.Combine(baseDir, "venv", "Scripts", "python.exe")
            };

            foreach (var v in venvCandidates)
            {
                if (File.Exists(v)) return v;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pyRoot = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(pyRoot))
            {
                var pyDirs = Directory.GetDirectories(pyRoot, "Python3*").OrderByDescending(d => d);
                foreach (var d in pyDirs)
                {
                    string exe = Path.Combine(d, "python.exe");
                    if (File.Exists(exe)) return exe;
                }
            }

            return "python";
        }

        private async Task<bool> LaunchAndWaitAsync(ProcessStartInfo psi, int maxPolls = 35)
        {
            try
            {
                _engineProcess = Process.Start(psi);
                if (_engineProcess == null) return false;

                _engineProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Logger.Info($"[Engine-Out] {e.Data}");
                };
                _engineProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Logger.Info($"[Engine-Err] {e.Data}");
                };

                _engineProcess.BeginOutputReadLine();
                _engineProcess.BeginErrorReadLine();

                for (int i = 0; i < maxPolls; i++)
                {
                    if (await IsServerRunningAsync())
                        return true;
                    if (_engineProcess.HasExited)
                    {
                        Logger.Error($"Tien trinh Engine da thoat som voi ExitCode = {_engineProcess.ExitCode}");
                        break;
                    }
                    await Task.Delay(300);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Loi khi khoi chay tien trinh Engine", ex);
            }
            return false;
        }

        public async Task<bool> StartEngineAsync(bool forceRestart = false)
        {
            string root = Logger.ProjectRoot;
            string modelsDir = Logger.ResolveModelsDirectory();
            string outputDir = Path.Combine(root, "Output");
            Directory.CreateDirectory(modelsDir);
            Directory.CreateDirectory(outputDir);

            if (!forceRestart && await IsServerRunningAsync())
            {
                Logger.Info($"FastAPI Engine da san sang tai 127.0.0.1:8000 (ModelsDir: {modelsDir}).");
                return true;
            }

            KillExistingPort8000Process();

            string scriptPath = FindEngineScriptPath(root);
            string bundledExe = FindBundledEngineExe(root);

            // Nhanh 1: Khoi chay qua Python + Engine\main.py
            if (!string.IsNullOrEmpty(scriptPath) && File.Exists(scriptPath))
            {
                string pyExe = ResolvePythonExecutable(root);
                Logger.Info($"Dang khoi dong Engine bang '{pyExe}' voi script: {scriptPath}");

                var psiPy = new ProcessStartInfo
                {
                    FileName = pyExe,
                    Arguments = $"\"{scriptPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(scriptPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psiPy.EnvironmentVariables["VOICECRAFT_MODELS_DIR"] = modelsDir;
                psiPy.EnvironmentVariables["VOICECRAFT_OUTPUT_DIR"] = outputDir;
                psiPy.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                if (await LaunchAndWaitAsync(psiPy, 35))
                {
                    Logger.Info("Khoi dong Python FastAPI Engine thanh cong!");
                    return true;
                }
            }

            // Nhanh 2: Fallback tu dong sang TTS_Engine.exe neu tren may khach chua cai Python hoac thieu thu vien
            if (!string.IsNullOrEmpty(bundledExe) && File.Exists(bundledExe))
            {
                Logger.Info($"Dang fallback sang chay doc lap TTS_Engine.exe tai: {bundledExe}");
                KillExistingPort8000Process();

                var psiExe = new ProcessStartInfo
                {
                    FileName = bundledExe,
                    WorkingDirectory = Path.GetDirectoryName(bundledExe),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psiExe.EnvironmentVariables["VOICECRAFT_MODELS_DIR"] = modelsDir;
                psiExe.EnvironmentVariables["VOICECRAFT_OUTPUT_DIR"] = outputDir;
                psiExe.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                if (await LaunchAndWaitAsync(psiExe, 35))
                {
                    Logger.Info("Khoi dong TTS_Engine.exe thanh cong!");
                    return true;
                }
            }

            Logger.Error("Khong the khoi dong TTS Engine qua ca Python lan TTS_Engine.exe.");
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