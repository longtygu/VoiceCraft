using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceCraft.App_Core.Services
{
    public class UpdateInfo
    {
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = UpdateService.CurrentAppVersion;
        public string LatestVersion { get; set; } = UpdateService.CurrentAppVersion;
        public string ReleaseNotes { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string AssetFileName { get; set; } = "";
    }

    public class UpdateService : IDisposable
    {
        public const string CurrentAppVersion = "1.0.4";
        public const string LockedGitHubRepoUrl = "https://github.com/longtygu/VoiceCraft";
        public const string LockedGitHubRepoSlug = "longtygu/VoiceCraft";
        private readonly HttpClient _httpClient;

        public UpdateService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VoiceCraft-Updater", CurrentAppVersion));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync(string ignoredRepoPath = null)
        {
            var result = new UpdateInfo { CurrentVersion = CurrentAppVersion, LatestVersion = CurrentAppVersion };
            string cleanRepo = LockedGitHubRepoSlug;
            string apiUrl = $"https://api.github.com/repos/{cleanRepo}/releases/latest";

            Logger.Info($"Kiểm tra cập nhật GitHub (khóa cứng) tại: {apiUrl}");
            using var response = await _httpClient.GetAsync(apiUrl);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result.ReleaseNotes = $"Kho lưu trữ '{LockedGitHubRepoUrl}' hiện chưa có bản phát hành (Release) công khai nào.\nPhiên bản hiện tại (v{CurrentAppVersion}) là mới nhất.";
                return result;
            }

            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? CurrentAppVersion) : CurrentAppVersion;
            string latestVerStr = tagName.TrimStart('v', 'V', ' ');
            result.LatestVersion = latestVerStr;
            result.ReleaseNotes = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? "Không có mô tả chi tiết.") : "";
            result.HtmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? (htmlProp.GetString() ?? "") : "";

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                    string dlUrl = asset.TryGetProperty("browser_download_url", out var dProp) ? (dProp.GetString() ?? "") : "";
                    if (!string.IsNullOrEmpty(dlUrl))
                    {
                        result.DownloadUrl = dlUrl;
                        result.AssetFileName = string.IsNullOrEmpty(name) ? $"VoiceCraft_Update_{latestVerStr}.zip" : name;
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
            }

            if (string.IsNullOrEmpty(result.DownloadUrl) && root.TryGetProperty("zipball_url", out var zipProp))
            {
                result.DownloadUrl = zipProp.GetString() ?? "";
                result.AssetFileName = $"VoiceCraft_Update_{latestVerStr}.zip";
            }

            result.IsUpdateAvailable = CompareVersions(latestVerStr, CurrentAppVersion) > 0;
            return result;
        }

        public async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double> progress = null)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.DownloadUrl))
                throw new InvalidOperationException("Không tìm thấy đường dẫn tải gói cập nhật.");

            string tempRoot = Path.Combine(Path.GetTempPath(), "VoiceCraft_SelfUpdate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempRoot);

            string fileName = string.IsNullOrWhiteSpace(info.AssetFileName)
                ? $"VoiceCraft_Update_{info.LatestVersion}.zip"
                : info.AssetFileName;
            string zipPath = Path.Combine(tempRoot, fileName);

            using (var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double pct = Math.Round((double)totalRead * 90.0 / totalBytes.Value, 1);
                        progress?.Report(Math.Clamp(pct, 0, 90));
                    }
                }
            }

            progress?.Report(95.0);
            string extractDir = Path.Combine(tempRoot, "extracted");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);
            File.Delete(zipPath);

            progress?.Report(100.0);
            ApplyInPlaceUpdateAndRestart(extractDir, tempRoot);
            return extractDir;
        }

        private void ApplyInPlaceUpdateAndRestart(string extractDir, string tempRoot)
        {
            string targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(targetDir, "VoiceCraft.exe");
            int currentPid = Process.GetCurrentProcess().Id;

            string sourceDir = extractDir;
            var subDirs = Directory.GetDirectories(extractDir);
            if (Directory.GetFiles(extractDir, "*.exe").Length == 0 && subDirs.Length == 1)
            {
                sourceDir = subDirs[0];
            }

            string oldUpdatesFolder = Path.Combine(Logger.ProjectRoot, "Updates");
            string updaterBat = Path.Combine(Path.GetTempPath(), $"vc_updater_{currentPid}.cmd");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine($"taskkill /PID {currentPid} /F >nul 2>&1");
            sb.AppendLine("taskkill /IM TTS_Engine.exe /F >nul 2>&1");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine($"xcopy \"{sourceDir}\\*\" \"{targetDir}\\\" /E /Y /C /I /Q >nul 2>&1");
            sb.AppendLine($"if exist \"{oldUpdatesFolder}\" rmdir /S /Q \"{oldUpdatesFolder}\" >nul 2>&1");
            sb.AppendLine($"start \"\" \"{exePath}\"");
            sb.AppendLine($"rmdir /S /Q \"{tempRoot}\" >nul 2>&1");
            sb.AppendLine("del \"%~f0\" >nul 2>&1");

            File.WriteAllText(updaterBat, sb.ToString(), new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{updaterBat}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);

            Application.Current?.Dispatcher?.Invoke(() => Application.Current.Shutdown());
        }

        public void OpenUpdateFolderOrFile(string filePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo(LockedGitHubRepoUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi mở trang GitHub Releases", ex);
            }
        }

        private static int CompareVersions(string v1, string v2)
        {
            if (Version.TryParse(NormalizeVersionString(v1), out var ver1) &&
                Version.TryParse(NormalizeVersionString(v2), out var ver2))
            {
                return ver1.CompareTo(ver2);
            }
            return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeVersionString(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
            string clean = v.Trim().TrimStart('v', 'V');
            int dashIdx = clean.IndexOf('-');
            if (dashIdx > 0) clean = clean.Substring(0, dashIdx);
            var parts = clean.Split('.');
            if (parts.Length == 2) return clean + ".0";
            if (parts.Length == 1) return clean + ".0.0";
            return clean;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}