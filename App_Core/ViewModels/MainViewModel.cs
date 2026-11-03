using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using VoiceCraft.App_Core.Models;
using VoiceCraft.App_Core.Services;

namespace VoiceCraft.App_Core.ViewModels
{
    public class ModelDspProfile
    {
        public double Speed { get; set; } = 1.0;
        public double Pitch { get; set; } = 1.0;
        public double EqBassDb { get; set; } = 0.0;
        public double EqTrebleDb { get; set; } = 0.0;
        public double DeHissCutoff { get; set; } = 10000.0;
        public double VitsNoiseScale { get; set; } = 0.67;
    }

    public class UserSettingsData
    {
        public string SelectedAeroTheme { get; set; }
        public string SelectedAccentItem { get; set; }
        public string SelectedWaveformColorItem { get; set; }
        public string SelectedSecondaryLineColorItem { get; set; }
        public double GlassOpacity { get; set; } = 0.85;
        public double CardOpacity { get; set; } = 0.45;
        public bool ShowAeroReflection { get; set; } = true;
        public string SelectedVoice { get; set; }
        public string SelectedModelFile { get; set; } = "Tự động theo vùng miền (Auto)";
        public int SpeakerId { get; set; } = -1;
        public bool BypassDsp { get; set; } = false;
        public string SelectedGender { get; set; }
        public bool BilingualAuto { get; set; } = true;
        public bool SingleVoiceMode { get; set; } = true;
        public bool AutoPlayAudio { get; set; } = true;
        public double Speed { get; set; } = 1.0;
        public double Pitch { get; set; } = 1.0;
        public double Volume { get; set; } = 100.0;
        public bool IsVoiceCloneEnabled { get; set; } = false;
        public bool IsEqPanelExpanded { get; set; } = false;
        public string CloneReferencePath { get; set; } = "";
        public double CloneStrength { get; set; } = 0.80;
        public double EqBassDb { get; set; } = 0.0;
        public double EqTrebleDb { get; set; } = 0.0;
        public double DeHissCutoff { get; set; } = 10000.0;
        public double VitsNoiseScale { get; set; } = 0.67;
        public Dictionary<string, ModelDspProfile> ModelProfiles { get; set; } = new Dictionary<string, ModelDspProfile>();
        public string GitHubRepo { get; set; } = "longtygu/VoiceCraft";
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        private readonly EngineManager _engineManager;
        private readonly AudioPlayer _audioPlayer;
        private readonly UpdateService _updateService;
        private readonly DispatcherTimer _timer;
        private readonly string _outputDir;
        private readonly string _modelsDir;
        private readonly string _settingsFilePath;
        private string _currentAudioPath;
        private bool _isTimerUpdating;
        private bool _isLoadingSettings;
        private bool _isSwitchingModelProfile;
        private UpdateInfo _latestUpdateInfo;
        private string _downloadedUpdateFilePath;
        private Dictionary<string, ModelDspProfile> _modelProfiles = new Dictionary<string, ModelDspProfile>();

        public string AppVersion => UpdateService.CurrentAppVersion;

        public MainViewModel()
        {
            _engineManager = new EngineManager();
            _audioPlayer = new AudioPlayer();
            _updateService = new UpdateService();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += (s, e) => UpdatePlaybackProgress();
            _audioPlayer.PlaybackStopped += (s, e) =>
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    _timer.Stop();
                    _isTimerUpdating = true;
                    CurrentPosition = TotalDuration;
                    _isTimerUpdating = false;
                    UpdateTimeDisplay();
                });
            };

            _outputDir = Path.Combine(Logger.ProjectRoot, "Output");
            _modelsDir = Logger.ResolveModelsDirectory();
            if (!Directory.Exists(_outputDir)) Directory.CreateDirectory(_outputDir);
            if (!Directory.Exists(_modelsDir)) Directory.CreateDirectory(_modelsDir);
            _settingsFilePath = Path.Combine(Logger.ProjectRoot, "settings.json");

            Logger.Info($"Ứng dụng VoiceCraft v{AppVersion} khởi động.");

            SynthesizeCommand = new RelayCommand(async (o) => await SynthesizeAsync(), (o) => !IsProcessing && !string.IsNullOrWhiteSpace(InputText));
            PlayCommand = new RelayCommand((o) => { _audioPlayer.Play(); _timer.Start(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            PauseCommand = new RelayCommand((o) => { _audioPlayer.Pause(); _timer.Stop(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            StopCommand = new RelayCommand((o) => { _audioPlayer.Stop(); _timer.Stop(); CurrentPosition = 0; UpdateTimeDisplay(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            SaveCommand = new RelayCommand(SaveFile, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));

            PasteTextCommand = new RelayCommand((o) =>
            {
                if (Clipboard.ContainsText())
                    InputText = Clipboard.GetText();
            });
            ClearTextCommand = new RelayCommand((o) => InputText = "");

            SwitchTabCommand = new RelayCommand((param) =>
            {
                string tab = param?.ToString() ?? "Studio";
                IsSettingsOpen = (tab == "Settings");
            });

            RefreshModelsCommand = new RelayCommand((o) => RefreshAvailableModels());

            AdjustSpeakerIdCommand = new RelayCommand((param) =>
            {
                if (int.TryParse(param?.ToString(), out int delta))
                    SpeakerId = Math.Clamp(SpeakerId + delta, -1, 903);
            });

            // Chọn mẫu Nhái giọng và hiển thị ngay Sóng âm thanh lên trình phát phía dưới
            SelectCloneSampleCommand = new RelayCommand((o) =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Chọn file âm thanh mẫu (5 - 15 giây) để Nhái giọng",
                    Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    CloneReferencePath = dlg.FileName;
                    IsVoiceCloneEnabled = true;
                    LoadAudioAndWaveformToTimeline(CloneReferencePath);
                }
            });

            PreviewCloneSampleCommand = new RelayCommand((o) =>
            {
                if (!string.IsNullOrEmpty(CloneReferencePath) && File.Exists(CloneReferencePath))
                {
                    LoadAudioAndWaveformToTimeline(CloneReferencePath);
                    _audioPlayer.Play();
                    _timer.Start();
                }
            }, (o) => !string.IsNullOrEmpty(CloneReferencePath) && File.Exists(CloneReferencePath));

            ClearCloneSampleCommand = new RelayCommand((o) =>
            {
                CloneReferencePath = "";
                IsVoiceCloneEnabled = false;
            });

            ResetEqCommand = new RelayCommand((o) =>
            {
                EqBassDb = 0.0;
                EqTrebleDb = 0.0;
                DeHissCutoff = 10000.0;
                VitsNoiseScale = 0.67;
            });

            AdjustSpeedCommand = new RelayCommand((param) =>
            {
                if (double.TryParse(param?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double delta))
                    Speed = Math.Round(Math.Clamp(Speed + delta, 0.25, 3.0), 2);
            });
            SetSpeedPresetCommand = new RelayCommand((param) =>
            {
                if (double.TryParse(param?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                    Speed = val;
            });

            AdjustPitchCommand = new RelayCommand((param) =>
            {
                if (double.TryParse(param?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double delta))
                    Pitch = Math.Round(Math.Clamp(Pitch + delta, 0.5, 2.0), 2);
            });
            SetPitchPresetCommand = new RelayCommand((param) =>
            {
                if (double.TryParse(param?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                    Pitch = val;
            });

            RestartEngineCommand = new RelayCommand(async (o) => await InitializeEngineAsync());
            OpenFolderCommand = new RelayCommand((param) =>
            {
                string target = param?.ToString();
                string folder = target == "Models" ? _modelsDir
                              : target == "Updates" ? Path.Combine(Logger.ProjectRoot, "Updates")
                              : _outputDir;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", folder);
            });

            ClearCacheCommand = new RelayCommand((o) =>
            {
                try
                {
                    _audioPlayer.Stop();
                    _timer.Stop();
                    int count = 0;
                    if (Directory.Exists(_outputDir))
                    {
                        foreach (var f in Directory.GetFiles(_outputDir, "*.wav"))
                        {
                            try { File.Delete(f); count++; } catch { }
                        }
                    }
                    HistoryList.Clear();
                    _currentAudioPath = null;
                    WaveformPoints = new PointCollection();
                    TotalDuration = 0;
                    CurrentPosition = 0;
                    UpdateTimeDisplay();
                    MessageBox.Show($"Đã dọn dẹp sạch {count} file âm thanh trong thư mục Output!", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { Logger.Error("Lỗi dọn dẹp Output", ex); }
            });

            RepairAndDownloadModelsCommand = new RelayCommand(async (o) => await RepairAndDownloadModelsAsync(), (o) => !IsRepairingModels);
            CheckUpdateCommand = new RelayCommand(async (o) => await CheckForUpdateAsync(), (o) => !IsCheckingUpdate && !IsDownloadingUpdate);
            DownloadUpdateCommand = new RelayCommand(async (o) => await DownloadUpdateAsync(), (o) => !IsDownloadingUpdate && IsUpdateAvailable);
            OpenUpdateFolderCommand = new RelayCommand((o) => _updateService.OpenUpdateFolderOrFile(_downloadedUpdateFilePath));

            RefreshAvailableModels();
            LoadSettings();
            // Đảm bảo mặc định Tab 2 và Tab 3 luôn thu gọn khi khởi động theo yêu cầu
            _isVoiceCloneEnabled = false;
            _isEqPanelExpanded = false;
            OnPropertyChanged(nameof(IsVoiceCloneEnabled));
            OnPropertyChanged(nameof(VoiceClonePanelVisibility));
            OnPropertyChanged(nameof(IsEqPanelExpanded));
            OnPropertyChanged(nameof(EqPanelVisibility));

            UpdateSelectedModelInfo();
            UpdateThemeBrushes();
            UpdateTimeDisplay();
            _ = InitializeEngineAsync();
        }

        private void LoadAudioAndWaveformToTimeline(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            try
            {
                _currentAudioPath = filePath;
                _audioPlayer.Load(_currentAudioPath);
                TotalDuration = Math.Max(0.1, _audioPlayer.TotalTime.TotalSeconds);
                _isTimerUpdating = true;
                CurrentPosition = 0;
                _isTimerUpdating = false;
                UpdateTimeDisplay();
                WaveformPoints = GenerateWaveform(_currentAudioPath);
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi nạp sóng âm thanh vào Timeline", ex);
            }
        }

        // --- TAB 1: CHỌN MODEL & ẨN/HIỆN VÙNG MIỀN + GIỚI TÍNH ---
        public ObservableCollection<string> AvailableModels { get; } = new ObservableCollection<string>();

        private string _selectedModelFile = "Tự động theo vùng miền (Auto)";
        public string SelectedModelFile
        {
            get => _selectedModelFile;
            set
            {
                string nextVal = string.IsNullOrEmpty(value) ? "Tự động theo vùng miền (Auto)" : value;
                if (_selectedModelFile == nextVal) return;

                SaveCurrentModelProfile();
                _selectedModelFile = nextVal;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAutoModelSelected));
                OnPropertyChanged(nameof(AutoModelOptionsVisibility));
                LoadProfileForModel(_selectedModelFile);
                UpdateSelectedModelInfo();
                SaveSettings();
            }
        }

        public bool IsAutoModelSelected => string.IsNullOrEmpty(SelectedModelFile) || SelectedModelFile == "Tự động theo vùng miền (Auto)";
        public Visibility AutoModelOptionsVisibility => IsAutoModelSelected ? Visibility.Visible : Visibility.Collapsed;

        private string _selectedModelInfoText = "Chế độ Tự động: Tự chọn model theo vùng miền & giới tính.";
        public string SelectedModelInfoText
        {
            get => _selectedModelInfoText;
            set { _selectedModelInfoText = value; OnPropertyChanged(); }
        }

        private int _speakerId = -1;
        public int SpeakerId
        {
            get => _speakerId;
            set
            {
                _speakerId = Math.Clamp(value, -1, 903);
                OnPropertyChanged();
                OnPropertyChanged(nameof(SpeakerIdDisplay));
                SaveSettings();
            }
        }
        public string SpeakerIdDisplay => SpeakerId < 0 ? "Auto (-1)" : $"ID: {SpeakerId}";

        private bool _bypassDsp = false;
        public bool BypassDsp
        {
            get => _bypassDsp;
            set { _bypassDsp = value; OnPropertyChanged(); SaveSettings(); }
        }

        // --- TAB 2: NHÁI GIỌNG AI (MẶC ĐỊNH THU GỌN, CHỈ MỞ KHI BẬT CÔNG TẮC) ---
        private bool _isVoiceCloneEnabled = false;
        public bool IsVoiceCloneEnabled
        {
            get => _isVoiceCloneEnabled;
            set
            {
                _isVoiceCloneEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VoiceClonePanelVisibility));
                SaveSettings();
            }
        }
        public Visibility VoiceClonePanelVisibility => IsVoiceCloneEnabled ? Visibility.Visible : Visibility.Collapsed;

        private string _cloneReferencePath = "";
        public string CloneReferencePath
        {
            get => _cloneReferencePath;
            set
            {
                _cloneReferencePath = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(CloneReferenceFileName));
                CommandManager.InvalidateRequerySuggested();
                SaveSettings();
            }
        }
        public string CloneReferenceFileName => string.IsNullOrEmpty(_cloneReferencePath)
            ? "Chưa chọn mẫu giọng (.wav/.mp3)"
            : Path.GetFileName(_cloneReferencePath);

        private double _cloneStrength = 0.80;
        public double CloneStrength
        {
            get => _cloneStrength;
            set
            {
                _cloneStrength = Math.Round(Math.Clamp(value, 0.0, 1.0), 2);
                OnPropertyChanged();
                OnPropertyChanged(nameof(CloneStrengthPercent));
                SaveSettings();
            }
        }
        public string CloneStrengthPercent => $"{(int)(CloneStrength * 100)}%";

        // --- TAB 3: BỘ LỌC EQ (MẶC ĐỊNH THU GỌN, CHỈ MỞ KHI BẬT CÔNG TẮC) ---
        private bool _isEqPanelExpanded = false;
        public bool IsEqPanelExpanded
        {
            get => _isEqPanelExpanded;
            set
            {
                _isEqPanelExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EqPanelVisibility));
                SaveSettings();
            }
        }
        public Visibility EqPanelVisibility => IsEqPanelExpanded ? Visibility.Visible : Visibility.Collapsed;

        private double _eqBassDb = 0.0;
        public double EqBassDb
        {
            get => _eqBassDb;
            set
            {
                _eqBassDb = Math.Round(Math.Clamp(value, -12.0, 12.0), 1);
                OnPropertyChanged();
                OnPropertyChanged(nameof(EqBassDisplay));
                SaveCurrentModelProfile();
                SaveSettings();
            }
        }
        public string EqBassDisplay => _eqBassDb >= 0 ? $"+{_eqBassDb:0.0} dB" : $"{_eqBassDb:0.0} dB";

        private double _eqTrebleDb = 0.0;
        public double EqTrebleDb
        {
            get => _eqTrebleDb;
            set
            {
                _eqTrebleDb = Math.Round(Math.Clamp(value, -12.0, 12.0), 1);
                OnPropertyChanged();
                OnPropertyChanged(nameof(EqTrebleDisplay));
                SaveCurrentModelProfile();
                SaveSettings();
            }
        }
        public string EqTrebleDisplay => _eqTrebleDb >= 0 ? $"+{_eqTrebleDb:0.0} dB" : $"{_eqTrebleDb:0.0} dB";

        private double _deHissCutoff = 10000.0;
        public double DeHissCutoff
        {
            get => _deHissCutoff;
            set
            {
                _deHissCutoff = Math.Round(Math.Clamp(value, 4000.0, 12000.0), 0);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DeHissDisplay));
                SaveCurrentModelProfile();
                SaveSettings();
            }
        }
        public string DeHissDisplay => _deHissCutoff >= 10500 ? "Tắt (Full)" : $"{_deHissCutoff:0} Hz";

        private double _vitsNoiseScale = 0.67;
        public double VitsNoiseScale
        {
            get => _vitsNoiseScale;
            set
            {
                _vitsNoiseScale = Math.Round(Math.Clamp(value, 0.10, 1.00), 2);
                OnPropertyChanged();
                SaveCurrentModelProfile();
                SaveSettings();
            }
        }

        // --- TRÌNH SỬA & TẢI MODEL TỰ ĐỘNG TRONG SETTINGS ---
        private bool _isRepairingModels;
        public bool IsRepairingModels
        {
            get => _isRepairingModels;
            set
            {
                _isRepairingModels = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelRepairProgressVisibility));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private double _modelRepairProgress;
        public double ModelRepairProgress
        {
            get => _modelRepairProgress;
            set
            {
                _modelRepairProgress = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelRepairProgressPercent));
                OnPropertyChanged(nameof(ModelRepairProgressVisibility));
            }
        }
        public string ModelRepairProgressPercent => $"{(int)ModelRepairProgress}%";
        public Visibility ModelRepairProgressVisibility => (IsRepairingModels || ModelRepairProgress > 0) ? Visibility.Visible : Visibility.Collapsed;

        private string _modelRepairLogText = "Nhấn '🔧 Sửa & Tải Model tự động' để kiểm tra và tải đầy đủ các bộ Model AI về máy.";
        public string ModelRepairLogText
        {
            get => _modelRepairLogText;
            set { _modelRepairLogText = value; OnPropertyChanged(); }
        }

        private void AppendRepairLog(string line)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                ModelRepairLogText += $"\n[{DateTime.Now:HH:mm:ss}] {line}";
            });
        }

        private async Task RepairAndDownloadModelsAsync()
        {
            IsRepairingModels = true;
            ModelRepairProgress = 2;
            ModelRepairLogText = $"[{DateTime.Now:HH:mm:ss}] Bắt đầu kiểm tra & sửa chữa hệ thống Model AI...";

            try
            {
                string kokoroDir = Path.Combine(_modelsDir, "kokoro_24k");
                Directory.CreateDirectory(_modelsDir);
                Directory.CreateDirectory(kokoroDir);

                var downloadList = new List<(string Name, string Url, string DestPath, long MinSize)>
                {
                    ("Kokoro-82M 24kHz (kokoro-v1.0.onnx)", "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx", Path.Combine(kokoroDir, "kokoro-v1.0.onnx"), 10_000_000),
                    ("Kokoro 54 Voices (voices-v1.0.bin)", "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin", Path.Combine(kokoroDir, "voices-v1.0.bin"), 1_000_000),
                    ("Tiếng Việt Miền Bắc (vais1000-medium.onnx)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx?download=true", Path.Combine(_modelsDir, "bac_vi_VN-vais1000-medium.onnx"), 5_000_000),
                    ("Cấu hình Miền Bắc (vais1000-medium.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx.json?download=true", Path.Combine(_modelsDir, "bac_vi_VN-vais1000-medium.onnx.json"), 100),
                    ("Tiếng Việt Miền Trung (25hours_single-low.onnx)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/25hours_single/low/vi_VN-25hours_single-low.onnx?download=true", Path.Combine(_modelsDir, "trung_vi_VN-25hours_single-low.onnx"), 5_000_000),
                    ("Cấu hình Miền Trung (25hours_single-low.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/25hours_single/low/vi_VN-25hours_single-low.onnx.json?download=true", Path.Combine(_modelsDir, "trung_vi_VN-25hours_single-low.onnx.json"), 100),
                    ("Tiếng Việt Miền Nam (vivos-x_low.onnx)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx?download=true", Path.Combine(_modelsDir, "nam_vi_VN-vivos-x_low.onnx"), 5_000_000),
                    ("Cấu hình Miền Nam (vivos-x_low.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx.json?download=true", Path.Combine(_modelsDir, "nam_vi_VN-vivos-x_low.onnx.json"), 100),
                    ("English Nữ High (en_US-lessac-high.onnx)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/high/en_US-lessac-high.onnx?download=true", Path.Combine(_modelsDir, "en_female_hi_en_US-lessac-high.onnx"), 5_000_000),
                    ("Cấu hình Lessac High (.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/high/en_US-lessac-high.onnx.json?download=true", Path.Combine(_modelsDir, "en_female_hi_en_US-lessac-high.onnx.json"), 100),
                    ("English Nam High (en_US-ryan-high.onnx)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/ryan/high/en_US-ryan-high.onnx?download=true", Path.Combine(_modelsDir, "en_male_hi_en_US-ryan-high.onnx"), 5_000_000),
                    ("Cấu hình Ryan High (.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/ryan/high/en_US-ryan-high.onnx.json?download=true", Path.Combine(_modelsDir, "en_male_hi_en_US-ryan-high.onnx.json"), 100)
                };

                int totalItems = downloadList.Count;
                for (int i = 0; i < totalItems; i++)
                {
                    var item = downloadList[i];
                    double basePct = (i * 85.0) / totalItems;
                    ModelRepairProgress = Math.Max(5, basePct);

                    if (File.Exists(item.DestPath) && new FileInfo(item.DestPath).Length >= item.MinSize)
                    {
                        AppendRepairLog($"✔ Đã chuẩn: {item.Name}");
                        continue;
                    }

                    AppendRepairLog($"⏳ Đang tải: {item.Name}...");
                    using var resp = await _httpClient.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    long? len = resp.Content.Headers.ContentLength;
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var fs = new FileStream(item.DestPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                    byte[] buf = new byte[81920];
                    long readTotal = 0;
                    int r;
                    while ((r = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await fs.WriteAsync(buf, 0, r);
                        readTotal += r;
                        if (len.HasValue && len.Value > 0)
                        {
                            double subPct = (double)readTotal / len.Value * (85.0 / totalItems);
                            ModelRepairProgress = Math.Min(90, basePct + subPct);
                        }
                    }
                    AppendRepairLog($"✅ Tải xong: {item.Name} ({Math.Round(readTotal / 1048576.0, 2)} MB)");
                }

                AppendRepairLog("⏳ Đang kiểm tra các gói thư viện Python (edge-tts, kokoro-onnx, pedalboard, cmudict)...");
                ModelRepairProgress = 92;
                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = "-m pip install edge-tts kokoro-onnx miniaudio pedalboard cmudict scipy numpy piper-tts onnxruntime fastapi uvicorn pydantic",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(120000);
                });

                RefreshAvailableModels();
                ModelRepairProgress = 100;
                AppendRepairLog("🎉 HOÀN TẤT! Toàn bộ Model và thư viện đã sẵn sàng 100%.");
            }
            catch (Exception ex)
            {
                AppendRepairLog($"❌ Lỗi: {ex.Message}");
                Logger.Error("Lỗi sửa & tải Model tự động", ex);
            }
            finally
            {
                IsRepairingModels = false;
            }
        }

        private void SaveCurrentModelProfile()
        {
            if (_isLoadingSettings || _isSwitchingModelProfile || string.IsNullOrEmpty(_selectedModelFile)) return;
            _modelProfiles[_selectedModelFile] = new ModelDspProfile
            {
                Speed = _speed,
                Pitch = _pitch,
                EqBassDb = _eqBassDb,
                EqTrebleDb = _eqTrebleDb,
                DeHissCutoff = _deHissCutoff,
                VitsNoiseScale = _vitsNoiseScale
            };
        }

        private void LoadProfileForModel(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return;
            _isSwitchingModelProfile = true;
            try
            {
                if (_modelProfiles.TryGetValue(modelKey, out var prof) && prof != null)
                {
                    _speed = Math.Clamp(prof.Speed, 0.25, 3.0);
                    _pitch = Math.Clamp(prof.Pitch, 0.5, 2.0);
                    _eqBassDb = Math.Clamp(prof.EqBassDb, -12.0, 12.0);
                    _eqTrebleDb = Math.Clamp(prof.EqTrebleDb, -12.0, 12.0);
                    _deHissCutoff = Math.Clamp(prof.DeHissCutoff, 4000.0, 12000.0);
                    _vitsNoiseScale = Math.Clamp(prof.VitsNoiseScale, 0.10, 1.00);
                }
                else
                {
                    if (modelKey.Contains("25hours") || modelKey.Contains("vivos"))
                    {
                        _eqBassDb = 2.0;
                        _eqTrebleDb = 1.5;
                        _deHissCutoff = 7500.0;
                        _vitsNoiseScale = 0.55;
                    }
                    else if (modelKey.StartsWith("[VieNeu-TTS") || modelKey.StartsWith("[VietTTS"))
                    {
                        _eqBassDb = 2.0;
                        _eqTrebleDb = 1.5;
                        _deHissCutoff = 10500.0;
                        _vitsNoiseScale = 0.67;
                    }
                    else
                    {
                        _eqBassDb = 0.0;
                        _eqTrebleDb = 0.0;
                        _deHissCutoff = 10000.0;
                        _vitsNoiseScale = 0.67;
                    }
                }
                OnPropertyChanged(nameof(Speed));
                OnPropertyChanged(nameof(Pitch));
                OnPropertyChanged(nameof(EqBassDb));
                OnPropertyChanged(nameof(EqBassDisplay));
                OnPropertyChanged(nameof(EqTrebleDb));
                OnPropertyChanged(nameof(EqTrebleDisplay));
                OnPropertyChanged(nameof(DeHissCutoff));
                OnPropertyChanged(nameof(DeHissDisplay));
                OnPropertyChanged(nameof(VitsNoiseScale));
            }
            finally
            {
                _isSwitchingModelProfile = false;
            }
        }

        public void RefreshAvailableModels()
        {
            string currentSelection = _selectedModelFile;
            AvailableModels.Clear();
            AvailableModels.Add("Tự động theo vùng miền (Auto)");

            try
            {
                AvailableModels.Add("[VieNeu-TTS-24kHz] Giọng Nam Truyền Cảm (Vietnamese Neural Prosody)");
                AvailableModels.Add("[VieNeu-TTS-24kHz] Giọng Nữ Truyền Cảm (Vietnamese Neural Prosody)");
                AvailableModels.Add("[VietTTS-HiFiGAN] Giọng Nữ Miền Bắc Chuẩn (Offline NAT + Prosody)");
                AvailableModels.Add("[VietTTS-HiFiGAN] Giọng Nam Trầm Chuẩn (Offline VITS + HiFi-GAN)");

                AvailableModels.Add("[Multilingual-24kHz] en-US-AndrewMultilingualNeural (Nam Đọc Việt+Anh Đồng Nhất)");
                AvailableModels.Add("[Multilingual-24kHz] en-US-AvaMultilingualNeural (Nữ Đọc Việt+Anh Đồng Nhất)");
                AvailableModels.Add("[Multilingual-24kHz] en-US-BrianMultilingualNeural (Nam Trầm Việt+Anh Đồng Nhất)");
                AvailableModels.Add("[Multilingual-24kHz] en-US-EmmaMultilingualNeural (Nữ Ấm Việt+Anh Đồng Nhất)");
                AvailableModels.Add("[Neural-24kHz] vi-VN-NamMinhNeural (Giọng Nam Việt Siêu Thực)");
                AvailableModels.Add("[Neural-24kHz] vi-VN-HoaiMyNeural (Giọng Nữ Việt Siêu Thực)");

                if (File.Exists(Path.Combine(_modelsDir, "kokoro_24k", "kokoro-v1.0.onnx")))
                {
                    AvailableModels.Add("[Kokoro-82M-24kHz] af_heart (Nữ Mỹ Phòng Thu Top #1)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] af_bella (Nữ Mỹ Truyền Cảm)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] am_adam (Nam Mỹ Trầm Ấm)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] am_michael (Nam Mỹ Phát Thanh Viên)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] bf_emma (Nữ Anh Quốc Hoàng Gia)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] bm_george (Nam Anh Quốc Cổ Điển)");
                }

                if (Directory.Exists(Path.Combine(_modelsDir, "mms_vie_male")) &&
                    File.Exists(Path.Combine(_modelsDir, "mms_vie_male", "config.json")))
                {
                    AvailableModels.Add("[PyTorch] mms_vie_male (Meta AI Giọng Nam)");
                }

                if (Directory.Exists(_modelsDir))
                {
                    var onnxFiles = Directory.GetFiles(_modelsDir, "*.onnx")
                        .Where(f => new FileInfo(f).Length > 1000000)
                        .Select(Path.GetFileName)
                        .OrderBy(n => n);

                    foreach (var name in onnxFiles)
                    {
                        AvailableModels.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi quét danh sách model", ex);
            }

            if (AvailableModels.Contains(currentSelection))
                _selectedModelFile = currentSelection;
            else
                _selectedModelFile = "Tự động theo vùng miền (Auto)";

            OnPropertyChanged(nameof(SelectedModelFile));
            OnPropertyChanged(nameof(IsAutoModelSelected));
            OnPropertyChanged(nameof(AutoModelOptionsVisibility));
            UpdateSelectedModelInfo();
        }

        private void UpdateSelectedModelInfo()
        {
            if (IsAutoModelSelected)
            {
                SelectedModelInfoText = "Tự động phối hợp Model theo Vùng miền & Giới tính.";
                return;
            }
            if (SelectedModelFile.StartsWith("[VieNeu-TTS-24kHz]"))
            {
                SelectedModelInfoText = "Engine: VieNeu-TTS 24000 Hz | Tối ưu ngữ điệu 6 thanh Tiếng Việt & Nối âm tự nhiên.";
                return;
            }
            if (SelectedModelFile.StartsWith("[VietTTS-HiFiGAN]"))
            {
                SelectedModelInfoText = "Engine: VietTTS NAT + HiFi-GAN (100% Offline) | Tối ưu độ ấm giọng Việt.";
                return;
            }
            if (SelectedModelFile.StartsWith("[Multilingual-24kHz]"))
            {
                SelectedModelInfoText = "Engine: Neural Multilingual 24000 Hz | Đọc trực tiếp Việt & Anh bằng 1 giọng duy nhất.";
                return;
            }
            if (SelectedModelFile.StartsWith("[Neural-24kHz]"))
            {
                SelectedModelInfoText = "Engine: Microsoft Neural 24000 Hz | Giọng Tiếng Việt Nam/Nữ siêu thực.";
                return;
            }
            if (SelectedModelFile.StartsWith("[Kokoro-82M-24kHz]"))
            {
                SelectedModelInfoText = "Engine: Kokoro-82M ONNX (100% Offline) | Tần số: 24000 Hz | Top #1 Open-Weight TTS.";
                return;
            }
            if (SelectedModelFile.StartsWith("[PyTorch]"))
            {
                SelectedModelInfoText = "Engine: PyTorch VITS (100% Offline) | Tần số gốc: 16000 Hz | Giọng Nam bản ngữ Meta AI.";
                return;
            }

            string jsonPath = Path.Combine(_modelsDir, SelectedModelFile + ".json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    var root = doc.RootElement;
                    int sr = 22050;
                    if (root.TryGetProperty("audio", out var audioProp) && audioProp.TryGetProperty("sample_rate", out var srProp))
                        sr = srProp.GetInt32();
                    int numSpk = root.TryGetProperty("num_speakers", out var spkProp) ? spkProp.GetInt32() : 1;
                    string spkNote = numSpk > 1 ? $"{numSpk} giọng (Speaker ID: 0 -> {numSpk - 1})" : "1 giọng đơn";
                    SelectedModelInfoText = $"ONNX | Tần số gốc: {sr} Hz | {spkNote}";
                    return;
                }
                catch { }
            }
            SelectedModelInfoText = $"File ONNX: {SelectedModelFile}";
        }

        public void LoadSettings()
        {
            if (!File.Exists(_settingsFilePath)) return;
            _isLoadingSettings = true;
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                var data = JsonSerializer.Deserialize<UserSettingsData>(json);
                if (data != null)
                {
                    if (!string.IsNullOrEmpty(data.SelectedAeroTheme)) _selectedAeroTheme = data.SelectedAeroTheme;
                    if (!string.IsNullOrEmpty(data.SelectedAccentItem)) _selectedAccentItem = data.SelectedAccentItem;
                    if (!string.IsNullOrEmpty(data.SelectedWaveformColorItem)) _selectedWaveformColorItem = data.SelectedWaveformColorItem;
                    if (!string.IsNullOrEmpty(data.SelectedSecondaryLineColorItem)) _selectedSecondaryLineColorItem = data.SelectedSecondaryLineColorItem;
                    _glassOpacity = Math.Clamp(data.GlassOpacity, 0.25, 1.0);
                    _cardOpacity = Math.Clamp(data.CardOpacity, 0.15, 0.95);
                    _showAeroReflection = data.ShowAeroReflection;
                    if (!string.IsNullOrEmpty(data.SelectedVoice)) _selectedVoice = data.SelectedVoice;
                    if (data.ModelProfiles != null) _modelProfiles = data.ModelProfiles;
                    if (!string.IsNullOrEmpty(data.SelectedModelFile) && AvailableModels.Contains(data.SelectedModelFile))
                        _selectedModelFile = data.SelectedModelFile;
                    _speakerId = Math.Clamp(data.SpeakerId, -1, 903);
                    _bypassDsp = data.BypassDsp;
                    if (!string.IsNullOrEmpty(data.SelectedGender)) _selectedGender = data.SelectedGender;
                    _bilingualAuto = data.BilingualAuto;
                    _singleVoiceMode = data.SingleVoiceMode;
                    _autoPlayAudio = data.AutoPlayAudio;
                    _speed = Math.Clamp(data.Speed, 0.25, 3.0);
                    _pitch = Math.Clamp(data.Pitch, 0.5, 2.0);
                    _volume = Math.Clamp(data.Volume, 0, 100);
                    _audioPlayer.Volume = (float)(_volume / 100.0);
                    _cloneReferencePath = data.CloneReferencePath ?? "";
                    _cloneStrength = Math.Clamp(data.CloneStrength, 0.0, 1.0);
                    _eqBassDb = Math.Clamp(data.EqBassDb, -12.0, 12.0);
                    _eqTrebleDb = Math.Clamp(data.EqTrebleDb, -12.0, 12.0);
                    _deHissCutoff = Math.Clamp(data.DeHissCutoff, 4000.0, 12000.0);
                    _vitsNoiseScale = Math.Clamp(data.VitsNoiseScale, 0.10, 1.00);
                    if (!string.IsNullOrEmpty(data.GitHubRepo)) _gitHubRepo = data.GitHubRepo;
                }
            }
            catch (Exception ex) { Logger.Error("Không thể đọc settings.json", ex); }
            finally { _isLoadingSettings = false; }
        }

        public void SaveSettings()
        {
            if (_isLoadingSettings) return;
            try
            {
                var data = new UserSettingsData
                {
                    SelectedAeroTheme = SelectedAeroTheme,
                    SelectedAccentItem = SelectedAccentItem,
                    SelectedWaveformColorItem = SelectedWaveformColorItem,
                    SelectedSecondaryLineColorItem = SelectedSecondaryLineColorItem,
                    GlassOpacity = GlassOpacity,
                    CardOpacity = CardOpacity,
                    ShowAeroReflection = ShowAeroReflection,
                    SelectedVoice = SelectedVoice,
                    SelectedModelFile = SelectedModelFile,
                    SpeakerId = SpeakerId,
                    BypassDsp = BypassDsp,
                    SelectedGender = SelectedGender,
                    BilingualAuto = BilingualAuto,
                    SingleVoiceMode = SingleVoiceMode,
                    AutoPlayAudio = AutoPlayAudio,
                    Speed = Speed,
                    Pitch = Pitch,
                    Volume = Volume,
                    IsVoiceCloneEnabled = IsVoiceCloneEnabled,
                    IsEqPanelExpanded = IsEqPanelExpanded,
                    CloneReferencePath = CloneReferencePath,
                    CloneStrength = CloneStrength,
                    EqBassDb = EqBassDb,
                    EqTrebleDb = EqTrebleDb,
                    DeHissCutoff = DeHissCutoff,
                    VitsNoiseScale = VitsNoiseScale,
                    ModelProfiles = _modelProfiles,
                    GitHubRepo = GitHubRepo
                };
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex) { Logger.Error("Không thể lưu settings.json", ex); }
        }

        private void UpdatePlaybackProgress()
        {
            if (_audioPlayer.IsPlaying)
            {
                _isTimerUpdating = true;
                CurrentPosition = _audioPlayer.CurrentTime.TotalSeconds;
                _isTimerUpdating = false;
                UpdateTimeDisplay();
            }
        }

        public static string FormatTimeCode(double seconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
        }

        private void UpdateTimeDisplay()
        {
            double curSec = Math.Max(0, CurrentPosition);
            double totSec = Math.Max(0, TotalDuration > 0.11 ? TotalDuration : 0);
            double remSec = totSec > curSec ? (totSec - curSec) : 0;

            CurrentTimeText = FormatTimeCode(curSec);
            TotalTimeText = FormatTimeCode(totSec);

            TimeSpan rem = TimeSpan.FromSeconds(remSec);
            RemainingTimeText = $"-{rem.Minutes:D2}:{rem.Seconds:D2}";
            ProgressPercentText = totSec > 0.01
                ? $"{Math.Min(100, (int)(curSec * 100 / totSec))}%"
                : "0%";
        }

        private bool _isSettingsOpen;
        public bool IsSettingsOpen
        {
            get => _isSettingsOpen;
            set
            {
                _isSettingsOpen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StudioVisibility));
                OnPropertyChanged(nameof(SettingsVisibility));
            }
        }
        public Visibility StudioVisibility => IsSettingsOpen ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SettingsVisibility => IsSettingsOpen ? Visibility.Visible : Visibility.Collapsed;

        // --- MÀU SẮC & GIAO DIỆN (ĐÃ BỔ SUNG MÀU ĐEN & XÁM) ---
        private double _glassOpacity = 0.85;
        public double GlassOpacity
        {
            get => _glassOpacity;
            set
            {
                _glassOpacity = Math.Round(Math.Clamp(value, 0.25, 1.0), 2);
                OnPropertyChanged();
                OnPropertyChanged(nameof(GlassOpacityPercent));
                UpdateThemeBrushes();
                SaveSettings();
            }
        }
        public string GlassOpacityPercent => $"{(int)(GlassOpacity * 100)}%";

        private double _cardOpacity = 0.45;
        public double CardOpacity
        {
            get => _cardOpacity;
            set
            {
                _cardOpacity = Math.Round(Math.Clamp(value, 0.15, 0.95), 2);
                OnPropertyChanged();
                OnPropertyChanged(nameof(CardOpacityPercent));
                UpdateThemeBrushes();
                SaveSettings();
            }
        }
        public string CardOpacityPercent => $"{(int)(CardOpacity * 100)}%";

        private bool _showAeroReflection = true;
        public bool ShowAeroReflection
        {
            get => _showAeroReflection;
            set
            {
                _showAeroReflection = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AeroReflectionVisibility));
                SaveSettings();
            }
        }
        public Visibility AeroReflectionVisibility => ShowAeroReflection ? Visibility.Visible : Visibility.Collapsed;

        public string[] AeroThemes { get; } = {
            "Đen Tuyền (Pure Pitch Black)",
            "Xám Chuẩn (Neutral Charcoal Gray)",
            "Đen Khói Windows 10 (Obsidian Glass)",
            "Xám Bạc Hiện Đại (Frosted Slate)",
            "Xanh Băng Windows 7 (Aero Ice Blue)",
            "Xanh Đại Dương Sâu (Deep Ocean Glass)",
            "Xanh Ngọc Lục Bảo (Emerald Glass)",
            "Tím Pha Lê Đêm (Royal Amethyst)",
            "Đỏ Hồng Ngọc (Crimson Ruby Glass)"
        };
        private string _selectedAeroTheme = "Đen Khói Windows 10 (Obsidian Glass)";
        public string SelectedAeroTheme
        {
            get => _selectedAeroTheme;
            set { _selectedAeroTheme = value; OnPropertyChanged(); UpdateThemeBrushes(); SaveSettings(); }
        }

        public string[] AccentColorList { get; } = {
            "Xanh Windows 10 (#0078D4)",
            "Xanh Aero Cyan (#00B4FF)",
            "Xanh Ngọc Lục Bảo (#10B981)",
            "Xám Kim Loại (#64748B)",
            "Đen Tuyền Viền Sáng (#27272A)",
            "Tím Hiện Đại (#8B5CF6)",
            "Đỏ Hồng Ruby (#F43F5E)",
            "Vàng Hổ Phách (#F59E0B)",
            "Xanh Băng Sáng (#38BDF8)"
        };
        private string _selectedAccentItem = "Xanh Windows 10 (#0078D4)";
        public string SelectedAccentItem
        {
            get => _selectedAccentItem;
            set { _selectedAccentItem = value; OnPropertyChanged(); UpdateThemeBrushes(); SaveSettings(); }
        }

        public string[] WaveformColorList { get; } = {
            "Xanh Sóng Âm Aero (#00B4FF)",
            "Xanh Windows 10 (#0078D4)",
            "Xanh Lục Neon (#00E676)",
            "Xám Bạc Sóng Âm (#94A3B8)",
            "Đen Khói Sóng Âm (#3F3F46)",
            "Tím Sóng Âm (#A855F7)",
            "Cam Hoàng Hôn (#FB923C)",
            "Trắng Băng Tuyết (#E2E8F0)"
        };
        private string _selectedWaveformColorItem = "Xanh Sóng Âm Aero (#00B4FF)";
        public string SelectedWaveformColorItem
        {
            get => _selectedWaveformColorItem;
            set { _selectedWaveformColorItem = value; OnPropertyChanged(); UpdateThemeBrushes(); SaveSettings(); }
        }

        public string[] SecondaryLineColorList { get; } = {
            "Vàng Tia Chớp (#FFD600)",
            "Xanh Ngọc Sáng (#00E5FF)",
            "Xanh Lá Neon (#00E676)",
            "Xám Sáng (#CBD5E1)",
            "Hồng Tâm Điểm (#FF4081)",
            "Trắng Tinh Khiết (#FFFFFF)"
        };
        private string _selectedSecondaryLineColorItem = "Vàng Tia Chớp (#FFD600)";
        public string SelectedSecondaryLineColorItem
        {
            get => _selectedSecondaryLineColorItem;
            set { _selectedSecondaryLineColorItem = value; OnPropertyChanged(); UpdateThemeBrushes(); SaveSettings(); }
        }

        private string _windowGlassBrush = "#D912161F";
        public string WindowGlassBrush { get => _windowGlassBrush; set { _windowGlassBrush = value; OnPropertyChanged(); } }

        private string _cardGlassBrush = "#73090E17";
        public string CardGlassBrush { get => _cardGlassBrush; set { _cardGlassBrush = value; OnPropertyChanged(); } }

        private string _windowBorderBrush = "#550078D4";
        public string WindowBorderBrush { get => _windowBorderBrush; set { _windowBorderBrush = value; OnPropertyChanged(); } }

        private string _accentBrush = "#0078D4";
        public string AccentBrush { get => _accentBrush; set { _accentBrush = value; OnPropertyChanged(); } }

        private string _waveformBrush = "#00B4FF";
        public string WaveformBrush { get => _waveformBrush; set { _waveformBrush = value; OnPropertyChanged(); } }

        private string _secondaryLineBrush = "#FFD600";
        public string SecondaryLineBrush { get => _secondaryLineBrush; set { _secondaryLineBrush = value; OnPropertyChanged(); } }

        private static string ExtractHex(string item, string fallback)
        {
            if (string.IsNullOrEmpty(item)) return fallback;
            int idx = item.IndexOf('#');
            if (idx >= 0 && idx + 7 <= item.Length)
                return item.Substring(idx, 7);
            return fallback;
        }

        private void UpdateThemeBrushes()
        {
            byte winAlpha = (byte)(Math.Clamp(GlassOpacity, 0.25, 1.0) * 255);
            byte cardAlpha = (byte)(Math.Clamp(CardOpacity, 0.15, 0.95) * 255);
            string winHexA = winAlpha.ToString("X2");
            string cardHexA = cardAlpha.ToString("X2");

            string baseRgb = "12161F";
            if (SelectedAeroTheme.Contains("Đen Tuyền")) baseRgb = "050507";
            else if (SelectedAeroTheme.Contains("Xám Chuẩn")) baseRgb = "22252A";
            else if (SelectedAeroTheme.Contains("Windows 7")) baseRgb = "0D1F38";
            else if (SelectedAeroTheme.Contains("Đại Dương")) baseRgb = "071B2E";
            else if (SelectedAeroTheme.Contains("Ngọc Lục Bảo")) baseRgb = "092622";
            else if (SelectedAeroTheme.Contains("Tím")) baseRgb = "1B102E";
            else if (SelectedAeroTheme.Contains("Đỏ")) baseRgb = "2B0D16";
            else if (SelectedAeroTheme.Contains("Xám Bạc")) baseRgb = "1E242B";

            WindowGlassBrush = $"#{winHexA}{baseRgb}";
            CardGlassBrush = $"#{cardHexA}080D14";

            AccentBrush = ExtractHex(SelectedAccentItem, "#0078D4");
            WaveformBrush = ExtractHex(SelectedWaveformColorItem, "#00B4FF");
            SecondaryLineBrush = ExtractHex(SelectedSecondaryLineColorItem, "#FFD600");
            WindowBorderBrush = "#66" + AccentBrush.TrimStart('#');
        }

        private string _inputText = "Xin chào! Đây là phiên bản nâng cấp VoiceCraft 1.0.1 tích hợp VieNeu-TTS, tính năng Nhái giọng AI chuẩn xác và bộ lọc âm thanh EQ riêng cho từng Model.";
        public string InputText
        {
            get => _inputText;
            set
            {
                _inputText = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(CharCount));
                CommandManager.InvalidateRequerySuggested();
            }
        }
        public int CharCount => InputText?.Length ?? 0;

        public string[] Voices { get; } = {
            "Song ngữ Việt - Anh (Tự động)",
            "Tiếng Việt - Miền Bắc",
            "Tiếng Việt - Miền Trung",
            "Tiếng Việt - Miền Nam",
            "English - US (Chuẩn Mỹ 22.05kHz)"
        };
        private string _selectedVoice = "Song ngữ Việt - Anh (Tự động)";
        public string SelectedVoice { get => _selectedVoice; set { _selectedVoice = value; OnPropertyChanged(); SaveSettings(); } }

        public string[] Genders { get; } = { "Nữ (Female)", "Nam (Male)" };
        private string _selectedGender = "Nữ (Female)";
        public string SelectedGender { get => _selectedGender; set { _selectedGender = value; OnPropertyChanged(); SaveSettings(); } }

        private bool _bilingualAuto = true;
        public bool BilingualAuto { get => _bilingualAuto; set { _bilingualAuto = value; OnPropertyChanged(); SaveSettings(); } }

        private bool _singleVoiceMode = true;
        public bool SingleVoiceMode { get => _singleVoiceMode; set { _singleVoiceMode = value; OnPropertyChanged(); SaveSettings(); } }

        private bool _autoPlayAudio = true;
        public bool AutoPlayAudio { get => _autoPlayAudio; set { _autoPlayAudio = value; OnPropertyChanged(); SaveSettings(); } }

        private double _speed = 1.0;
        public double Speed
        {
            get => _speed;
            set
            {
                _speed = Math.Round(Math.Clamp(value, 0.25, 3.0), 2);
                OnPropertyChanged();
                SaveCurrentModelProfile();
                SaveSettings();
            }
        }

        private double _pitch = 1.0;
        public double Pitch
        {
            get => _pitch;
            set
            {
                _pitch = Math.Round(Math.Clamp(value, 0.5, 2.0), 2);
                OnPropertyChanged();
                SaveCurrentModelProfile();
                SaveSettings();
            }
        }

        private double _volume = 100.0;
        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Round(Math.Clamp(value, 0, 100), 0);
                _audioPlayer.Volume = (float)(_volume / 100.0);
                OnPropertyChanged();
                SaveSettings();
            }
        }

        private string _connectionStatus = "Đang chờ...";
        public string ConnectionStatus { get => _connectionStatus; set { _connectionStatus = value; OnPropertyChanged(); } }

        private string _connectionColor = "Gray";
        public string ConnectionColor { get => _connectionColor; set { _connectionColor = value; OnPropertyChanged(); } }

        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                _isProcessing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsControlsEnabled));
                OnPropertyChanged(nameof(ProcessingVisibility));
                OnPropertyChanged(nameof(EditorBlurRadius));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsControlsEnabled => !IsProcessing;
        public Visibility ProcessingVisibility => IsProcessing ? Visibility.Visible : Visibility.Collapsed;
        public double EditorBlurRadius => IsProcessing ? 8.0 : 0.0;

        private double _synthesisProgress;
        public double SynthesisProgress
        {
            get => _synthesisProgress;
            set
            {
                _synthesisProgress = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                OnPropertyChanged(nameof(SynthesisProgressPercent));
            }
        }
        public string SynthesisProgressPercent => $"{(int)SynthesisProgress}%";

        private string _synthesisStatusText = "Đang chuẩn bị tổng hợp...";
        public string SynthesisStatusText { get => _synthesisStatusText; set { _synthesisStatusText = value; OnPropertyChanged(); } }

        private double _currentPosition;
        public double CurrentPosition
        {
            get => _currentPosition;
            set
            {
                _currentPosition = Math.Clamp(value, 0, Math.Max(0.01, TotalDuration));
                OnPropertyChanged();
                if (!_isTimerUpdating && _audioPlayer.TotalTime.TotalSeconds > 0)
                {
                    _audioPlayer.CurrentTime = TimeSpan.FromSeconds(_currentPosition);
                    UpdateTimeDisplay();
                }
            }
        }

        private double _totalDuration = 0.0;
        public double TotalDuration { get => _totalDuration; set { _totalDuration = value; OnPropertyChanged(); } }

        private string _currentTimeText = "00:00.0";
        public string CurrentTimeText { get => _currentTimeText; set { _currentTimeText = value; OnPropertyChanged(); } }

        private string _totalTimeText = "00:00.0";
        public string TotalTimeText { get => _totalTimeText; set { _totalTimeText = value; OnPropertyChanged(); } }

        private string _remainingTimeText = "-00:00";
        public string RemainingTimeText { get => _remainingTimeText; set { _remainingTimeText = value; OnPropertyChanged(); } }

        private string _progressPercentText = "0%";
        public string ProgressPercentText { get => _progressPercentText; set { _progressPercentText = value; OnPropertyChanged(); } }

        private string _hoverTimeText = "00:00.0";
        public string HoverTimeText { get => _hoverTimeText; set { _hoverTimeText = value; OnPropertyChanged(); } }

        public ObservableCollection<HistoryItem> HistoryList { get; } = new ObservableCollection<HistoryItem>();

        private HistoryItem _selectedHistoryItem;
        public HistoryItem SelectedHistoryItem
        {
            get => _selectedHistoryItem;
            set
            {
                _selectedHistoryItem = value;
                if (value != null && File.Exists(value.FilePath))
                {
                    LoadAudioAndWaveformToTimeline(value.FilePath);
                }
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private PointCollection _waveformPoints;
        public PointCollection WaveformPoints { get => _waveformPoints; set { _waveformPoints = value; OnPropertyChanged(); } }

        private string _gitHubRepo = "longtygu/VoiceCraft";
        public string GitHubRepo
        {
            get => _gitHubRepo;
            set { _gitHubRepo = value; OnPropertyChanged(); SaveSettings(); }
        }

        private string _updateStatusText = $"Phiên bản hiện tại: v{UpdateService.CurrentAppVersion} (Nhấn 'Kiểm tra cập nhật' để kiểm tra từ GitHub)";
        public string UpdateStatusText { get => _updateStatusText; set { _updateStatusText = value; OnPropertyChanged(); } }

        private string _updateChangelog = "Chưa có dữ liệu bản cập nhật mới.";
        public string UpdateChangelog { get => _updateChangelog; set { _updateChangelog = value; OnPropertyChanged(); } }

        private double _updateProgress = 0;
        public double UpdateProgress
        {
            get => _updateProgress;
            set
            {
                _updateProgress = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpdateProgressPercent));
            }
        }
        public string UpdateProgressPercent => $"{(int)UpdateProgress}%";

        private bool _isCheckingUpdate;
        public bool IsCheckingUpdate
        {
            get => _isCheckingUpdate;
            set { _isCheckingUpdate = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
        }

        private bool _isDownloadingUpdate;
        public bool IsDownloadingUpdate
        {
            get => _isDownloadingUpdate;
            set
            {
                _isDownloadingUpdate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpdateProgressVisibility));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private bool _isUpdateAvailable;
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set { _isUpdateAvailable = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
        }

        public Visibility UpdateProgressVisibility => (IsDownloadingUpdate || UpdateProgress > 0) ? Visibility.Visible : Visibility.Collapsed;

        // Commands
        public ICommand SynthesizeCommand { get; }
        public ICommand PlayCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand PasteTextCommand { get; }
        public ICommand ClearTextCommand { get; }
        public ICommand SwitchTabCommand { get; }
        public ICommand RefreshModelsCommand { get; }
        public ICommand AdjustSpeakerIdCommand { get; }
        public ICommand SelectCloneSampleCommand { get; }
        public ICommand PreviewCloneSampleCommand { get; }
        public ICommand ClearCloneSampleCommand { get; }
        public ICommand ResetEqCommand { get; }
        public ICommand AdjustSpeedCommand { get; }
        public ICommand SetSpeedPresetCommand { get; }
        public ICommand AdjustPitchCommand { get; }
        public ICommand SetPitchPresetCommand { get; }
        public ICommand RestartEngineCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ClearCacheCommand { get; }
        public ICommand RepairAndDownloadModelsCommand { get; }
        public ICommand CheckUpdateCommand { get; }
        public ICommand DownloadUpdateCommand { get; }
        public ICommand OpenUpdateFolderCommand { get; }

        private async Task CheckForUpdateAsync()
        {
            IsCheckingUpdate = true;
            UpdateStatusText = "⏳ Đang kết nối GitHub Releases API...";
            try
            {
                _latestUpdateInfo = await _updateService.CheckForUpdatesAsync(GitHubRepo);
                IsUpdateAvailable = _latestUpdateInfo.IsUpdateAvailable;
                UpdateChangelog = string.IsNullOrWhiteSpace(_latestUpdateInfo.ReleaseNotes)
                    ? "Không có ghi chú phát hành."
                    : _latestUpdateInfo.ReleaseNotes;

                if (_latestUpdateInfo.IsUpdateAvailable)
                    UpdateStatusText = $"🎉 Phát hiện phiên bản mới: v{_latestUpdateInfo.LatestVersion} (Hiện tại: v{AppVersion})";
                else
                    UpdateStatusText = $"✔ Bạn đang sử dụng phiên bản mới nhất (v{AppVersion}).";
            }
            catch (Exception ex)
            {
                UpdateStatusText = "❌ Lỗi kiểm tra cập nhật: " + ex.Message;
                Logger.Error("Lỗi kiểm tra cập nhật GitHub", ex);
            }
            finally { IsCheckingUpdate = false; }
        }

        private async Task DownloadUpdateAsync()
        {
            if (_latestUpdateInfo == null || !_latestUpdateInfo.IsUpdateAvailable) return;
            IsDownloadingUpdate = true;
            UpdateProgress = 0;
            UpdateStatusText = $"\u23F3 \u0110ang t\u1EA3i v\u00E0 c\u00E0i \u0111\u1EB7t tr\u1EF1c ti\u1EBFp v{_latestUpdateInfo.LatestVersion}...";
            try
            {
                var progress = new Progress<double>(pct =>
                {
                    UpdateProgress = pct;
                    UpdateStatusText = $"\u23F3 \u0110ang c\u1EADp nh\u1EADt tr\u1EF1c ti\u1EBFp: {pct:0.0}%";
                });
                await _updateService.DownloadAndApplyUpdateAsync(
                    _latestUpdateInfo,
                    progress,
                    msg => Application.Current?.Dispatcher?.Invoke(() => UpdateStatusText = msg)
                );
            }
            catch (Exception ex)
            {
                UpdateStatusText = "\u274C L\u1ED7i c\u1EADp nh\u1EADt: " + ex.Message;
                Logger.Error("L\u1ED7i t\u1EF1 \u0111\u1ED9ng c\u1EADp nh\u1EADt GitHub", ex);
            }
            finally { IsDownloadingUpdate = false; }
        }

        private async Task InitializeEngineAsync()
        {
            ConnectionStatus = "Đang khởi động Engine...";
            ConnectionColor = "#FFB300";
            Logger.Info("Tiến hành kết nối TTS Engine...");

            bool isReady = await _engineManager.StartEngineAsync();
            if (isReady)
            {
                ConnectionStatus = $"Sẵn sàng (v{AppVersion})";
                ConnectionColor = "#00E676";
                Logger.Info("Engine đã kết nối thành công tại cổng 8000.");
            }
            else
            {
                ConnectionStatus = "Lỗi kết nối Engine";
                ConnectionColor = "#FF5252";
                Logger.Error("Không thể kết nối với Engine API (Timeout).");
            }
        }

        private async Task SynthesizeAsync()
        {
            IsProcessing = true;
            SynthesisProgress = 5;
            SynthesisStatusText = "Đang phân tích ngữ cảnh & nạp Model...";
            ConnectionStatus = "Đang tổng hợp giọng nói...";
            ConnectionColor = "#00C8FF";

            using var cts = new CancellationTokenSource();
            var progressTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested && SynthesisProgress < 92)
                    {
                        await Task.Delay(140, cts.Token);
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            if (SynthesisProgress < 35)
                            {
                                SynthesisProgress += 4;
                                SynthesisStatusText = $"Đang xử lý văn bản với Model: {SelectedModelFile}...";
                            }
                            else if (SynthesisProgress < 72)
                            {
                                SynthesisProgress += 3;
                                SynthesisStatusText = IsVoiceCloneEnabled
                                    ? $"Đang đồng bộ cao độ F0 & âm sắc mẫu ({CloneStrengthPercent})..."
                                    : "Đang tổng hợp sóng âm nơ-ron (Speaker ID: " + SpeakerId + ")...";
                            }
                            else if (SynthesisProgress < 92)
                            {
                                SynthesisProgress += 1.5;
                                SynthesisStatusText = BypassDsp
                                    ? "Đang xuất trực tiếp sóng âm nguyên bản (Raw Mode)..."
                                    : $"Đang lọc Studio EQ (Bass: {EqBassDisplay}, Treble: {EqTrebleDisplay})...";
                            }
                        });
                    }
                }
                catch (TaskCanceledException) { }
            });

            try
            {
                var payload = new
                {
                    text = InputText,
                    voice_id = SelectedVoice,
                    model_file = SelectedModelFile,
                    speaker_id = SpeakerId,
                    bypass_dsp = BypassDsp,
                    gender = SelectedGender,
                    speed = Speed,
                    pitch = Pitch,
                    bilingual_auto = BilingualAuto,
                    single_voice_mode = SingleVoiceMode,
                    clone_enabled = IsVoiceCloneEnabled,
                    clone_reference_path = CloneReferencePath,
                    clone_strength = CloneStrength,
                    vits_noise_scale = VitsNoiseScale,
                    eq_bass_db = EqBassDb,
                    eq_treble_db = EqTrebleDb,
                    de_hiss_cutoff = DeHissCutoff
                };
                if (!await _engineManager.IsServerRunningAsync()) { await _engineManager.StartEngineAsync(); } var response = await _httpClient.PostAsJsonAsync("http://127.0.0.1:8000/synthesize", payload);
                cts.Cancel();

                if (response.IsSuccessStatusCode)
                {
                    SynthesisProgress = 96;
                    SynthesisStatusText = "Đang dựng sóng âm thanh & hoàn tất file .WAV...";

                    byte[] audioBytes = await response.Content.ReadAsByteArrayAsync();
                    string modTag = IsAutoModelSelected
                        ? (SelectedGender.Contains("Nam") ? "AutoNam" : "AutoNu")
                        : Path.GetFileNameWithoutExtension(SelectedModelFile.Replace("[", "").Replace("]", "")).Split(' ')[0];
                    string cloneTag = (IsVoiceCloneEnabled && !string.IsNullOrEmpty(CloneReferencePath)) ? "_CLONE" : "";
                    string rawTag = BypassDsp ? "_RAW" : "";
                    string fileName = $"VC_{modTag}{cloneTag}{rawTag}_{DateTime.Now:HHmmss}.wav";
                    string savedPath = Path.Combine(_outputDir, fileName);
                    await File.WriteAllBytesAsync(savedPath, audioBytes);

                    SynthesisProgress = 100;
                    SynthesisStatusText = "Hoàn tất 100%!";
                    await Task.Delay(100);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var newItem = new HistoryItem
                        {
                            DisplayText = $"[{DateTime.Now:HH:mm:ss}] {modTag}{cloneTag}{rawTag} | {Speed:0.0#}x",
                            FilePath = savedPath
                        };
                        HistoryList.Insert(0, newItem);
                        SelectedHistoryItem = newItem;
                        if (AutoPlayAudio)
                        {
                            _audioPlayer.Play();
                            _timer.Start();
                        }
                    });

                    ConnectionStatus = $"Sẵn sàng (v{AppVersion})";
                    ConnectionColor = "#00E676";
                }
                else
                {
                    ConnectionStatus = "Cảnh báo từ Engine";
                    ConnectionColor = "#FF5252";
                    string errDetail = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Lỗi từ Engine ({response.StatusCode}):\n{errDetail}", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                cts.Cancel();
                ConnectionStatus = "Lỗi kết nối Engine";
                ConnectionColor = "#FF5252";
                Logger.Error("Lỗi hệ thống khi gọi API Synthesize", ex);
                MessageBox.Show("Không thể kết nối tới TTS Engine. Hãy mở Cài đặt (⚙) -> Khởi động lại Engine!\nChi tiết: " + ex.Message, "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void SaveFile(object obj)
        {
            if (string.IsNullOrEmpty(_currentAudioPath) || !File.Exists(_currentAudioPath)) return;
            var dialog = new SaveFileDialog { Filter = "Wave Audio|*.wav", Title = "Lưu file âm thanh", FileName = Path.GetFileName(_currentAudioPath) };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    File.Copy(_currentAudioPath, dialog.FileName, true);
                    MessageBox.Show("Đã lưu file âm thanh thành công!", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Lỗi khi lưu file: " + ex.Message, "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Hỗ trợ vẽ sóng âm thanh cho cả file .WAV lẫn .MP3 (Khi chọn file mẫu Nhái giọng)
        private PointCollection GenerateWaveform(string filePath)
        {
            var points = new PointCollection();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return points;

            double width = 1000, height = 100, midY = height / 2;

            try
            {
                using (var reader = new AudioFileReader(filePath))
                {
                    int channels = Math.Max(1, reader.WaveFormat.Channels);
                    long totalFloats = reader.Length / 4;
                    int floatsPerPixel = Math.Max(channels, (int)(totalFloats / width));
                    float[] buffer = new float[floatsPerPixel];

                    var topPoints = new List<Point>();
                    var bottomPoints = new List<Point>();

                    int x = 0, floatsRead;
                    while ((floatsRead = reader.Read(buffer, 0, buffer.Length)) > 0 && x <= width)
                    {
                        float max = 0f;
                        for (int i = 0; i < floatsRead; i++)
                        {
                            float val = Math.Abs(buffer[i]);
                            if (val > max) max = val;
                        }
                        max = Math.Clamp(max, 0f, 1f);
                        topPoints.Add(new Point(x, midY - (max * midY * 0.92)));
                        bottomPoints.Insert(0, new Point(x, midY + (max * midY * 0.92)));
                        x++;
                    }

                    points.Add(new Point(0, midY));
                    foreach (var p in topPoints) points.Add(p);
                    foreach (var p in bottomPoints) points.Add(p);
                    points.Add(new Point(0, midY));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi vẽ waveform UI", ex);
            }

            return points;
        }

        public void Shutdown()
        {
            SaveCurrentModelProfile();
            SaveSettings();
            _timer?.Stop();
            _audioPlayer?.Dispose();
            _engineManager?.Dispose();
            _updateService?.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}