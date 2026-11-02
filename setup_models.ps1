$modelsDir = Join-Path $PSScriptRoot "Models"
$kokoroDir = Join-Path $modelsDir "kokoro_24k"
$mmsDir = Join-Path $modelsDir "mms_vie_male"
New-Item -ItemType Directory -Path $modelsDir -Force | Out-Null
New-Item -ItemType Directory -Path $kokoroDir -Force | Out-Null

Write-Host "[1/4] Installing Python packages..." -ForegroundColor Cyan
& python -m pip install edge-tts kokoro-onnx miniaudio pedalboard cmudict scipy numpy transformers torch piper-tts onnxruntime fastapi uvicorn pydantic

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host "[2/4] Downloading Kokoro-82M 24kHz Offline models..." -ForegroundColor Cyan
$kokoroOnnx = Join-Path $kokoroDir "kokoro-v1.0.onnx"
$kokoroVoices = Join-Path $kokoroDir "voices-v1.0.bin"
if (!(Test-Path $kokoroOnnx)) {
    & curl.exe -L "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx" -o $kokoroOnnx
}
if (!(Test-Path $kokoroVoices)) {
    & curl.exe -L "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin" -o $kokoroVoices
}

Write-Host "[3/4] Downloading Piper ONNX Vietnamese & English models..." -ForegroundColor Cyan
$models = @(
    @{ Prefix="bac"; Name="vi_VN-vais1000-medium"; Base="https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium" },
    @{ Prefix="trung"; Name="vi_VN-25hours_single-low"; Base="https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/25hours_single/low/vi_VN-25hours_single-low" },
    @{ Prefix="nam"; Name="vi_VN-vivos-x_low"; Base="https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low" },
    @{ Prefix="en_female_hi"; Name="en_US-lessac-high"; Base="https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/high/en_US-lessac-high" },
    @{ Prefix="en_male_hi"; Name="en_US-ryan-high"; Base="https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/ryan/high/en_US-ryan-high" }
)
foreach ($m in $models) {
    $onnx = Join-Path $modelsDir "$($m.Prefix)_$($m.Name).onnx"
    $json = "$onnx.json"
    if (!(Test-Path $onnx)) { & curl.exe -L "$($m.Base).onnx?download=true" -o $onnx }
    if (!(Test-Path $json)) { & curl.exe -L "$($m.Base).onnx.json?download=true" -o $json }
}

if (!(Test-Path (Join-Path $mmsDir "config.json"))) {
    Write-Host "[4/4] Downloading Meta AI MMS Vietnamese Male model..." -ForegroundColor Cyan
    & python -c "from transformers import VitsModel, AutoTokenizer; m='facebook/mms-tts-vie'; d=r'$mmsDir'; AutoTokenizer.from_pretrained(m).save_pretrained(d); VitsModel.from_pretrained(m).save_pretrained(d)"
}

Write-Host "[DONE] All models installed successfully!" -ForegroundColor Green