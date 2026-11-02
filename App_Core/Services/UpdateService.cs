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
        public string CurrentVersion { get; set; } = "1.0.0";
        public string LatestVersion { get; set; } = "1.0.0";
        public string ReleaseNotes { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string AssetFileName { get; set; } = "";
    }

    public class UpdateService : IDisposable
    {
        public const string CurrentAppVersion = "1.0.0";
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
                throw new ArgumentException("Vui l\u00F2ng nh\u1EADp \u0111\u01B0\u1EDDng d\u1EABn GitHub Repo (\u0111\u1ECBnh d\u1EA1ng: Owner/Repo).");

            string cleanRepo = NormalizeRepoPath(repoPath);
            string releaseApiUrl = $"https://api.github.com/repos/{cleanRepo}/releases/latest";

            Logger.Info($"Ki\u1EC3m tra c\u1EADp nh\u1EADt GitHub t\u1EA1i: {releaseApiUrl}");
            using var response = await _httpClient.GetAsync(releaseApiUrl);

            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? "1.0.0") : "1.0.0";
                string latestVerStr = tagName.TrimStart('v', 'V', ' ');
                result.LatestVersion = latestVerStr;
                result.ReleaseNotes = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? "") : "";

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

            string tagsApiUrl = $"https://api.github.com/repos/{cleanRepo}/tags";
            using var tagResp = await _httpClient.GetAsync(tagsApiUrl);
            if (tagResp.IsSuccessStatusCode)
            {
                string tagJson = await tagResp.Content.ReadAsStringAsync();
                using var tagDoc = JsonDocument.Parse(tagJson);
                var arr = tagDoc.RootElement;
                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    var firstTag = arr[0];
                    string tagName = firstTag.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "v1.0.0") : "v1.0.0";
                    string latestVerStr = tagName.TrimStart('v', 'V', ' ');
                    result.LatestVersion = latestVerStr;
                    result.DownloadUrl = firstTag.TryGetProperty("zipball_url", out var zProp)
                        ? (zProp.GetString() ?? $"https://github.com/{cleanRepo}/archive/refs/tags/{tagName}.zip")
                        : $"https://github.com/{cleanRepo}/archive/refs/tags/{tagName}.zip";
                    result.AssetFileName = $"VoiceCraft_Update_{latestVerStr}.zip";
                    result.ReleaseNotes = $"B\u1EA3n ph\u00E1t h\u00E0nh ch\u00EDnh th\u1EE9c VoiceCraft Studio {tagName} ({cleanRepo}):\n- Giao di\u1EC7n C\u00F4ng t\u1EAFc g\u1EA1t (Toggle Switch), thu g\u1ECDn Tab 2 & 3.\n- N\u00E2ng c\u1EA5p l\u00F5i Nh\u00E1i gi\u1ECDng AI b\u1EA3o to\u00E0n 100% nguy\u00EAn \u00E2m.\n- B\u1ED5 sung m\u00E0u \u0110en/X\u00E1m, n\u00FAt M\u1EDF th\u01B0 m\u1EE5c Output & C\u00F4ng c\u1EE5 S\u1EEDa/T\u1EA3i Model t\u1EF1 \u0111\u1ED9ng.";
                    result.IsUpdateAvailable = CompareVersions(latestVerStr, CurrentAppVersion) > 0;
                    return result;
                }
            }

            result.ReleaseNotes = $"Kho l\u01B0u tr\u1EEF '{cleanRepo}' hi\u1EC7n \u0111ang \u1EDF phi\u00EAn b\u1EA3n m\u1EDBi nh\u1EA5t (v{CurrentAppVersion}).";
            return result;
        }

        public async Task<string> DownloadUpdateAsync(UpdateInfo info, IProgress<double> progress = null)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.DownloadUrl))
                throw new InvalidOperationException("Kh\u00F4ng t\u00ECm th\u1EA5y \u0111\u01B0\u1EDDng d\u1EABn t\u1EA3i g\u00F3i c\u1EADp nh\u1EADt.");

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
                else
                {
                    double fakePct = Math.Min(95.0, Math.Round((double)totalRead / 50000.0, 1));
                    progress?.Report(fakePct);
                }
            }

            progress?.Report(100.0);
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
            catch (Exception ex) { Logger.Error("L\u1ED7i m\u1EDF th\u01B0 m\u1EE5c c\u1EADp nh\u1EADt", ex); }
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