using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoiceCraft.App_Core.Services
{
    public class UpdateInfo
    {
        public bool IsUpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = "0.0.1";
        public string LatestVersion { get; set; } = "0.0.1";
        public string ReleaseNotes { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string AssetFileName { get; set; } = "";
    }

    public class UpdateService : IDisposable
    {
        public const string CurrentAppVersion = "0.0.1";
        private readonly HttpClient _httpClient;

        public UpdateService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VoiceCraft-Updater", CurrentAppVersion));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync(string repoPath)
        {
            var result = new UpdateInfo { CurrentVersion = CurrentAppVersion, LatestVersion = CurrentAppVersion };
            if (string.IsNullOrWhiteSpace(repoPath))
                throw new ArgumentException("Vui lòng nhập đường dẫn GitHub Repo (định dạng: Owner/Repo).");

            string cleanRepo = NormalizeRepoPath(repoPath);
            string apiUrl = $"https://api.github.com/repos/{cleanRepo}/releases/latest";

            Logger.Info($"Kiểm tra cập nhật GitHub tại: {apiUrl}");
            using var response = await _httpClient.GetAsync(apiUrl);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result.ReleaseNotes = $"Kho lưu trữ '{cleanRepo}' hiện chưa có bản phát hành (Release) công khai nào trên GitHub.\nPhiên bản hiện tại ({CurrentAppVersion}) là mới nhất.";
                return result;
            }

            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? "0.0.1") : "0.0.1";
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
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
            }

            if (string.IsNullOrEmpty(result.DownloadUrl) && root.TryGetProperty("zipball_url", out var zipProp))
            {
                result.DownloadUrl = zipProp.GetString() ?? "";
                result.AssetFileName = $"VoiceCraft_{latestVerStr}.zip";
            }

            result.IsUpdateAvailable = CompareVersions(latestVerStr, CurrentAppVersion) > 0;
            return result;
        }

        public async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double> progress = null)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.DownloadUrl))
                throw new InvalidOperationException("Không tìm thấy đường dẫn tải gói cập nhật.");

            string updateDir = Path.Combine(Logger.ProjectRoot, "Updates");
            if (!Directory.Exists(updateDir)) Directory.CreateDirectory(updateDir);

            string fileName = string.IsNullOrWhiteSpace(info.AssetFileName)
                ? $"VoiceCraft_Update_{info.LatestVersion}.zip"
                : info.AssetFileName;
            string destPath = Path.Combine(updateDir, fileName);

            using var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    double pct = Math.Round((double)totalRead * 100.0 / totalBytes.Value, 1);
                    progress?.Report(Math.Clamp(pct, 0, 100));
                }
            }

            progress?.Report(100.0);
            Logger.Info($"Đã tải xong bản cập nhật tại: {destPath}");
            return destPath;
        }

        public void OpenUpdateFolderOrFile(string filePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                else
                {
                    string updateDir = Path.Combine(Logger.ProjectRoot, "Updates");
                    if (!Directory.Exists(updateDir)) Directory.CreateDirectory(updateDir);
                    Process.Start("explorer.exe", updateDir);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi mở thư mục cập nhật", ex);
            }
        }

        private static string NormalizeRepoPath(string input)
        {
            string s = input.Trim().TrimEnd('/');
            if (s.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("https://github.com/".Length);
            else if (s.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("http://github.com/".Length);
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            return s.Trim('/');
        }

        private static int CompareVersions(string v1, string v2)
        {
            string CleanVer(string v)
            {
                int dash = v.IndexOf('-');
                return dash >= 0 ? v.Substring(0, dash) : v;
            }
            if (Version.TryParse(CleanVer(v1), out var ver1) && Version.TryParse(CleanVer(v2), out var ver2))
                return ver1.CompareTo(ver2);
            return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() => _httpClient?.Dispose();
    }
}