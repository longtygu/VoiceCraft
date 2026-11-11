# ==============================================================================
# VOICECRAFT ALL-IN-ONE RELEASE & GITHUB PUBLISH SCRIPT (package_release.ps1)
# Tự động: Đồng bộ Version -> Build Release -> Nén & Kiểm định ZIP (PKZIP EOCD)
#          -> Trả trạng thái Debug cho Visual Studio -> Đẩy lên GitHub Releases
# ==============================================================================
param(
    [string]$Version = "1.0.5",
    [string]$ReleaseNotes = "- Cập nhật và tối ưu hóa hệ thống VoiceCraft Audio Studio.`n- Chuẩn hóa gói nén PKZIP (EOCD) tương thích cập nhật tự động (OTA)."
)

$ErrorActionPreference = "Stop"
$projectRoot = "E:\WORK\Microsoft Visual Studio\VoiceCraft"
$repoSlug    = "longtygu/VoiceCraft"
$repoUrl     = "https://github.com/longtygu/VoiceCraft.git"
$tagName     = "v$Version"

$publishDir  = Join-Path $projectRoot "bin\Release\net8.0-windows\publish"
$updatesDir  = Join-Path $projectRoot "Updates"
$desktop     = [Environment]::GetFolderPath("Desktop")
$workDir     = Join-Path $desktop "FixVoiceCraft"
$sendDir     = if (Test-Path (Join-Path $workDir " File send")) { Join-Path $workDir " File send" } else { Join-Path $workDir "File send" }

$utf8Bom     = New-Object System.Text.UTF8Encoding($true)

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "   BẮT ĐẦU QUY TRÌNH ĐÓNG GÓI & PUBLISH VOICECRAFT $tagName" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

# ------------------------------------------------------------------------------
# [1/7] ĐÓNG TIẾN TRÌNH ĐANG CHẠY & ĐỒNG BỘ SỐ PHIÊN BẢN TRONG MÃ NGUỒN
# ------------------------------------------------------------------------------
Write-Host "[1/7] Đang đóng tiến trình cũ và đồng bộ mã phiên bản v$Version..." -ForegroundColor Cyan
Get-Process -Name "VoiceCraft" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "TTS_Engine" -ErrorAction SilentlyContinue | Stop-Process -Force
try {
    Get-NetTCPConnection -LocalPort 8000 -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue
    }
} catch { }

$usPath = Join-Path $projectRoot "App_Core\Services\UpdateService.cs"
if (Test-Path $usPath) {
    $usText = [System.IO.File]::ReadAllText($usPath, [System.Text.Encoding]::UTF8)
    $usText = [regex]::Replace($usText, 'CurrentAppVersion\s*=\s*"[^"]+"', "CurrentAppVersion = `"$Version`"")
    [System.IO.File]::WriteAllText($usPath, $usText, $utf8Bom)
}

foreach ($xamlName in @("MainWindow.xaml", "SettingsWindow.xaml")) {
    $xPath = Join-Path $projectRoot $xamlName
    if (Test-Path $xPath) {
        $xText = [System.IO.File]::ReadAllText($xPath, [System.Text.Encoding]::UTF8)
        $xText = [regex]::Replace($xText, 'FallbackValue=v\d+\.\d+\.\d+', "FallbackValue=v$Version")
        [System.IO.File]::WriteAllText($xPath, $xText, $utf8Bom)
    }
}

# ------------------------------------------------------------------------------
# [2/7] BIÊN DỊCH BẢN RELEASE & CHUẨN BỊ THƯ MỤC PUBLISH
# ------------------------------------------------------------------------------
Write-Host "[2/7] Đang biên dịch dự án ở chế độ Release..." -ForegroundColor Cyan
Set-Location $projectRoot
Remove-Item -Path $publishDir -Recurse -Force -ErrorAction SilentlyContinue
& dotnet restore | Out-Null
& dotnet publish -c Release -o $publishDir | Out-Null

# Chép Engine\main.py vào gói Publish
$destEngine = Join-Path $publishDir "Engine"
New-Item -ItemType Directory -Path $destEngine -Force | Out-Null
Copy-Item -Path (Join-Path $projectRoot "Engine\main.py") -Destination $destEngine -Force

# Khởi tạo thư mục Models & Output và chép sẵn 1 model Tiếng Việt siêu nhẹ (~28MB)
# để gói cập nhật OTA tải nhanh và tương thích với giới hạn timeout của các bản cũ
$pubModelsDir = Join-Path $publishDir "Models"
$pubOutputDir = Join-Path $publishDir "Output"
New-Item -ItemType Directory -Path $pubModelsDir -Force | Out-Null
New-Item -ItemType Directory -Path $pubOutputDir -Force | Out-Null

$devModelsDir = Join-Path $projectRoot "Models"
$lightModel = Join-Path $devModelsDir "nam_vi_VN-vivos-x_low.onnx"
if (Test-Path $lightModel) {
    Copy-Item $lightModel (Join-Path $pubModelsDir "nam_vi_VN-vivos-x_low.onnx") -Force
    if (Test-Path "$lightModel.json") {
        Copy-Item "$lightModel.json" (Join-Path $pubModelsDir "nam_vi_VN-vivos-x_low.onnx.json") -Force
    }
}

# ------------------------------------------------------------------------------
# [3/7] NÉN GÓI ZIP BẰNG .NET ZIPFILE API & KIỂM ĐỊNH CHỮ KÝ EOCD
# ------------------------------------------------------------------------------
Write-Host "[3/7] Đang đóng gói và kiểm định cấu trúc PKZIP (EOCD)..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $updatesDir -Force | Out-Null
New-Item -ItemType Directory -Path $sendDir -Force | Out-Null

$zipFileName = "VoiceCraft_Update_$Version.zip"
$zipUpdates  = Join-Path $updatesDir $zipFileName
$zipSend     = Join-Path $sendDir $zipFileName

if (Test-Path $zipUpdates) { Remove-Item $zipUpdates -Force }
if (Test-Path $zipSend)    { Remove-Item $zipSend -Force }

[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDir,
    $zipUpdates,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

# Kiểm định trực tiếp bản ghi End of Central Directory (EOCD)
$zipArchive = [System.IO.Compression.ZipFile]::OpenRead($zipUpdates)
$entryCount = $zipArchive.Entries.Count
if ($entryCount -eq 0) {
    $zipArchive.Dispose()
    throw "Gói ZIP tạo ra bị trống (0 entries)!"
}
$zipArchive.Dispose()

$sizeMb = [Math]::Round((Get-Item $zipUpdates).Length / 1MB, 2)
Copy-Item -Path $zipUpdates -Destination $zipSend -Force
Write-Host "  -> Kiểm định ZIP thành công: $sizeMb MB ($entryCount files, EOCD hợp lệ)." -ForegroundColor Green

# ------------------------------------------------------------------------------
# [4/7] TRẢ TRẠNG THÁI VỀ DEBUG CHO VISUAL STUDIO (CHỐNG CẢNH BÁO JUST MY CODE)
# ------------------------------------------------------------------------------
Write-Host "[4/7] Đang biên dịch lại chế độ Debug cho Visual Studio..." -ForegroundColor Cyan
& dotnet build -c Debug | Out-Null
foreach ($cfg in @("Debug", "Release")) {
    $cfgEngineDir = Join-Path $projectRoot "bin\$cfg\net8.0-windows\Engine"
    New-Item -ItemType Directory -Path $cfgEngineDir -Force | Out-Null
    Copy-Item -Path (Join-Path $projectRoot "Engine\main.py") -Destination $cfgEngineDir -Force
}

# ------------------------------------------------------------------------------
# [5/7] KIỂM TRA & XÁC THỰC GITHUB CLI (GH)
# ------------------------------------------------------------------------------
Write-Host "[5/7] Đang kiểm tra công cụ GitHub CLI (gh)..." -ForegroundColor Cyan
if (-not (Get-Command "gh" -ErrorAction SilentlyContinue)) {
    Write-Host "  -> Đang cài đặt GitHub CLI tự động qua winget..." -ForegroundColor Yellow
    winget install --id GitHub.cli --accept-source-agreements --accept-package-agreements
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
}

$ErrorActionPreference = "Continue"
& gh auth status 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  -> Vui lòng xác thực tài khoản GitHub trên trình duyệt..." -ForegroundColor Yellow
    & gh auth login --hostname github.com --git-protocol https --web
}

# ------------------------------------------------------------------------------
# [6/7] ĐỒNG BỘ MÃ NGUỒN VÀ GIT TAG LÊN GITHUB
# ------------------------------------------------------------------------------
Write-Host "[6/7] Đang đẩy mã nguồn và Git Tag $tagName lên GitHub..." -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $projectRoot ".git"))) {
    & git init
    & git branch -M main
}
$remotes = & git remote
if ($remotes -contains "origin") {
    & git remote set-url origin $repoUrl
} else {
    & git remote add origin $repoUrl
}

& git add .
& git commit -m "Release VoiceCraft $tagName" 2>$null
& git tag -f $tagName
& git push -u origin main --force
& git push origin $tagName --force

# ------------------------------------------------------------------------------
# [7/7] TẠO MỚI HOẶC GHI ĐÈ (--CLOBBER) GÓI ZIP TRÊN GITHUB RELEASES
# ------------------------------------------------------------------------------
Write-Host "[7/7] Đang tải tệp $zipFileName lên GitHub Releases ($repoSlug)..." -ForegroundColor Cyan
& gh release view $tagName --repo $repoSlug >$null 2>&1
$releaseExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = "Stop"

if ($releaseExists) {
    & gh release upload $tagName "$zipUpdates" --clobber --repo $repoSlug
    & gh release edit $tagName --title "VoiceCraft $tagName" --notes "$ReleaseNotes" --latest --repo $repoSlug
    Write-Host "  -> Đã ghi đè (--clobber) thành công gói cập nhật lên Release $tagName!" -ForegroundColor Green
} else {
    & gh release create $tagName "$zipUpdates" --title "VoiceCraft $tagName" --notes "$ReleaseNotes" --latest --repo $repoSlug
    Write-Host "  -> Đã khởi tạo thành công Release $tagName mới nhất trên GitHub!" -ForegroundColor Green
}

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "[HOÀN TẤT 100%] Phiên bản VoiceCraft $tagName đã được phát hành!" -ForegroundColor Green
Write-Host "  -> File ZIP cục bộ: $zipUpdates ($sizeMb MB)" -ForegroundColor Yellow
Write-Host "  -> URL GitHub: https://github.com/$repoSlug/releases/tag/$tagName" -ForegroundColor Yellow
Write-Host "==================================================================" -ForegroundColor Green

& gh release view $tagName --repo $repoSlug --web
