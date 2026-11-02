# 🎙️ VoiceCraft Studio (v1.0.0)
**Hệ thống Chuyển đổi Văn bản thành Giọng nói Đa Mô hình (Multi-Engine AI TTS), Nhái giọng Zero-Shot & Phòng thu DSP**

Phát triển bởi: **Longtygu**  
Ngôn ngữ & Nền tảng: **C# WPF (.NET 8.0) + Python (FastAPI, ONNX Runtime, PyTorch, Pedalboard DSP)**

---

## ✨ Tính năng nổi bật (v1.0.0)

1. **Hỗ trợ Đa Kiến trúc Mô hình AI (Multi-Engine Model Lab):**
   - **VieNeu-TTS & VietTTS HiFi-GAN:** Tối ưu hóa ngữ điệu 6 thanh điệu Tiếng Việt và độ ấm vòm họng.
   - **Multilingual Neural 24kHz (`Andrew`, `Ava`, `Brian`, `Emma`, `NamMinh`, `HoaiMy`):** Đọc song ngữ **Tiếng Việt & Tiếng Anh** trong cùng một câu bằng duy nhất 1 giọng người thật, kèm cơ chế tự động lưu **Offline Cache (`Models/neural_cache`)**.
   - **Kokoro-82M ONNX (24kHz Offline):** Mô hình trọng số mở đạt điểm tự nhiên hàng đầu trên TTS Arena.
   - **Meta AI MMS-TTS (`facebook/mms-tts-vie`) & Piper VITS High-End (`lessac-high`, `ryan-high`, `LibriTTS-R 904 Speakers`):** Chạy 100% Offline trên CPU, hỗ trợ chọn trực tiếp `Speaker ID` (`0 -> 903`).
   - **Bộ biên dịch âm vị 135.000 từ (`CMU Pronouncing Dictionary`):** Tự động chuyển đổi từ Tiếng Anh và số phiên bản (`0.0.1`) sang âm vị tương đương khi chạy chế độ giữ 1 giọng Offline.

2. **Nhái giọng AI (Zero-Shot Voice Cloning 5-15s):**
   - Trích xuất vân tay âm học (*Cepstral Formant Liftering + Autocorrelation Median F0*) từ bất kỳ file âm thanh mẫu `.wav` / `.mp3` dài 5-15 giây và chuyển đổi âm sắc theo thời gian thực (`Clone Strength: 0% - 100%`).

3. **Bộ lọc Studio EQ lưu riêng cho từng Model (Per-Model DSP Profiles):**
   - Tinh chỉnh độc lập **Bass/Warmth (185Hz)**, **Treble/Presence (3.4kHz)**, **De-Hiss Lowpass Cutoff (4.5kHz - 11.5kHz)** và **VITS Noise Scale**. Tự động nhớ cấu hình riêng cho từng Model trong `settings.json`.

4. **Giao diện Phẳng Windows 10 kết hợp Kính trong suốt Windows 7 Aero:**
   - Cửa sổ `SettingsWindow` tách biệt mở qua icon bánh răng trên Title Bar.
   - Trình phát sóng âm `NAudio` tương tác 2 vạch (**Vạch chính Playhead** + **Vạch phụ nét đứt bám theo chuột** kèm bộ đếm thời gian phụ `00:00.0`).
   - Tích hợp sẵn hệ thống kiểm tra và tải bản cập nhật tự động qua **GitHub Releases API (`UpdateService.cs`)**.

---

## 🚀 Hướng dẫn Cài đặt & Chạy dự án

1. **Cài đặt thư viện Python & Tải bộ Model AI:**
   Chạy script dưới đây trong PowerShell tại thư mục gốc của dự án để tự động cài đặt các gói Python và tải các model `.onnx` về thư mục `Models`:
   ```powershell
   .\setup_models.ps1
   ```
2. **Biên dịch & Chạy ứng dụng C# WPF:**
   Mở `VoiceCraft.sln` (hoặc `VoiceCraft.csproj`) bằng **Visual Studio** và nhấn **F5** (hoặc chạy `dotnet run`).