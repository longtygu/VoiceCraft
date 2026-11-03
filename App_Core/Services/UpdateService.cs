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
        public const string CurrentAppVersion = "1.0.1";
        private readonly HttpClient _httpClient;

        public UpdateService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
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

                string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? CurrentAppVersion) : CurrentAppVersion;
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
                    string tagName = firstTag.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "v1.0.1") : "v1.0.1";
                    string latestVerStr = tagName.TrimStart('v', 'V', ' ');
                    result.LatestVersion = latestVerStr;
                    result.DownloadUrl = firstTag.TryGetProperty("zipball_url", out var zProp)
                        ? (zProp.GetString() ?? $"https://github.com/{cleanRepo}/archive/refs/tags/{tagName}.zip")
                        : $"https://github.com/{cleanRepo}/archive/refs/tags/{tagName}.zip";
                    result.AssetFileName = $"VoiceCraft_Update_{latestVerStr}.zip";
                    result.ReleaseNotes = $"B\u1EA3n c\u1EADp nh\u1EADt VoiceCraft Studio v{latestVerStr}:\n- T\u1EF1 \u0111\u1ED9ng thay th\u1EBF th\u1EB3ng file \u1EE9ng d\u1EE5ng, d\u1ECDn s\u1EA1ch file t\u1EA1m v\u00E0 kh\u1EDFi \u0111\u1ED9ng l\u1EA1i ngay.\n- Kh\u1EAFc ph\u1EE5c tri\u1EC7t \u0111\u1EC3 l\u1ED7i k\u1EBFt n\u1ED1i Engine (127.0.0.1:8000) khi ch\u1EA1y b\u1EA3n Release.";
                    result.IsUpdateAvailable = CompareVersions(latestVerStr, CurrentAppVersion) > 0;
                    return result;
                }
            }

            result.ReleaseNotes = $"Kho l\u01B0u tr\u1EEF '{cleanRepo}' hi\u1EC7n \u0111ang \u1EDF phi\u00EAn b\u1EA3n m\u1EDBi nh\u1EA5t (v{CurrentAppVersion}).";
            return result;
        }

        /// <summary>
        /// T\u1EA3i g\u00F3i c\u1EADp nh\u1EADt v\u00E0o %TEMP%, gi\u1EA3i n\u00E9n, ghi \u0111\u00E8 th\u1EB3ng v\u00E0o \u1EE9ng d\u1EE5ng \u0111ang ch\u1EA1y,
        /// x\u00F3a s\u1EA1ch to\u00E0n b\u1ED9 file t\u1EA1m (kh\u00F4ng \u0111\u1EC3 l\u1EA1i r\u00E1c) v\u00E0 t\u1EF1 \u0111\u1ED9ng kh\u1EDFi \u0111\u1ED9ng l\u1EA1i App.
        /// </summary>
        public async Task DownloadAndApplyUpdateAsync(UpdateInfo info, IProgress<double> progress = null, Action<string> statusCallback = null)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.DownloadUrl))
                throw new InvalidOperationException("Kh\u00F4ng t\u00ECm th\u1EA5y \u0111\u01B0\u1EDDng d\u1EABn t\u1EA3i b\u1EA3n c\u1EADp nh\u1EADt.");

            string tempRoot = Path.Combine(Path.GetTempPath(), "VoiceCraft_AutoUpdate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempRoot);
            string tempZip = Path.Combine(tempRoot, "update_pkg.zip");
            string extractDir = Path.Combine(tempRoot, "extracted");

            statusCallback?.Invoke($"\u23F3 \u0110ang t\u1EA3i b\u1EA3n c\u1EADp nh\u1EADt v{info.LatestVersion}...");
            using (var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

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
                    else
                    {
                        double fakePct = Math.Min(88.0, Math.Round((double)totalRead / 45000.0, 1));
                        progress?.Report(fakePct);
                    }
                }
            }

            progress?.Report(92.0);
            statusCallback?.Invoke("\u26A1 \u0110ang gi\u1EA3i n\u00E9n v\u00E0 chu\u1EA9n b\u1ECB thay th\u1EBF file tr\u1EF1c ti\u1EBFp...");
            ZipFile.ExtractToDirectory(tempZip, extractDir, true);
            File.Delete(tempZip); // X\u00F3a ngay file .zip t\u1EA1m sau khi gi\u1EA3i n\u00E9n

            // Unwrap th\u01B0 m\u1EE5c con n\u1EBFu l\u00E0 g\u00F3i zipball t\u1EEB GitHub
            string sourceContentDir = extractDir;
            var subDirs = Directory.GetDirectories(extractDir);
            var rootFiles = Directory.GetFiles(extractDir);
            if (subDirs.Length == 1 && rootFiles.Length == 0)
            {
                sourceContentDir = subDirs[0];
            }

            progress?.Report(100.0);
            statusCallback?.Invoke("\u2705 \u0110ang t\u1EF1 \u0111\u1ED9ng thay th\u1EBF file, d\u1ECDn r\u00E1c v\u00E0 kh\u1EDFi \u0111\u1ED9ng l\u1EA1i...");
            await Task.Delay(350);

            string currentExePath = Process.GetCurrentProcess().MainModule?.FileName
                                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VoiceCraft.exe");
            string currentAppDir = Path.GetDirectoryName(currentExePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string projectRoot = Logger.ProjectRoot;
            string updatesJunkDir = Path.Combine(projectRoot, "Updates");
            int currentPid = Process.GetCurrentProcess().Id;

            string psUpdaterPath = Path.Combine(Path.GetTempPath(), $"vc_hot_swap_{currentPid}.ps1");
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine($"$pidToWait = {currentPid}");
            sb.AppendLine($"$srcDir = '{sourceContentDir}'");
            sb.AppendLine($"$tempRoot = '{tempRoot}'");
            sb.AppendLine($"$appDir = '{currentAppDir.TrimEnd('\\')}'");
            sb.AppendLine($"$projRoot = '{projectRoot.TrimEnd('\\')}'");
            sb.AppendLine($"$exePath = '{currentExePath}'");
            sb.AppendLine($"$junkUpdatesDir = '{updatesJunkDir}'");
            sb.AppendLine(@"
for ($i = 0; $i -lt 30; $i++) {
    if (!(Get-Process -Id $pidToWait -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 200
}
Stop-Process -Id $pidToWait -Force -ErrorAction SilentlyContinue
Get-Process -Name 'TTS_Engine' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-NetTCPConnection -LocalPort 8000 -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 300

# 1. Neu goi cap nhat co thu muc ReleaseBin (chua file .exe/.dll da bien dich san)
$relBin = Join-Path $srcDir 'ReleaseBin'
if (Test-Path (Join-Path $relBin 'VoiceCraft.exe')) {
    Copy-Item -Path ""$relBin\*"" -Destination $appDir -Recurse -Force
    if (Test-Path (Join-Path $srcDir 'Engine\main.py')) {
        $destEng = Join-Path $appDir 'Engine'
        New-Item -ItemType Directory -Path $destEng -Force | Out-Null
        Copy-Item -Path (Join-Path $srcDir 'Engine\main.py') -Destination (Join-Path $destEng 'main.py') -Force
    }
    # Cap nhat dong bo ma nguon trong ProjectRoot neu dang chay tren may Dev
    if (Test-Path (Join-Path $projRoot 'VoiceCraft.csproj')) {
        Get-ChildItem -Path $srcDir -Exclude 'Models','Output','Logs','Updates','.git','bin','obj' | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination $projRoot -Recurse -Force
        }
        $dbgDir = Join-Path $projRoot 'bin\Debug\net8.0-windows'
        if (Test-Path $dbgDir) { Copy-Item -Path ""$relBin\*"" -Destination $dbgDir -Recurse -Force }
        $pubDir = Join-Path $projRoot 'bin\Release\net8.0-windows\publish'
        if (Test-Path $pubDir) { Copy-Item -Path ""$relBin\*"" -Destination $pubDir -Recurse -Force }
    }
}
elseif (Test-Path (Join-Path $srcDir 'VoiceCraft.exe')) {
    # 2. Neu goi cap nhat la ban Portable Binary truc tiep
    Copy-Item -Path ""$srcDir\*"" -Destination $appDir -Recurse -Force
}
else {
    # 3. Neu goi cap nhat chi chua Source Code -> Copy source va build truc tiep
    Get-ChildItem -Path $srcDir -Exclude 'Models','Output','Logs','Updates','.git','bin','obj' | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $projRoot -Recurse -Force
    }
    Set-Location $projRoot
    & dotnet build -c Debug -o $appDir | Out-Null
}

# 4. Xoa sach thu muc Updates rac va thu muc Temp (Zero-Junk)
if (Test-Path $junkUpdatesDir) { Remove-Item -Path $junkUpdatesDir -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $tempRoot) { Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }

# 5. Khoi dong lai ung dung ngay lap tuc
Start-Process -FilePath $exePath -WorkingDirectory $appDir
Remove-Item -Path $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
");

            File.WriteAllText(psUpdaterPath, sb.ToString(), new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{psUpdaterPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);

            Application.Current?.Dispatcher?.Invoke(() => Application.Current.Shutdown());
        }

        public void OpenUpdateFolderOrFile(string filePath)
        {
            try
            {
                string folder = Logger.ProjectRoot;
                Process.Start("explorer.exe", folder);
            }
            catch (Exception ex) { Logger.Error("L\u1ED7i m\u1EDF th\u01B0 m\u1EE5c", ex); }
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