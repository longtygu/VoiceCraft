@echo off
cd /d "%~dp0"
pip install fastapi uvicorn pydantic pyinstaller piper-tts onnxruntime
pyinstaller --noconfirm --onedir --name "TTS_Engine" --collect-all piper --collect-all piper_phonemize --collect-all onnxruntime main.py
echo Build hoan tat!