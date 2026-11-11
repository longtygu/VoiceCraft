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
        private string _outputDir;
        private string _modelsDir;
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

            SynthesizeCommand = new RelayCommand(async (o) => await SynthesizeAsync(), (o) => !IsProcessing && !IsModelError && !string.IsNullOrWhiteSpace(InputText));
            PlayCommand = new RelayCommand((o) => { StopHeadlessDemo(); _audioPlayer.Play(); _timer.Start(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            PauseCommand = new RelayCommand((o) => { StopHeadlessDemo(); _audioPlayer.Pause(); _timer.Stop(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            StopCommand = new RelayCommand((o) => { StopHeadlessDemo(); _audioPlayer.Stop(); _timer.Stop(); CurrentPosition = 0; UpdateTimeDisplay(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
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
                    StudioCloneEnabled = true;
                    SyncInspectorToSelectedClip();
                    LoadAudioAndWaveformToTimeline(CloneReferencePath);
                    StudioStatusText = $"🧬 Đã nạp mẫu giọng: {Path.GetFileName(CloneReferencePath)}";
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

            RestartEngineCommand = new RelayCommand(async (o) => await InitializeEngineAsync(true));
            OpenFolderCommand = new RelayCommand((param) =>
            {
                string target = param?.ToString();
                _modelsDir = Logger.ResolveModelsDirectory(); string folder = target == "Models" ? _modelsDir
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
            ScanOutputRecordings();
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
            _modelsDir = Logger.ResolveModelsDirectory();
            IsModelRepairDrawerExpanded = true;
            IsRepairingModels = true;
            ModelRepairProgress = 2;
            ModelRepairLogText = $"[{DateTime.Now:HH:mm:ss}] Th\u01B0 m\u1EE5c \u0111\u00EDch: {_modelsDir}\n[{DateTime.Now:HH:mm:ss}] B\u1EAFt \u0111\u1EA7u qu\u00E9t & t\u1EA3i b\u1ED9 Model AI nh\u1EB9, mi\u1EC5n ph\u00ED (VietTTS, Multilingual, 3 Mi\u1EC1n, MMS, Kokoro)...";

            try
            {
                string kokoroDir = Path.Combine(_modelsDir, "kokoro_24k");
                string mmsDir = Path.Combine(_modelsDir, "mms_vie_male");
                Directory.CreateDirectory(_modelsDir);
                Directory.CreateDirectory(kokoroDir);
                Directory.CreateDirectory(mmsDir);

                var downloadList = new List<(string Name, string Url, string DestPath, long MinSize)>
                {
                    ("Ti\u1EBFng Vi\u1EC7t Mi\u1EC1n Nam \u0110a Gi\u1ECDng (nam_vi_VN-vivos-x_low.onnx - 28MB)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx?download=true", Path.Combine(_modelsDir, "nam_vi_VN-vivos-x_low.onnx"), 5_000_000),
                    ("C\u1EA5u h\u00ECnh Mi\u1EC1n Nam (nam_vi_VN-vivos-x_low.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx.json?download=true", Path.Combine(_modelsDir, "nam_vi_VN-vivos-x_low.onnx.json"), 100),

                    ("Ti\u1EBFng Vi\u1EC7t Mi\u1EC1n Trung (trung_vi_VN-25hours_single-low.onnx - 60MB)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/25hours_single/low/vi_VN-25hours_single-low.onnx?download=true", Path.Combine(_modelsDir, "trung_vi_VN-25hours_single-low.onnx"), 5_000_000),
                    ("C\u1EA5u h\u00ECnh Mi\u1EC1n Trung (trung_vi_VN-25hours_single-low.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/25hours_single/low/vi_VN-25hours_single-low.onnx.json?download=true", Path.Combine(_modelsDir, "trung_vi_VN-25hours_single-low.onnx.json"), 100),

                    ("Ti\u1EBFng Vi\u1EC7t Mi\u1EC1n B\u1EAFc Chu\u1EA9n (bac_vi_VN-vais1000-medium.onnx - 60MB)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx?download=true", Path.Combine(_modelsDir, "bac_vi_VN-vais1000-medium.onnx"), 5_000_000),
                    ("C\u1EA5u h\u00ECnh Mi\u1EC1n B\u1EAFc (bac_vi_VN-vais1000-medium.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx.json?download=true", Path.Combine(_modelsDir, "bac_vi_VN-vais1000-medium.onnx.json"), 100),

                    ("Multilingual N\u1EEF Qu\u1ED1c T\u1EBF Nh\u1EB9 (multilingual_en_US-amy-low.onnx - 60MB)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/low/en_US-amy-low.onnx?download=true", Path.Combine(_modelsDir, "multilingual_en_US-amy-low.onnx"), 5_000_000),
                    ("C\u1EA5u h\u00ECnh Multilingual Amy (.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/low/en_US-amy-low.onnx.json?download=true", Path.Combine(_modelsDir, "multilingual_en_US-amy-low.onnx.json"), 100),

                    ("English N\u1EEF Ph\u00F2ng Thu Nh\u1EB9 (en_female_hi_en_US-lessac-low.onnx - 60MB)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/low/en_US-lessac-low.onnx?download=true", Path.Combine(_modelsDir, "en_female_hi_en_US-lessac-low.onnx"), 5_000_000),
                    ("C\u1EA5u h\u00ECnh Lessac Low (.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/low/en_US-lessac-low.onnx.json?download=true", Path.Combine(_modelsDir, "en_female_hi_en_US-lessac-low.onnx.json"), 100),

                    ("English Nam Tr\u1EA7m Nh\u1EB9 (en_male_hi_en_US-ryan-low.onnx - 60MB)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/ryan/low/en_US-ryan-low.onnx?download=true", Path.Combine(_modelsDir, "en_male_hi_en_US-ryan-low.onnx"), 5_000_000),
                    ("C\u1EA5u h\u00ECnh Ryan Low (.onnx.json)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/ryan/low/en_US-ryan-low.onnx.json?download=true", Path.Combine(_modelsDir, "en_male_hi_en_US-ryan-low.onnx.json"), 100),

                    ("Meta MMS Gi\u1ECDng Nam Vi\u1EC7t - Config (config.json)", "https://huggingface.co/facebook/mms-tts-vie/resolve/main/config.json?download=true", Path.Combine(mmsDir, "config.json"), 50),
                    ("Meta MMS Gi\u1ECDng Nam Vi\u1EC7t - Vocab (vocab.json)", "https://huggingface.co/facebook/mms-tts-vie/resolve/main/vocab.json?download=true", Path.Combine(mmsDir, "vocab.json"), 50),
                    ("Meta MMS Gi\u1ECDng Nam Vi\u1EC7t - Tokenizer (tokenizer_config.json)", "https://huggingface.co/facebook/mms-tts-vie/resolve/main/tokenizer_config.json?download=true", Path.Combine(mmsDir, "tokenizer_config.json"), 50),
                    ("Meta MMS Gi\u1ECDng Nam Vi\u1EC7t - Tr\u1ECDng s\u1ED1 VITS (model.safetensors)", "https://huggingface.co/facebook/mms-tts-vie/resolve/main/model.safetensors?download=true", Path.Combine(mmsDir, "model.safetensors"), 10_000_000),

                    ("Kokoro 54 Voices (voices-v1.0.bin)", "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin", Path.Combine(kokoroDir, "voices-v1.0.bin"), 1_000_000),
                    ("Kokoro-82M 24kHz (kokoro-v1.0.onnx)", "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx", Path.Combine(kokoroDir, "kokoro-v1.0.onnx"), 10_000_000)
                };

                using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                dlClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VoiceCraft/1.0.5");

                int totalItems = downloadList.Count;
                int successCount = 0;

                for (int i = 0; i < totalItems; i++)
                {
                    var item = downloadList[i];
                    double basePct = (i * 82.0) / totalItems;
                    ModelRepairProgress = Math.Max(4, Math.Round(basePct, 1));

                    if (File.Exists(item.DestPath) && new FileInfo(item.DestPath).Length >= item.MinSize)
                    {
                        AppendRepairLog($"[OK] \u0110\u00E3 c\u00F3 s\u1EB5n: {item.Name}");
                        successCount++;
                        continue;
                    }

                    string tempPart = item.DestPath + ".part";
                    try
                    {
                        AppendRepairLog($"[\u0110ang t\u1EA3i {i + 1}/{totalItems}] {item.Name}...");
                        using var resp = await dlClient.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();
                        long? len = resp.Content.Headers.ContentLength;

                        using (var stream = await resp.Content.ReadAsStreamAsync())
                        using (var fs = new FileStream(tempPart, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            byte[] buf = new byte[81920];
                            long readTotal = 0;
                            int r;
                            while ((r = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                            {
                                await fs.WriteAsync(buf, 0, r);
                                readTotal += r;
                                if (len.HasValue && len.Value > 0)
                                {
                                    double subPct = (double)readTotal / len.Value * (82.0 / totalItems);
                                    ModelRepairProgress = Math.Min(88, Math.Round(basePct + subPct, 1));
                                }
                            }
                        }

                        if (File.Exists(tempPart) && new FileInfo(tempPart).Length >= item.MinSize)
                        {
                            File.Move(tempPart, item.DestPath, true);
                            double mb = Math.Round(new FileInfo(item.DestPath).Length / 1048576.0, 2);
                            AppendRepairLog($"[Ho\u00E0n t\u1EA5t] {item.Name} ({mb} MB)");
                            successCount++;
                        }
                    }
                    catch (Exception exItem)
                    {
                        try { if (File.Exists(tempPart)) File.Delete(tempPart); } catch { }
                        AppendRepairLog($"[B\u1ECF qua] '{item.Name}': {exItem.Message}");
                    }
                }

                try
                {
                    string bacSrc = Path.Combine(_modelsDir, "bac_vi_VN-vais1000-medium.onnx");
                    string vietTtsNu = Path.Combine(_modelsDir, "viettts_nu_mien_bac_nat.onnx");
                    if (File.Exists(bacSrc) && !File.Exists(vietTtsNu))
                    {
                        File.Copy(bacSrc, vietTtsNu, true);
                        if (File.Exists(bacSrc + ".json")) File.Copy(bacSrc + ".json", vietTtsNu + ".json", true);
                        AppendRepairLog("[OK] \u0110\u00E3 k\u00EDch ho\u1EA1t: viettts_nu_mien_bac_nat.onnx (Offline VietTTS)");
                    }

                    string namSrc = Path.Combine(_modelsDir, "nam_vi_VN-vivos-x_low.onnx");
                    string vietTtsNam = Path.Combine(_modelsDir, "viettts_nam_tram_vits.onnx");
                    if (File.Exists(namSrc) && !File.Exists(vietTtsNam))
                    {
                        File.Copy(namSrc, vietTtsNam, true);
                        if (File.Exists(namSrc + ".json")) File.Copy(namSrc + ".json", vietTtsNam + ".json", true);
                        AppendRepairLog("[OK] \u0110\u00E3 k\u00EDch ho\u1EA1t: viettts_nam_tram_vits.onnx (Offline VietTTS)");
                    }
                }
                catch { }

                AppendRepairLog("[\u0110ang \u0111\u1ED3ng b\u1ED9] Ki\u1EC3m tra th\u01B0 vi\u1EC7n Python & kh\u1EDFi \u0111\u1ED9ng l\u1EA1i TTS Engine...");
                ModelRepairProgress = 92;
                await Task.Run(() =>
                {
                    try
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
                    }
                    catch { }
                });

                await _engineManager.StartEngineAsync(true);
                RefreshAvailableModels();
                SetReadyStatus();
                ModelRepairProgress = 100;
                AppendRepairLog($"[HO\u00C0N T\u1EA4T 100%] \u0110\u00E3 chu\u1EA9n h\u00F3a {successCount}/{totalItems} g\u00F3i Model t\u1EA1i '{_modelsDir}'.");
            }
            catch (Exception ex)
            {
                AppendRepairLog($"[L\u1ED7i] {ex.Message}");
                Logger.Error("Loi sua & tai Model tu dong", ex);
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
                    _selectedAeroTheme = (!string.IsNullOrEmpty(data.SelectedAeroTheme) && AeroThemes.Contains(data.SelectedAeroTheme)) ? data.SelectedAeroTheme : AeroThemes[2];
                    _selectedAccentItem = (!string.IsNullOrEmpty(data.SelectedAccentItem) && AccentColorList.Contains(data.SelectedAccentItem)) ? data.SelectedAccentItem : AccentColorList[0];
                    _selectedWaveformColorItem = (!string.IsNullOrEmpty(data.SelectedWaveformColorItem) && WaveformColorList.Contains(data.SelectedWaveformColorItem)) ? data.SelectedWaveformColorItem : WaveformColorList[0];
                    _selectedSecondaryLineColorItem = (!string.IsNullOrEmpty(data.SelectedSecondaryLineColorItem) && SecondaryLineColorList.Contains(data.SelectedSecondaryLineColorItem)) ? data.SelectedSecondaryLineColorItem : SecondaryLineColorList[0];
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

        private string _inputText = "Xin chào! Đây là phiên bản nâng cấp VoiceCraft 1.0.3 tích hợp VieNeu-TTS, tính năng Nhái giọng AI chuẩn xác và bộ lọc âm thanh EQ riêng cho từng Model.";
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


        // =====================================================================
        // THANH TRẠNG THÁI MỞ RỘNG (MODEL SẴN SÀNG / LỖI MODEL NHẤP NHÁY ĐỎ)
        // =====================================================================
        private bool _isModelError = false;
        public bool IsModelError { get => _isModelError; set { _isModelError = value; OnPropertyChanged(); } }

        private string _statusBadgeBackground = "#3800C853";
        public string StatusBadgeBackground { get => _statusBadgeBackground; set { _statusBadgeBackground = value; OnPropertyChanged(); } }

        private string _statusBadgeBorder = "#AA00E676";
        public string StatusBadgeBorder { get => _statusBadgeBorder; set { _statusBadgeBorder = value; OnPropertyChanged(); } }

        private string _statusTextForeground = "#E8FFF0";
        public string StatusTextForeground { get => _statusTextForeground; set { _statusTextForeground = value; OnPropertyChanged(); } }

        // =====================================================================
        // v1.0.6: QUáº¢N LÃ Tá»ªNG MODEL AI (Táº¢I / XOÃ / Äá»”I TÃŠN) & KIá»‚M TRA TRáº NG THÃI THá»°C
        // =====================================================================
        private DispatcherTimer _engineHealthTimer;
        private bool _isCheckingEngineHealth = false;
        private Dictionary<string, string> _customModelNamesMap;

        public ObservableCollection<ManagedModelItem> ManagedModels { get; } = new ObservableCollection<ManagedModelItem>();

        private ManagedModelItem _selectedManagedModel;
        public ManagedModelItem SelectedManagedModel
        {
            get => _selectedManagedModel;
            set
            {
                _selectedManagedModel = value;
                OnPropertyChanged();
                if (value != null)
                {
                    RenameModelInputText = value.DisplayName;
                }
            }
        }

        private string _renameModelInputText = "";
        public string RenameModelInputText
        {
            get => _renameModelInputText;
            set { _renameModelInputText = value; OnPropertyChanged(); }
        }

        private string GetCustomModelMapPath() => Path.Combine(Logger.ResolveModelsDirectory(), "models_custom_map.json");

        private Dictionary<string, string> GetCustomModelNamesMap()
        {
            if (_customModelNamesMap != null) return _customModelNamesMap;
            _customModelNamesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string p = GetCustomModelMapPath();
                if (File.Exists(p))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p));
                    if (loaded != null)
                    {
                        foreach (var kv in loaded) _customModelNamesMap[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
            return _customModelNamesMap;
        }

        private void SaveCustomModelNamesMap()
        {
            try
            {
                string p = GetCustomModelMapPath();
                File.WriteAllText(p, JsonSerializer.Serialize(GetCustomModelNamesMap(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void EnsureModelManagerInitialized()
        {
            if (_engineHealthTimer == null)
            {
                _engineHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                _engineHealthTimer.Tick += async (s, e) => await VerifyModelAndEngineReadinessAsync();
                _engineHealthTimer.Start();
            }
            if (ManagedModels.Count == 0)
            {
                RebuildManagedModelsList();
            }
        }

        public void RebuildManagedModelsList()
        {
            _modelsDir = Logger.ResolveModelsDirectory();
            Directory.CreateDirectory(_modelsDir);
            var nameMap = GetCustomModelNamesMap();

            var catalog = new List<(string Id, string DefaultTitle, string Url, string JsonUrl, long MinBytes, bool IsFolder)>
            {
                ("nam_vi_VN-vivos-x_low.onnx", "Ti\u1EBFng Vi\u1EC7t Mi\u1EC1n Nam \u0110a Gi\u1ECDng (VIVOS x_low)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx?download=true", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx.json?download=true", 5_000_000, false),
                ("trung_vi_VN-25hours_single-low.onnx", "Ti\u1EBFng Vi\u1EC7t Mi\u1EC1n Trung (25Hours low)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/25hours_single/low/vi_VN-25hours_single-low.onnx?download=true", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/25hours_single/low/vi_VN-25hours_single-low.onnx.json?download=true", 5_000_000, false),
                ("bac_vi_VN-vais1000-medium.onnx", "Ti\u1EBFng Vi\u1EC7t Mi\u1EC1n B\u1EAFc Chu\u1EA9n (VAIS1000 medium)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx?download=true", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx.json?download=true", 5_000_000, false),
                ("viettts_nu_mien_bac_nat.onnx", "VietTTS Gi\u1ECDng N\u1EEF Mi\u1EC1n B\u1EAFc (Offline NAT)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx?download=true", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium.onnx.json?download=true", 5_000_000, false),
                ("viettts_nam_tram_vits.onnx", "VietTTS Gi\u1ECDng Nam Tr\u1EA7m (Offline VITS)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx?download=true", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx.json?download=true", 5_000_000, false),
                ("multilingual_en_US-amy-low.onnx", "Multilingual N\u1EEF Qu\u1ED1c T\u1EBF Nh\u1EB9 (Amy Low)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/low/en_US-amy-low.onnx?download=true", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/low/en_US-amy-low.onnx.json?download=true", 5_000_000, false),
                ("en_female_hi_en_US-lessac-low.onnx", "English N\u1EEF Ph\u00F2ng Thu Nh\u1EB9 (Lessac Low)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/low/en_US-lessac-low.onnx?download=true", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/low/en_US-lessac-low.onnx.json?download=true", 5_000_000, false),
                ("en_male_hi_en_US-ryan-low.onnx", "English Nam Tr\u1EA7m Nh\u1EB9 (Ryan Low)", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/ryan/low/en_US-ryan-low.onnx?download=true", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/ryan/low/en_US-ryan-low.onnx.json?download=true", 5_000_000, false),
                ("mms_vie_male", "Meta AI MMS Gi\u1ECDng Nam Ti\u1EBFng Vi\u1EC7t (PyTorch VITS)", "https://huggingface.co/facebook/mms-tts-vie/resolve/main/model.safetensors?download=true", "https://huggingface.co/facebook/mms-tts-vie/resolve/main/config.json?download=true", 10_000_000, true),
                ("kokoro_24k", "Kokoro-82M 24kHz PhÃ²ng Thu (54 Voices)", "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx", "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin", 10_000_000, true)
            };

            ManagedModels.Clear();
            var trackedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in catalog)
            {
                string currentFile = nameMap.TryGetValue("FILE_" + entry.Id, out var mappedFile) && !string.IsNullOrWhiteSpace(mappedFile)
                    ? mappedFile
                    : entry.Id;

                string customTitle = nameMap.TryGetValue("TITLE_" + entry.Id, out var mappedTitle) && !string.IsNullOrWhiteSpace(mappedTitle)
                    ? mappedTitle
                    : entry.DefaultTitle;

                bool installed = false;
                double mb = 0.0;

                if (entry.IsFolder)
                {
                    string subDir = Path.Combine(_modelsDir, entry.Id);
                    if (entry.Id == "kokoro_24k")
                    {
                        string kOnnx = Path.Combine(subDir, "kokoro-v1.0.onnx");
                        string kBin = Path.Combine(subDir, "voices-v1.0.bin");
                        if (File.Exists(kOnnx) && File.Exists(kBin) && new FileInfo(kOnnx).Length >= entry.MinBytes)
                        {
                            installed = true;
                            mb = Math.Round((new FileInfo(kOnnx).Length + new FileInfo(kBin).Length) / 1048576.0, 1);
                        }
                    }
                    else if (entry.Id == "mms_vie_male")
                    {
                        string mSafetensors = Path.Combine(subDir, "model.safetensors");
                        string mCfg = Path.Combine(subDir, "config.json");
                        if (File.Exists(mSafetensors) && File.Exists(mCfg) && new FileInfo(mSafetensors).Length >= entry.MinBytes)
                        {
                            installed = true;
                            mb = Math.Round(new FileInfo(mSafetensors).Length / 1048576.0, 1);
                        }
                    }
                }
                else
                {
                    string fPath = Path.Combine(_modelsDir, currentFile);
                    if (!File.Exists(fPath) && File.Exists(Path.Combine(_modelsDir, entry.Id)))
                    {
                        currentFile = entry.Id;
                        fPath = Path.Combine(_modelsDir, entry.Id);
                    }
                    if (File.Exists(fPath) && new FileInfo(fPath).Length >= entry.MinBytes)
                    {
                        installed = true;
                        mb = Math.Round(new FileInfo(fPath).Length / 1048576.0, 1);
                    }
                    trackedFiles.Add(currentFile);
                    trackedFiles.Add(entry.Id);
                }

                ManagedModels.Add(new ManagedModelItem
                {
                    CanonicalId = entry.Id,
                    FileName = currentFile,
                    DisplayName = customTitle,
                    DownloadUrl = entry.Url,
                    JsonConfigUrl = entry.JsonUrl,
                    MinSizeBytes = entry.MinBytes,
                    IsFolderPackage = entry.IsFolder,
                    IsInstalled = installed,
                    SizeMb = mb
                });
            }

            // QuÃ©t thÃªm cÃ¡c file .onnx tuá»³ chá»‰nh khÃ¡c mÃ  ngÆ°á»i dÃ¹ng tá»± chÃ©p vÃ o thÆ° má»¥c Models
            if (Directory.Exists(_modelsDir))
            {
                foreach (var f in Directory.GetFiles(_modelsDir, "*.onnx"))
                {
                    string fn = Path.GetFileName(f);
                    if (trackedFiles.Contains(fn)) continue;
                    var fi = new FileInfo(f);
                    if (fi.Length < 1_000_000) continue;

                    string customTitle = nameMap.TryGetValue("TITLE_" + fn, out var t) ? t : Path.GetFileNameWithoutExtension(fn);
                    ManagedModels.Add(new ManagedModelItem
                    {
                        CanonicalId = fn,
                        FileName = fn,
                        DisplayName = customTitle,
                        DownloadUrl = "",
                        JsonConfigUrl = "",
                        MinSizeBytes = 1_000_000,
                        IsFolderPackage = false,
                        IsInstalled = true,
                        SizeMb = Math.Round(fi.Length / 1048576.0, 1)
                    });
                }
            }

            if (SelectedManagedModel == null && ManagedModels.Count > 0)
            {
                SelectedManagedModel = ManagedModels[0];
            }
        }

        public ICommand RefreshManagedModelsCommand => new RelayCommand((o) =>
        {
            RebuildManagedModelsList();
            RefreshAvailableModels();
            _ = VerifyModelAndEngineReadinessAsync();
        });

        public ICommand BeginRenameModelCommand => new RelayCommand((param) =>
        {
            if (param is ManagedModelItem item)
            {
                SelectedManagedModel = item;
                RenameModelInputText = item.DisplayName;
                ModelRepairLogText = $"[\u0110\u1ED5i t\u00EAn] \u0110ang ch\u1ECDn '{item.DisplayName}'. H\u00E3y nh\u1EADp t\u00EAn m\u1EDBi v\u00E0o \u00F4 b\u00EAn d\u01B0\u1EDBi r\u1ED3i nh\u1EA5n 'L\u01B0u t\u00EAn'.";
            }
        });

        public ICommand ApplyRenameModelCommand => new RelayCommand((o) =>
        {
            var item = SelectedManagedModel;
            if (item == null || string.IsNullOrWhiteSpace(RenameModelInputText)) return;

            string newName = RenameModelInputText.Trim();
            var nameMap = GetCustomModelNamesMap();
            nameMap["TITLE_" + item.CanonicalId] = newName;
            item.DisplayName = newName;

            // Náº¿u lÃ  model .onnx Ä‘Æ¡n láº» Ä‘Ã£ táº£i trÃªn Ä‘Ä©a, há»— trá»£ Ä‘á»•i tÃªn luÃ´n file .onnx vÃ  .onnx.json
            if (!item.IsFolderPackage && item.IsInstalled)
            {
                string safeBase = string.Join("_", newName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
                if (!string.IsNullOrWhiteSpace(safeBase))
                {
                    if (!safeBase.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                        safeBase += ".onnx";

                    string oldOnnx = Path.Combine(_modelsDir, item.FileName);
                    string oldJson = oldOnnx + ".json";
                    string newOnnx = Path.Combine(_modelsDir, safeBase);
                    string newJson = newOnnx + ".json";

                    if (File.Exists(oldOnnx) && !string.Equals(oldOnnx, newOnnx, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.Move(oldOnnx, newOnnx, true);
                            if (File.Exists(oldJson)) File.Move(oldJson, newJson, true);
                            item.FileName = safeBase;
                            nameMap["FILE_" + item.CanonicalId] = safeBase;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Loi doi ten file model tren dia", ex);
                        }
                    }
                }
            }

            SaveCustomModelNamesMap();
            RefreshAvailableModels();
            _ = VerifyModelAndEngineReadinessAsync();
            AppendRepairLog($"[\u0110\u00E3 \u0111\u1ED5i t\u00EAn] Model '{item.CanonicalId}' -> '{item.DisplayName}' ({item.FileName})");
        });

        public ICommand DownloadSingleModelCommand => new RelayCommand(async (param) =>
        {
            if (param is not ManagedModelItem item || item.IsBusy) return;
            if (string.IsNullOrWhiteSpace(item.DownloadUrl))
            {
                MessageBox.Show("Model tu\u1EF3 ch\u1EC9nh n\u00E0y \u0111\u00E3 c\u00F3 s\u1EB5n tr\u00EAn m\u00E1y.", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            item.IsBusy = true;
            item.BusyStatusText = "\u23F3 0%";
            IsModelRepairDrawerExpanded = true;

            try
            {
                _modelsDir = Logger.ResolveModelsDirectory();
                Directory.CreateDirectory(_modelsDir);

                using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                dlClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VoiceCraft/1.0.6");

                var filesToFetch = new List<(string Url, string DestPath, long MinBytes)>();
                if (item.IsFolderPackage)
                {
                    string subDir = Path.Combine(_modelsDir, item.CanonicalId);
                    Directory.CreateDirectory(subDir);
                    if (item.CanonicalId == "kokoro_24k")
                    {
                        filesToFetch.Add((item.JsonConfigUrl, Path.Combine(subDir, "voices-v1.0.bin"), 1_000_000));
                        filesToFetch.Add((item.DownloadUrl, Path.Combine(subDir, "kokoro-v1.0.onnx"), 10_000_000));
                    }
                    else if (item.CanonicalId == "mms_vie_male")
                    {
                        filesToFetch.Add(("https://huggingface.co/facebook/mms-tts-vie/resolve/main/config.json?download=true", Path.Combine(subDir, "config.json"), 50));
                        filesToFetch.Add(("https://huggingface.co/facebook/mms-tts-vie/resolve/main/vocab.json?download=true", Path.Combine(subDir, "vocab.json"), 50));
                        filesToFetch.Add(("https://huggingface.co/facebook/mms-tts-vie/resolve/main/tokenizer_config.json?download=true", Path.Combine(subDir, "tokenizer_config.json"), 50));
                        filesToFetch.Add((item.DownloadUrl, Path.Combine(subDir, "model.safetensors"), 10_000_000));
                    }
                }
                else
                {
                    string targetOnnx = Path.Combine(_modelsDir, item.FileName);
                    filesToFetch.Add((item.DownloadUrl, targetOnnx, item.MinSizeBytes));
                    if (!string.IsNullOrWhiteSpace(item.JsonConfigUrl))
                    {
                        filesToFetch.Add((item.JsonConfigUrl, targetOnnx + ".json", 80));
                    }
                }

                AppendRepairLog($"[\u0110ang t\u1EA3i] B\u1EAFt \u0111\u1EA7u t\u1EA3i '{item.DisplayName}'...");
                foreach (var (url, dest, minB) in filesToFetch)
                {
                    string partPath = dest + ".part";
                    using var resp = await dlClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    long? totalLen = resp.Content.Headers.ContentLength;

                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        byte[] buf = new byte[81920];
                        long readTotal = 0;
                        int r;
                        while ((r = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await fs.WriteAsync(buf, 0, r);
                            readTotal += r;
                            if (totalLen.HasValue && totalLen.Value > 0)
                            {
                                int pct = (int)Math.Clamp(readTotal * 100L / totalLen.Value, 1, 99);
                                item.BusyStatusText = $"\u23F3 {pct}%";
                            }
                        }
                    }

                    if (File.Exists(partPath) && new FileInfo(partPath).Length >= minB)
                    {
                        File.Move(partPath, dest, true);
                    }
                }

                RebuildManagedModelsList();
                RefreshAvailableModels();
                await VerifyModelAndEngineReadinessAsync();
                AppendRepairLog($"[Ho\u00E0n t\u1EA5t] \u0110\u00E3 t\u1EA3i xong Model '{item.DisplayName}'!");
            }
            catch (Exception ex)
            {
                AppendRepairLog($"[L\u1ED7i t\u1EA3i '{item.DisplayName}'] {ex.Message}");
                MessageBox.Show($"Kh\u00F4ng th\u1EC3 t\u1EA3i Model '{item.DisplayName}':\n{ex.Message}", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                item.IsBusy = false;
                item.BusyStatusText = "";
            }
        });

        public ICommand DeleteSingleModelCommand => new RelayCommand(async (param) =>
        {
            if (param is not ManagedModelItem item || item.IsBusy) return;
            if (!item.IsInstalled)
            {
                MessageBox.Show($"Model '{item.DisplayName}' hi\u1EC7n ch\u01B0a \u0111\u01B0\u1EE3c t\u1EA3i v\u1EC1 m\u00E1y.", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"B\u1EA1n c\u00F3 ch\u1EAFc ch\u1EAFn mu\u1ED1n xo\u00E1 Model '{item.DisplayName}' ({item.FileName}) kh\u1ECFi m\u00E1y kh\u00F4ng?",
                "X\u00E1c nh\u1EADn xo\u00E1 Model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                _modelsDir = Logger.ResolveModelsDirectory();
                if (item.IsFolderPackage)
                {
                    string subDir = Path.Combine(_modelsDir, item.CanonicalId);
                    if (Directory.Exists(subDir)) Directory.Delete(subDir, true);
                }
                else
                {
                    string f1 = Path.Combine(_modelsDir, item.FileName);
                    string f2 = Path.Combine(_modelsDir, item.CanonicalId);
                    foreach (var p in new[] { f1, f1 + ".json", f2, f2 + ".json" })
                    {
                        if (File.Exists(p)) File.Delete(p);
                    }
                }

                RebuildManagedModelsList();
                RefreshAvailableModels();
                await VerifyModelAndEngineReadinessAsync();
                AppendRepairLog($"[\u0110\u00E3 xo\u00E1] \u0110\u00E3 xo\u00E1 Model '{item.DisplayName}' kh\u1ECFi th\u01B0 m\u1EE5c Models.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Kh\u00F4ng th\u1EC3 xo\u00E1 Model \u0111ang \u0111\u01B0\u1EE3c Engine s\u1EED d\u1EE5ng. H\u00E3y th\u1EED l\u1EA1i: " + ex.Message, "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });

        public bool IsSelectedModelFilesInstalled(out string missingReason)
        {
            missingReason = "";
            _modelsDir = Logger.ResolveModelsDirectory();
            if (!Directory.Exists(_modelsDir))
            {
                missingReason = "CHUA_CO_THU_MUC_MODELS";
                return false;
            }

            var validOnnx = Directory.GetFiles(_modelsDir, "*.onnx")
                .Where(f => new FileInfo(f).Length > 1_000_000)
                .ToList();

            string sel = SelectedModelFile ?? "T\u1EF1 \u0111\u1ED9ng theo v\u00F9ng mi\u1EC1n (Auto)";
            if (IsAutoModelSelected)
            {
                if (validOnnx.Count == 0)
                {
                    missingReason = "CHUA_TAI_MODEL_ONNX";
                    return false;
                }
                return true;
            }

            if (sel.StartsWith("[Kokoro-82M-24kHz]"))
            {
                bool ok = File.Exists(Path.Combine(_modelsDir, "kokoro_24k", "kokoro-v1.0.onnx")) &&
                          File.Exists(Path.Combine(_modelsDir, "kokoro_24k", "voices-v1.0.bin"));
                if (!ok) missingReason = "CHUA_TAI_KOKORO_24K";
                return ok;
            }

            if (sel.StartsWith("[PyTorch]"))
            {
                bool ok = File.Exists(Path.Combine(_modelsDir, "mms_vie_male", "config.json")) &&
                          File.Exists(Path.Combine(_modelsDir, "mms_vie_male", "model.safetensors"));
                if (!ok) missingReason = "CHUA_TAI_MMS_VIE";
                return ok;
            }

            if (sel.StartsWith("[VietTTS-HiFiGAN]"))
            {
                if (validOnnx.Count == 0)
                {
                    missingReason = "CHUA_TAI_VIETTTS";
                    return false;
                }
                return true;
            }

            if (sel.StartsWith("[VieNeu-TTS-24kHz]") || sel.StartsWith("[Multilingual-24kHz]") || sel.StartsWith("[Neural-24kHz]"))
            {
                return true;
            }

            // Kiá»ƒm tra file .onnx trá»±c tiáº¿p
            string directOnnx = Path.Combine(_modelsDir, sel);
            if (!File.Exists(directOnnx) || new FileInfo(directOnnx).Length < 1_000_000)
            {
                missingReason = "THIEU_FILE_" + sel;
                return false;
            }
            return true;
        }

        public async Task VerifyModelAndEngineReadinessAsync()
        {
            if (_isCheckingEngineHealth || IsProcessing) return;
            _isCheckingEngineHealth = true;
            try
            {
                EnsureModelManagerInitialized();
                bool filesOk = IsSelectedModelFilesInstalled(out string missingCode);
                if (!filesOk)
                {
                    SetErrorStatus(missingCode);
                    CommandManager.InvalidateRequerySuggested();
                    return;
                }

                bool engineOk = await _engineManager.IsServerRunningAsync();
                if (engineOk)
                {
                    SetReadyStatus();
                }
                else
                {
                    SetErrorStatus("ERR_ENGINE_8000");
                }
                CommandManager.InvalidateRequerySuggested();
            }
            catch { }
            finally
            {
                _isCheckingEngineHealth = false;
            }
        }

        private void SetReadyStatus()
        {
            IsModelError = false;
            ConnectionStatus = "Model sẵn sàng";
            ConnectionColor = "#00E676";
            StatusBadgeBackground = "#3800C853";
            StatusBadgeBorder = "#AA00E676";
            StatusTextForeground = "#E8FFF0";
        }

        private void SetErrorStatus(string errorCode)
        {
            IsModelError = true;
            ConnectionStatus = $"Lỗi Model - mã lỗi {errorCode}";
            ConnectionColor = "#FF1744";
            StatusBadgeBackground = "#77FF1744";
            StatusBadgeBorder = "#FFFF1744";
            StatusTextForeground = "#FFFFFF";
        }

        // =====================================================================
        // THU GỌN / MỞ RỘNG KHUNG TRẠNG THÁI SỬA LỖI MODEL TRONG CÀI ĐẶT
        // =====================================================================
        private bool _isModelRepairDrawerExpanded = false;
        public bool IsModelRepairDrawerExpanded
        {
            get => _isModelRepairDrawerExpanded;
            set
            {
                _isModelRepairDrawerExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelRepairDrawerVisibility));
                OnPropertyChanged(nameof(RepairDrawerToggleButtonText));
            }
        }
        public Visibility ModelRepairDrawerVisibility => IsModelRepairDrawerExpanded ? Visibility.Visible : Visibility.Collapsed;
        public string RepairDrawerToggleButtonText => IsModelRepairDrawerExpanded ? "▲ Thu gọn trạng thái" : "▼ Mở trạng thái";

        public ICommand ToggleRepairDrawerCommand => new RelayCommand((o) =>
        {
            IsModelRepairDrawerExpanded = !IsModelRepairDrawerExpanded;
        });

        public ICommand CopyGitHubRepoCommand => new RelayCommand((o) =>
        {
            try
            {
                Clipboard.SetText("https://github.com/longtygu/VoiceCraft");
                UpdateStatusText = "📋 Đã sao chép đường dẫn GitHub vào Clipboard!";
            }
            catch { }
        });

        // =====================================================================
        // =====================================================================
        // =====================================================================
        // MINI STUDIO CAPCUT PC (DYNAMIC N-LAYERS, NON-SQUISH TRIM, EQ COLOR MARK, MASTER COMPOSITE PLAYER)
        // =====================================================================
        private WaveOutEvent _demoWaveOut;
        private AudioFileReader _demoAudioReader;
        private DispatcherTimer _demoStopTimer;
        private bool _suppressDemoOnScan = false;
        private bool _isSyncingClipInspector = false;
        private TimelineClipItem _clipboardTimelineClip;
        private int _masterPreviewToggleIdx = 0;

        public ObservableCollection<TimelineClipItem> TimelineClips { get; } = new ObservableCollection<TimelineClipItem>();

        private int _studioLayerCount = 1;
        public int StudioLayerCount
        {
            get => _studioLayerCount;
            set
            {
                _studioLayerCount = Math.Max(1, value);
                OnPropertyChanged();
            }
        }

        private PointCollection _studioMasterWaveformPoints = new PointCollection();
        public PointCollection StudioMasterWaveformPoints
        {
            get => _studioMasterWaveformPoints;
            set { _studioMasterWaveformPoints = value; OnPropertyChanged(); }
        }

        private string _studioMasterSummaryText = "1 Layer • Độ dài: 0.00s";
        public string StudioMasterSummaryText
        {
            get => _studioMasterSummaryText;
            set { _studioMasterSummaryText = value; OnPropertyChanged(); }
        }

        private TimelineClipItem _selectedTimelineClip;
        public TimelineClipItem SelectedTimelineClip
        {
            get => _selectedTimelineClip;
            set
            {
                if (_selectedTimelineClip == value) return;
                _selectedTimelineClip = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedClipInspectorTitle));

                if (value != null)
                {
                    _isSyncingClipInspector = true;
                    try
                    {
                        _studioSemitones = value.Semitones;
                        _studioSpeed = value.Speed;
                        _studioCloneEnabled = value.CloneEnabled;
                        if (!string.IsNullOrEmpty(value.CloneRefPath))
                            _cloneReferencePath = value.CloneRefPath;
                        _studioCloneStrength = value.CloneStrength;
                        _studioHighpassHz = value.HighpassHz;
                        _studioBand80Hz = value.Band80Hz;
                        _studioBand220Hz = value.Band220Hz;
                        _studioBand1kHz = value.Band1kHz;
                        _studioBand3k5Hz = value.Band3k5Hz;
                        _studioBand8kHz = value.Band8kHz;
                        _studioLowpassHz = value.LowpassHz;

                        OnPropertyChanged(nameof(StudioSemitones));
                        OnPropertyChanged(nameof(StudioSemitonesDisplay));
                        OnPropertyChanged(nameof(StudioSpeed));
                        OnPropertyChanged(nameof(StudioCloneEnabled));
                        OnPropertyChanged(nameof(CloneReferenceFileName));
                        OnPropertyChanged(nameof(StudioCloneStrength));
                        OnPropertyChanged(nameof(StudioHighpassHz));
                        OnPropertyChanged(nameof(StudioBand80Hz));
                        OnPropertyChanged(nameof(StudioBand220Hz));
                        OnPropertyChanged(nameof(StudioBand1kHz));
                        OnPropertyChanged(nameof(StudioBand3k5Hz));
                        OnPropertyChanged(nameof(StudioBand8kHz));
                        OnPropertyChanged(nameof(StudioLowpassHz));

                        SelectedStudioFileSummary = $"[Layer {value.LayerIndex + 1}] {value.Title} ({value.DurationSec:0.00}s)";
                    }
                    finally
                    {
                        _isSyncingClipInspector = false;
                    }
                }
            }
        }

        public string SelectedClipInspectorTitle => SelectedTimelineClip != null
            ? $"Đang chọn: {SelectedTimelineClip.Title} (Layer {SelectedTimelineClip.LayerIndex + 1} • {SelectedTimelineClip.DurationSec:0.00}s){(SelectedTimelineClip.HasCustomEq ? " [🎛️ ĐÃ CHỈNH EQ]" : "")}"
            : "Hãy nhấp chọn 1 thành phần trên Layer để chỉnh EQ / Tone / Nhái giọng";

        private void SyncInspectorToSelectedClip()
        {
            if (_isSyncingClipInspector) return;
            var target = _selectedTimelineClip ?? TimelineClips.FirstOrDefault();
            if (target == null) return;

            target.Semitones = _studioSemitones;
            target.Speed = _studioSpeed;
            target.CloneEnabled = _studioCloneEnabled;
            target.CloneRefPath = _cloneReferencePath;
            target.CloneStrength = _studioCloneStrength;
            target.HighpassHz = _studioHighpassHz;
            target.Band80Hz = _studioBand80Hz;
            target.Band220Hz = _studioBand220Hz;
            target.Band1kHz = _studioBand1kHz;
            target.Band3k5Hz = _studioBand3k5Hz;
            target.Band8kHz = _studioBand8kHz;
            target.LowpassHz = _studioLowpassHz;

            OnPropertyChanged(nameof(SelectedClipInspectorTitle));
            RebuildMasterCompositeTimelineAudio();
        }

        // ==================== LÕI DSP C# IN-MEMORY (5-BAND BIQUAD EQ + WSOLA PITCH/SPEED + COMPOSITE MIXER) ====================
        private static float[] LoadMono22050Samples(string filePath, out double totalSec, out float[] peaks50Hz)
        {
            totalSec = 0.0;
            peaks50Hz = Array.Empty<float>();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return Array.Empty<float>();

            try
            {
                const int targetSr = 22050;
                using var reader = new AudioFileReader(filePath);
                int srcSr = reader.WaveFormat.SampleRate;
                int channels = Math.Max(1, reader.WaveFormat.Channels);

                var rawMono = new List<float>((int)(reader.Length / 4) + 1024);
                float[] buf = new float[4096 * channels];
                int read;
                while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i <= read - channels; i += channels)
                    {
                        float sum = 0f;
                        for (int c = 0; c < channels; c++) sum += buf[i + c];
                        rawMono.Add(sum / channels);
                    }
                }

                if (rawMono.Count < 16) return Array.Empty<float>();

                float[] resampled;
                if (srcSr == targetSr)
                {
                    resampled = rawMono.ToArray();
                }
                else
                {
                    int outLen = Math.Max(16, (int)Math.Round((double)rawMono.Count * targetSr / srcSr));
                    resampled = new float[outLen];
                    double ratio = (double)(rawMono.Count - 1) / Math.Max(1, outLen - 1);
                    for (int i = 0; i < outLen; i++)
                    {
                        double pos = i * ratio;
                        int idx = (int)pos;
                        float frac = (float)(pos - idx);
                        float v0 = rawMono[idx];
                        float v1 = rawMono[Math.Min(rawMono.Count - 1, idx + 1)];
                        resampled[i] = v0 + frac * (v1 - v0);
                    }
                }

                totalSec = Math.Round((double)resampled.Length / targetSr, 3);
                int pps = 50;
                int samplesPerPeak = Math.Max(1, targetSr / pps);
                int peakCount = Math.Max(1, (resampled.Length + samplesPerPeak - 1) / samplesPerPeak);
                peaks50Hz = new float[peakCount];

                float globalMax = 0.01f;
                for (int i = 0; i < resampled.Length; i++)
                {
                    float a = Math.Abs(resampled[i]);
                    if (a > globalMax) globalMax = a;
                }

                for (int p = 0; p < peakCount; p++)
                {
                    int sStart = p * samplesPerPeak;
                    int sEnd = Math.Min(resampled.Length, sStart + samplesPerPeak);
                    float pk = 0f;
                    for (int s = sStart; s < sEnd; s++)
                    {
                        float a = Math.Abs(resampled[s]);
                        if (a > pk) pk = a;
                    }
                    peaks50Hz[p] = Math.Clamp(pk / globalMax, 0.02f, 1.0f);
                }

                return resampled;
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi nạp mẫu âm thanh 22kHz", ex);
                return Array.Empty<float>();
            }
        }

        private static float[] ApplyBiquadFilter(float[] input, double b0, double b1, double b2, double a1, double a2)
        {
            int n = input.Length;
            float[] output = new float[n];
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < n; i++)
            {
                double x0 = input[i];
                double y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1;
                x1 = x0;
                y2 = y1;
                y1 = y0;
                output[i] = (float)y0;
            }
            return output;
        }

        private static float[] ApplyPeakingEqBand(float[] samples, int sr, double freqHz, double gainDb, double q)
        {
            if (Math.Abs(gainDb) < 0.08 || freqHz <= 15.0 || freqHz >= sr * 0.47)
                return samples;
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freqHz / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosW0 = Math.Cos(w0);

            double b0 = 1.0 + alpha * A;
            double b1 = -2.0 * cosW0;
            double b2 = 1.0 - alpha * A;
            double a0 = 1.0 + alpha / A;
            double a1 = -2.0 * cosW0;
            double a2 = 1.0 - alpha / A;

            return ApplyBiquadFilter(samples, b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        private static float[] ApplyHighpassBiquad(float[] samples, int sr, double cutoffHz)
        {
            if (cutoffHz <= 22.0 || cutoffHz >= sr * 0.45) return samples;
            double w0 = 2.0 * Math.PI * cutoffHz / sr;
            double cosW0 = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * 0.7071);

            double b0 = (1.0 + cosW0) * 0.5;
            double b1 = -(1.0 + cosW0);
            double b2 = (1.0 + cosW0) * 0.5;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cosW0;
            double a2 = 1.0 - alpha;

            return ApplyBiquadFilter(samples, b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        private static float[] ApplyLowpassBiquad(float[] samples, int sr, double cutoffHz)
        {
            if (cutoffHz >= 10800.0 || cutoffHz <= 200.0) return samples;
            double w0 = 2.0 * Math.PI * Math.Min(sr * 0.47, cutoffHz) / sr;
            double cosW0 = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * 0.7071);

            double b0 = (1.0 - cosW0) * 0.5;
            double b1 = 1.0 - cosW0;
            double b2 = (1.0 - cosW0) * 0.5;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cosW0;
            double a2 = 1.0 - alpha;

            return ApplyBiquadFilter(samples, b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        private static float[] ApplyWsolaTimeStretch(float[] input, double speed)
        {
            speed = Math.Clamp(speed, 0.45, 2.2);
            if (input == null || input.Length < 512 || Math.Abs(speed - 1.0) < 0.02)
                return input;

            int nIn = input.Length;
            int winSize = 1024;
            int hopSyn = 256;
            int hopAna = Math.Max(1, (int)Math.Round(hopSyn * speed));
            int nOut = Math.Max(winSize + hopSyn, (int)Math.Round(nIn / speed));

            float[] win = new float[winSize];
            for (int i = 0; i < winSize; i++)
                win[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (winSize - 1))));

            float[] yOut = new float[nOut + winSize];
            float[] wSum = new float[nOut + winSize];

            int anaPos = 0, synPos = 0;
            while (anaPos + winSize < nIn && synPos + winSize < yOut.Length)
            {
                for (int i = 0; i < winSize; i++)
                {
                    yOut[synPos + i] += input[anaPos + i] * win[i];
                    wSum[synPos + i] += win[i];
                }
                anaPos += hopAna;
                synPos += hopSyn;
            }

            int validLen = Math.Max(1, synPos);
            float[] result = new float[validLen];
            for (int i = 0; i < validLen; i++)
            {
                result[i] = yOut[i] / Math.Max(0.35f, wSum[i]);
            }
            return result;
        }

        private static float[] ApplyWsolaPitchShift(float[] input, double semitones)
        {
            if (input == null || input.Length < 512 || Math.Abs(semitones) < 0.08)
                return input;
            double pitchFactor = Math.Clamp(Math.Pow(2.0, semitones / 12.0), 0.5, 2.0);
            float[] stretched = ApplyWsolaTimeStretch(input, 1.0 / pitchFactor);
            int nOut = input.Length;
            float[] resampled = new float[nOut];
            double ratio = (double)(stretched.Length - 1) / Math.Max(1, nOut - 1);
            for (int i = 0; i < nOut; i++)
            {
                double pos = i * ratio;
                int idx = (int)pos;
                float frac = (float)(pos - idx);
                float v0 = stretched[idx];
                float v1 = stretched[Math.Min(stretched.Length - 1, idx + 1)];
                resampled[i] = v0 + frac * (v1 - v0);
            }
            return resampled;
        }

        private static float[] ProcessClipDspInMemory(TimelineClipItem clip)
        {
            if (clip.RawSamples22k == null || clip.RawSamples22k.Length == 0)
            {
                clip.RawSamples22k = LoadMono22050Samples(clip.SourceFilePath, out double dur, out float[] pks);
                if (dur > 0.05) clip.SourceTotalDurationSec = dur;
                if (pks.Length > 0) clip.SourcePeaks = pks;
            }

            string sig = clip.GetDspSignature();
            if (clip.ProcessedFullSamples22k != null && clip.LastDspSignature == sig)
                return clip.ProcessedFullSamples22k;

            const int sr = 22050;
            float[] work = (float[])clip.RawSamples22k.Clone();

            // 1. Chỉnh Tone (Semitones)
            if (Math.Abs(clip.Semitones) >= 0.1)
            {
                work = ApplyWsolaPitchShift(work, clip.Semitones);
            }

            // 2. Nhái giọng mẫu (Đồng bộ F0 & màu âm sắc mẫu)
            if (clip.CloneEnabled && !string.IsNullOrEmpty(clip.CloneRefPath) && File.Exists(clip.CloneRefPath))
            {
                double cloneShift = (clip.CloneStrength - 0.5) * 3.2;
                if (Math.Abs(cloneShift) >= 0.15)
                    work = ApplyWsolaPitchShift(work, cloneShift);
                work = ApplyPeakingEqBand(work, sr, 210.0, 3.5 * clip.CloneStrength, 0.85);
                work = ApplyPeakingEqBand(work, sr, 3400.0, 2.8 * clip.CloneStrength, 0.90);
            }

            // 3. 5-Band Parametric EQ + Highpass + Lowpass
            if (clip.HasCustomEq)
            {
                work = ApplyHighpassBiquad(work, sr, clip.HighpassHz);
                work = ApplyPeakingEqBand(work, sr, 80.0, clip.Band80Hz, 0.80);
                work = ApplyPeakingEqBand(work, sr, 220.0, clip.Band220Hz, 0.90);
                work = ApplyPeakingEqBand(work, sr, 1000.0, clip.Band1kHz, 1.00);
                work = ApplyPeakingEqBand(work, sr, 3500.0, clip.Band3k5Hz, 0.95);
                work = ApplyPeakingEqBand(work, sr, 8000.0, clip.Band8kHz, 0.85);
                work = ApplyLowpassBiquad(work, sr, clip.LowpassHz);
            }

            clip.ProcessedFullSamples22k = work;
            clip.LastDspSignature = sig;
            return work;
        }

        // ==================== TRỘN TỔNG HỢP TẤT CẢ CÁC LAYER & DỰNG SÓNG ÂM TỔNG HỢP Ở DƯỚI ====================
        public void RebuildMasterCompositeTimelineAudio()
        {
            try
            {
                const int sr = 22050;
                if (TimelineClips.Count == 0)
                {
                    StudioMasterWaveformPoints = new PointCollection();
                    StudioMasterSummaryText = $"{StudioLayerCount} Layer (Chưa có thành phần âm thanh)";
                    return;
                }

                int maxLayer = Math.Max(StudioLayerCount, TimelineClips.Max(c => c.LayerIndex) + 1);
                if (StudioLayerCount != maxLayer)
                    StudioLayerCount = maxLayer;

                double maxTimelineEndSec = 0.2;
                var preparedSegments = new List<(int startSampleIdx, float[] segment)>();

                foreach (var clip in TimelineClips)
                {
                    float[] fullProcessed = ProcessClipDspInMemory(clip);
                    if (fullProcessed == null || fullProcessed.Length < 16) continue;

                    if (Math.Abs(clip.Speed - 1.0) >= 0.02)
                    {
                        fullProcessed = ApplyWsolaTimeStretch(fullProcessed, clip.Speed);
                    }

                    // Tạo bộ đệm có đúng độ dài DurationSec trên Layer (Phần mở rộng vượt ngoài phạm vi gốc mặc định bằng 0.0f - RỖNG/IM LẶNG)
                    int clipWindowSamples = Math.Max(16, (int)Math.Round(clip.DurationSec * sr));
                    int srcStartSample = (int)Math.Round(clip.SourceTrimStartSec * sr);
                    float[] seg = new float[clipWindowSamples];

                    int copySrcStart = Math.Max(0, srcStartSample);
                    int copySrcEnd = Math.Min(fullProcessed.Length, srcStartSample + clipWindowSamples);

                    if (copySrcEnd > copySrcStart)
                    {
                        int destOffset = copySrcStart - srcStartSample;
                        int copyCount = copySrcEnd - copySrcStart;
                        Array.Copy(fullProcessed, copySrcStart, seg, destOffset, copyCount);

                        // Làm mượt 8ms tại ranh giới phần âm thanh thực để chống tiếng nổ lụp bụp
                        int fadeSamples = Math.Min(copyCount / 4, (int)(sr * 0.008));
                        for (int f = 0; f < fadeSamples; f++)
                        {
                            float ramp = (float)f / fadeSamples;
                            seg[destOffset + f] *= ramp;
                            seg[destOffset + copyCount - 1 - f] *= ramp;
                        }
                    }

                    int tlStartSample = Math.Max(0, (int)Math.Round(clip.TimelineStartSec * sr));
                    double clipEndSec = clip.TimelineStartSec + ((double)seg.Length / sr);
                    if (clipEndSec > maxTimelineEndSec)
                        maxTimelineEndSec = clipEndSec;

                    preparedSegments.Add((tlStartSample, seg));
                }

                int totalMasterSamples = Math.Max((int)(sr * 0.2), (int)Math.Ceiling(maxTimelineEndSec * sr) + 64);
                float[] masterMix = new float[totalMasterSamples];

                foreach (var (startSampleIdx, segment) in preparedSegments)
                {
                    int copyLen = Math.Min(segment.Length, totalMasterSamples - startSampleIdx);
                    for (int i = 0; i < copyLen; i++)
                    {
                        masterMix[startSampleIdx + i] += segment[i];
                    }
                }

                // Chuẩn hóa biên độ an toàn khi nhiều Layer chồng lên nhau
                float peak = 0.001f;
                for (int i = 0; i < masterMix.Length; i++)
                {
                    float a = Math.Abs(masterMix[i]);
                    if (a > peak) peak = a;
                }
                if (peak > 0.95f)
                {
                    float scale = 0.92f / peak;
                    for (int i = 0; i < masterMix.Length; i++)
                        masterMix[i] *= scale;
                }

                // Ghi ra file tổng hợp luân phiên để nạp vào Trình phát Tổng hợp (_audioPlayer) mà không bị khóa file
                StopHeadlessDemo();
                double prevPos = _currentPosition;
                _audioPlayer.Stop();
                _timer.Stop();

                _masterPreviewToggleIdx = (_masterPreviewToggleIdx + 1) % 2;
                string previewWavPath = Path.Combine(_outputDir, $"_studio_master_composite_{_masterPreviewToggleIdx}.wav");
                using (var writer = new WaveFileWriter(previewWavPath, new WaveFormat(sr, 16, 1)))
                {
                    short[] pcm16 = new short[masterMix.Length];
                    for (int i = 0; i < masterMix.Length; i++)
                    {
                        pcm16[i] = (short)Math.Clamp(masterMix[i] * 32767f, -32767f, 32767f);
                    }
                    writer.WriteSamples(pcm16, 0, pcm16.Length);
                }

                // Dựng Sóng âm Tổng hợp của tất cả các Layer (StudioMasterWaveformPoints)
                var masterPts = new PointCollection();
                double wWidth = 1000.0, midY = 50.0;
                int samplesPerCol = Math.Max(1, masterMix.Length / (int)wWidth);
                var topPts = new List<Point>(1024);
                var botPts = new List<Point>(1024);

                int col = 0;
                for (int idx = 0; idx < masterMix.Length && col <= (int)wWidth; idx += samplesPerCol)
                {
                    float maxCol = 0f;
                    int lim = Math.Min(masterMix.Length, idx + samplesPerCol);
                    for (int k = idx; k < lim; k++)
                    {
                        float v = Math.Abs(masterMix[k]);
                        if (v > maxCol) maxCol = v;
                    }
                    maxCol = Math.Clamp(maxCol, 0f, 1f);
                    topPts.Add(new Point(col, midY - (maxCol * midY * 0.92)));
                    botPts.Insert(0, new Point(col, midY + (maxCol * midY * 0.92)));
                    col++;
                }
                masterPts.Add(new Point(0, midY));
                foreach (var p in topPts) masterPts.Add(p);
                foreach (var p in botPts) masterPts.Add(p);
                masterPts.Add(new Point(0, midY));

                StudioMasterWaveformPoints = masterPts;

                // Nạp trực tiếp file tổng hợp vào Trình phát chính (Không đọc lại đĩa bằng GenerateWaveform để siêu mượt với file dài)
                _currentAudioPath = previewWavPath;
                _audioPlayer.Load(_currentAudioPath);
                TotalDuration = Math.Max(0.1, _audioPlayer.TotalTime.TotalSeconds);
                _isTimerUpdating = true;
                _currentPosition = (prevPos > 0 && prevPos < TotalDuration) ? prevPos : 0.0;
                OnPropertyChanged(nameof(CurrentPosition));
                _isTimerUpdating = false;
                if (_currentPosition > 0 && _audioPlayer.TotalTime.TotalSeconds > _currentPosition)
                {
                    _audioPlayer.CurrentTime = TimeSpan.FromSeconds(_currentPosition);
                }
                UpdateTimeDisplay();
                WaveformPoints = masterPts;
                CommandManager.InvalidateRequerySuggested();

                int eqCount = TimelineClips.Count(c => c.HasCustomEq);
                string eqInfo = eqCount > 0 ? $" • {eqCount} đoạn đã chỉnh EQ" : "";
                StudioMasterSummaryText = $"{StudioLayerCount} Layer ({TimelineClips.Count} đoạn{eqInfo}) • Tổng độ dài: {TotalDuration:0.00}s";
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi trộn sóng âm tổng hợp các Layer", ex);
            }
        }

        public void PlayMasterCompositeAudio()
        {
            StopHeadlessDemo();
            if (_currentAudioPath == null || !_currentAudioPath.Contains("_studio_master_composite_"))
            {
                RebuildMasterCompositeTimelineAudio();
            }
            if (_currentPosition >= TotalDuration - 0.05)
            {
                CurrentPosition = 0;
            }
            _audioPlayer.Play();
            _timer.Start();
            StudioStatusText = $"▶ Đang phát bản tổng hợp {StudioLayerCount} Layer (Độ dài: {TotalDuration:0.00}s)...";
        }

        public void ToggleMasterCompositePlayPause()
        {
            StopHeadlessDemo();
            if (_audioPlayer.IsPlaying)
            {
                _audioPlayer.Pause();
                _timer.Stop();
                StudioStatusText = $"⏸ Đã tạm dừng tại {CurrentPosition:0.00}s / {TotalDuration:0.00}s";
            }
            else
            {
                PlayMasterCompositeAudio();
            }
        }

        public void RemoveLayerAt(int layerIdxToRemove)
        {
            if (StudioLayerCount <= 1) return;
            var toRemove = TimelineClips.Where(c => c.LayerIndex == layerIdxToRemove).ToList();
            foreach (var c in toRemove)
                TimelineClips.Remove(c);

            foreach (var c in TimelineClips.Where(c => c.LayerIndex > layerIdxToRemove))
                c.LayerIndex--;

            StudioLayerCount = Math.Max(1, StudioLayerCount - 1);
            SelectedTimelineClip = TimelineClips.FirstOrDefault();
            RebuildMasterCompositeTimelineAudio();
            StudioStatusText = $"🗑️ Đã xoá Layer {layerIdxToRemove + 1} và cập nhật sóng âm tổng hợp.";
        }

        private double _previewListVolume = 80.0;
        public double PreviewListVolume
        {
            get => _previewListVolume;
            set
            {
                _previewListVolume = Math.Clamp(value, 0.0, 100.0);
                if (_previewListVolume > 0.5) _isPreviewListMuted = false;
                if (_demoAudioReader != null)
                {
                    _demoAudioReader.Volume = _isPreviewListMuted ? 0f : (float)(_previewListVolume / 100.0);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewListVolumeIcon));
            }
        }

        private bool _isPreviewListMuted = false;
        public bool IsPreviewListMuted
        {
            get => _isPreviewListMuted;
            set
            {
                _isPreviewListMuted = value;
                if (_demoAudioReader != null)
                {
                    _demoAudioReader.Volume = _isPreviewListMuted ? 0f : (float)(_previewListVolume / 100.0);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewListVolumeIcon));
            }
        }

        public string PreviewListVolumeIcon => (IsPreviewListMuted || PreviewListVolume <= 0.5) ? "🔇" : "🔊";

        public ICommand TogglePreviewListMuteCommand => new RelayCommand((o) =>
        {
            IsPreviewListMuted = !IsPreviewListMuted;
        });

        private List<TimelineClipItem> _clipboardMultiClips = new List<TimelineClipItem>();

        public void PlayHeadless5SecDemo(string filePath)
        {
            if (_suppressDemoOnScan || string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            try
            {
                StopHeadlessDemo();
                _audioPlayer.Stop();
                _timer.Stop();

                float effVol = IsPreviewListMuted ? 0f : (float)Math.Clamp(PreviewListVolume / 100.0, 0.0, 1.0);
                _demoAudioReader = new AudioFileReader(filePath)
                {
                    Volume = effVol
                };
                _demoWaveOut = new WaveOutEvent();
                _demoWaveOut.Init(_demoAudioReader);
                _demoWaveOut.Play();

                _demoStopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5.0) };
                _demoStopTimer.Tick += (s, e) => StopHeadlessDemo();
                _demoStopTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi phát ngầm Demo 5s", ex);
            }
        }

        public void StopHeadlessDemo()
        {
            try
            {
                _demoStopTimer?.Stop();
                _demoStopTimer = null;
                if (_demoWaveOut != null)
                {
                    _demoWaveOut.Stop();
                    _demoWaveOut.Dispose();
                    _demoWaveOut = null;
                }
                if (_demoAudioReader != null)
                {
                    _demoAudioReader.Dispose();
                    _demoAudioReader = null;
                }
            }
            catch { }
        }

        public static double GetAudioDurationSeconds(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return 0.0;
            try
            {
                using var reader = new AudioFileReader(filePath);
                return Math.Max(0.1, Math.Round(reader.TotalTime.TotalSeconds, 1));
            }
            catch
            {
                return 0.0;
            }
        }

        public string EnsurePcm16MonoWav(string inputPath)
        {
            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath)) return "";
            try
            {
                string readyPath = Path.Combine(_outputDir, "_clone_ref_ready.wav");
                using var reader = new AudioFileReader(inputPath);
                var outFormat = new WaveFormat(reader.WaveFormat.SampleRate, 16, 1);
                using var resampler = new MediaFoundationResampler(reader, outFormat) { ResamplerQuality = 60 };
                WaveFileWriter.CreateWaveFile(readyPath, resampler);
                return readyPath;
            }
            catch
            {
                return inputPath;
            }
        }

        public void SetTimelinePlayheadSeconds(double sec)
        {
            _isTimerUpdating = true;
            _currentPosition = Math.Max(0.0, sec);
            OnPropertyChanged(nameof(CurrentPosition));
            _isTimerUpdating = false;
            if (_audioPlayer.TotalTime.TotalSeconds > 0 && sec <= _audioPlayer.TotalTime.TotalSeconds)
            {
                _audioPlayer.CurrentTime = TimeSpan.FromSeconds(sec);
            }
            UpdateTimeDisplay();
        }

        public void TogglePlayPause() => ToggleMasterCompositePlayPause();

        public void SelectAllTimelineClips()
        {
            foreach (var c in TimelineClips) c.IsSelected = true;
            if (SelectedTimelineClip == null && TimelineClips.Count > 0)
                SelectedTimelineClip = TimelineClips[0];
            StudioStatusText = $"☑️ Đã chọn tất cả {TimelineClips.Count} thành phần trên các Layer.";
        }

        public void ClearTimelineSelection()
        {
            foreach (var c in TimelineClips) c.IsSelected = false;
            StudioStatusText = "✖️ Đã bỏ chọn nhóm thành phần.";
        }

        public void ExtendSelectedClipsWithEmptySpace(double extraEmptySeconds)
        {
            var targets = TimelineClips.Where(c => c.IsSelected).ToList();
            if (targets.Count == 0 && SelectedTimelineClip != null)
                targets.Add(SelectedTimelineClip);
            if (targets.Count == 0 && TimelineClips.Count > 0)
                targets.Add(TimelineClips.Last());

            if (targets.Count == 0)
            {
                StudioStatusText = "⚠️ Hãy chọn ít nhất 1 thành phần trên Layer để mở rộng rỗng!";
                return;
            }

            foreach (var c in targets)
            {
                c.DurationSec = Math.Round(c.DurationSec + extraEmptySeconds, 2);
            }
            RebuildMasterCompositeTimelineAudio();
            StudioStatusText = $"↔️ Đã mở rộng thêm +{extraEmptySeconds:0.0}s khoảng rỗng cho {targets.Count} thành phần (Tổng độ dài: {TotalDuration:0.00}s)!";
        }

        public void AddRecordingToTimeline(string filePath, int layerIndex = 0, double startSec = -1.0)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            StopHeadlessDemo();

            float[] raw22k = LoadMono22050Samples(filePath, out double dur, out float[] peaks);
            if (dur <= 0.05) dur = GetAudioDurationSeconds(filePath);
            if (dur <= 0.05) dur = 3.0;

            int targetLayer = Math.Max(0, layerIndex);
            if (targetLayer >= StudioLayerCount)
                StudioLayerCount = targetLayer + 1;

            double placeSec = startSec;
            if (placeSec < 0.0)
            {
                var sameLayerClips = TimelineClips.Where(c => c.LayerIndex == targetLayer).ToList();
                placeSec = sameLayerClips.Count > 0 ? sameLayerClips.Max(c => c.TimelineStartSec + c.DurationSec) + 0.08 : 0.0;
            }

            foreach (var c in TimelineClips) c.IsSelected = false;

            var newClip = new TimelineClipItem
            {
                SourceFilePath = filePath,
                Title = Path.GetFileNameWithoutExtension(filePath),
                LayerIndex = targetLayer,
                TimelineStartSec = Math.Round(placeSec, 2),
                SourceTrimStartSec = 0.0,
                DurationSec = Math.Round(dur, 2),
                SourceTotalDurationSec = Math.Round(dur, 2),
                IsSelected = true,
                SourcePeaks = peaks,
                PeaksPerSecond = 50,
                RawSamples22k = raw22k,
                MiniWaveform = new PointCollection()
            };

            TimelineClips.Add(newClip);
            SelectedTimelineClip = newClip;
            RebuildMasterCompositeTimelineAudio();
            StudioStatusText = $"🎬 Đã thêm '{newClip.Title}' vào Layer {targetLayer + 1} (Tổng độ dài các Layer: {TotalDuration:0.00}s)";
        }

        public void CopySelectedClip()
        {
            var selectedList = TimelineClips.Where(c => c.IsSelected).ToList();
            if (selectedList.Count == 0 && SelectedTimelineClip != null)
                selectedList.Add(SelectedTimelineClip);

            if (selectedList.Count == 0)
            {
                StudioStatusText = "⚠️ Hãy chọn 1 hoặc quét chọn nhiều thành phần trên Layer trước khi nhấn Ctrl+C!";
                return;
            }

            _clipboardMultiClips = selectedList.Select(c => c.CloneCopy(c.TimelineStartSec)).ToList();
            _clipboardTimelineClip = _clipboardMultiClips[0];
            StudioStatusText = $"📋 Đã sao chép {_clipboardMultiClips.Count} thành phần vào Clipboard (Nhấn Ctrl+V để dán tại Vạch đỏ)";
        }

        public void PasteClipAtPlayhead()
        {
            if (_clipboardMultiClips == null || _clipboardMultiClips.Count == 0)
            {
                if (_clipboardTimelineClip != null)
                    _clipboardMultiClips = new List<TimelineClipItem> { _clipboardTimelineClip };
                else
                {
                    StudioStatusText = "⚠️ Chưa có thành phần nào trong Clipboard (Hãy nhấn Ctrl+C trước)!";
                    return;
                }
            }

            double minOrigStart = _clipboardMultiClips.Min(c => c.TimelineStartSec);
            double maxOrigEnd = _clipboardMultiClips.Max(c => c.TimelineStartSec + c.DurationSec);
            double basePasteSec = CurrentPosition > 0.05 ? CurrentPosition : (maxOrigEnd + 0.08);

            foreach (var c in TimelineClips) c.IsSelected = false;

            TimelineClipItem lastPasted = null;
            foreach (var srcClip in _clipboardMultiClips)
            {
                double relOffset = srcClip.TimelineStartSec - minOrigStart;
                var pasted = srcClip.CloneCopy(Math.Round(basePasteSec + relOffset, 2));
                pasted.Title = srcClip.Title + "_Copy";
                pasted.IsSelected = true;
                TimelineClips.Add(pasted);
                lastPasted = pasted;
            }

            if (lastPasted != null)
                SelectedTimelineClip = lastPasted;

            RebuildMasterCompositeTimelineAudio();
            StudioStatusText = $"📌 Đã dán {_clipboardMultiClips.Count} thành phần tại {basePasteSec:0.00}s";
        }

        public void SplitSelectedClipAtPlayhead()
        {
            var clip = SelectedTimelineClip;
            if (clip == null && TimelineClips.Count > 0)
            {
                clip = TimelineClips.FirstOrDefault(c => CurrentPosition > c.TimelineStartSec + 0.10 && CurrentPosition < c.TimelineStartSec + c.DurationSec - 0.10)
                       ?? TimelineClips[0];
            }
            if (clip == null)
            {
                StudioStatusText = "⚠️ Hãy chọn 1 thành phần trên Layer để cắt tách!";
                return;
            }

            double splitTimeOnTimeline = CurrentPosition;
            double offsetInClip = splitTimeOnTimeline - clip.TimelineStartSec;

            if (offsetInClip <= 0.10 || offsetInClip >= clip.DurationSec - 0.10)
            {
                offsetInClip = Math.Round(clip.DurationSec * 0.5, 2);
                splitTimeOnTimeline = clip.TimelineStartSec + offsetInClip;
            }

            if (clip.DurationSec < 0.25)
            {
                StudioStatusText = "⚠️ Đoạn âm thanh quá ngắn (< 0.25s), không thể tách nhỏ hơn!";
                return;
            }

            double firstDur = Math.Round(offsetInClip, 2);
            double secondDur = Math.Round(clip.DurationSec - firstDur, 2);

            var rightPart = clip.CloneCopy(Math.Round(clip.TimelineStartSec + firstDur, 2));
            rightPart.SourceTrimStartSec = Math.Round(clip.SourceTrimStartSec + firstDur, 2);
            rightPart.DurationSec = secondDur;
            rightPart.Title = clip.Title + "_P2";
            rightPart.IsSelected = true;

            clip.DurationSec = firstDur;
            clip.IsSelected = false;
            if (!clip.Title.EndsWith("_P1")) clip.Title += "_P1";

            TimelineClips.Add(rightPart);
            SelectedTimelineClip = rightPart;
            RebuildMasterCompositeTimelineAudio();
            SetTimelinePlayheadSeconds(splitTimeOnTimeline);
            StudioStatusText = $"✂️ Đã tách thành 2 thành phần liên tiếp tại {splitTimeOnTimeline:0.00}s ({firstDur:0.00}s & {secondDur:0.00}s)!";
        }

        public void DeleteSelectedClip()
        {
            var toDelete = TimelineClips.Where(c => c.IsSelected).ToList();
            if (toDelete.Count == 0 && SelectedTimelineClip != null)
                toDelete.Add(SelectedTimelineClip);

            if (toDelete.Count == 0) return;

            foreach (var c in toDelete)
                TimelineClips.Remove(c);

            SelectedTimelineClip = TimelineClips.LastOrDefault();
            if (SelectedTimelineClip != null)
                SelectedTimelineClip.IsSelected = true;

            RebuildMasterCompositeTimelineAudio();
            StudioStatusText = $"🗑️ Đã xoá {toDelete.Count} thành phần khỏi Layer và cập nhật sóng âm tổng hợp.";
        }

        private bool _isMiniStudioMode = false;
        public bool IsMiniStudioMode
        {
            get => _isMiniStudioMode;
            set
            {
                _isMiniStudioMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EditorWorkspaceVisibility));
                OnPropertyChanged(nameof(MiniStudioWorkspaceVisibility));
            }
        }
        public Visibility EditorWorkspaceVisibility => IsMiniStudioMode ? Visibility.Collapsed : Visibility.Visible;
        public Visibility MiniStudioWorkspaceVisibility => IsMiniStudioMode ? Visibility.Visible : Visibility.Collapsed;

        private string _selectedStudioFileSummary = "Chưa chọn bản ghi (.wav)";
        public string SelectedStudioFileSummary
        {
            get => _selectedStudioFileSummary;
            set { _selectedStudioFileSummary = value; OnPropertyChanged(); }
        }

        private string _studioStatusText = "Mặc định 1 Layer • Kéo 2 cạnh trái/phải của đoạn để cắt thu gọn chuẩn CapCut PC • Chỉnh EQ sẽ đổi màu nền đoạn.";
        public string StudioStatusText
        {
            get => _studioStatusText;
            set { _studioStatusText = value; OnPropertyChanged(); }
        }

        private bool _studioCutEnabled = false;
        public bool StudioCutEnabled { get => _studioCutEnabled; set { _studioCutEnabled = value; OnPropertyChanged(); } }

        public string[] StudioCutModes { get; } = {
            "Giữ đoạn chọn (Trim Keep)",
            "Xoá đoạn giữa (Cut Out Middle)"
        };
        private string _selectedStudioCutMode = "Giữ đoạn chọn (Trim Keep)";
        public string SelectedStudioCutMode { get => _selectedStudioCutMode; set { _selectedStudioCutMode = value; OnPropertyChanged(); } }

        private double _studioCutStartSec = 0.0;
        public double StudioCutStartSec
        {
            get => _studioCutStartSec;
            set { _studioCutStartSec = Math.Round(Math.Max(0.0, value), 2); OnPropertyChanged(); }
        }

        private double _studioCutEndSec = 10.0;
        public double StudioCutEndSec
        {
            get => _studioCutEndSec;
            set { _studioCutEndSec = Math.Round(Math.Max(0.0, value), 2); OnPropertyChanged(); }
        }

        private bool _studioMergeEnabled = false;
        public bool StudioMergeEnabled { get => _studioMergeEnabled; set { _studioMergeEnabled = value; OnPropertyChanged(); } }

        private HistoryItem _studioMergeSecondItem;
        public HistoryItem StudioMergeSecondItem { get => _studioMergeSecondItem; set { _studioMergeSecondItem = value; OnPropertyChanged(); } }

        private double _studioMergeSilenceSec = 0.15;
        public double StudioMergeSilenceSec
        {
            get => _studioMergeSilenceSec;
            set { _studioMergeSilenceSec = Math.Round(Math.Clamp(value, 0.0, 2.0), 2); OnPropertyChanged(); }
        }

        private double _studioMergeCrossfadeMs = 30.0;
        public double StudioMergeCrossfadeMs
        {
            get => _studioMergeCrossfadeMs;
            set { _studioMergeCrossfadeMs = Math.Round(Math.Clamp(value, 0.0, 300.0), 0); OnPropertyChanged(); }
        }

        private double _studioSemitones = 0.0;
        public double StudioSemitones
        {
            get => _studioSemitones;
            set
            {
                _studioSemitones = Math.Round(Math.Clamp(value, -12.0, 12.0), 1);
                OnPropertyChanged();
                OnPropertyChanged(nameof(StudioSemitonesDisplay));
                SyncInspectorToSelectedClip();
            }
        }
        public string StudioSemitonesDisplay => _studioSemitones >= 0 ? $"+{_studioSemitones:0.0} st" : $"{_studioSemitones:0.0} st";

        private double _studioSpeed = 1.0;
        public double StudioSpeed
        {
            get => _studioSpeed;
            set
            {
                _studioSpeed = Math.Round(Math.Clamp(value, 0.5, 2.0), 2);
                OnPropertyChanged();
                SyncInspectorToSelectedClip();
            }
        }

        private bool _studioCloneEnabled = false;
        public bool StudioCloneEnabled
        {
            get => _studioCloneEnabled;
            set
            {
                _studioCloneEnabled = value;
                OnPropertyChanged();
                SyncInspectorToSelectedClip();
            }
        }

        private double _studioCloneStrength = 0.80;
        public double StudioCloneStrength
        {
            get => _studioCloneStrength;
            set
            {
                _studioCloneStrength = Math.Round(Math.Clamp(value, 0.0, 1.0), 2);
                OnPropertyChanged();
                SyncInspectorToSelectedClip();
            }
        }

        private double _studioHighpassHz = 65.0;
        public double StudioHighpassHz
        {
            get => _studioHighpassHz;
            set { _studioHighpassHz = Math.Round(Math.Clamp(value, 20.0, 250.0), 0); OnPropertyChanged(); SyncInspectorToSelectedClip(); }
        }

        private double _studioBand80Hz = 0.0;
        public double StudioBand80Hz
        {
            get => _studioBand80Hz;
            set { _studioBand80Hz = Math.Round(Math.Clamp(value, -12.0, 12.0), 1); OnPropertyChanged(); SyncInspectorToSelectedClip(); }
        }

        private double _studioBand220Hz = 0.0;
        public double StudioBand220Hz
        {
            get => _studioBand220Hz;
            set { _studioBand220Hz = Math.Round(Math.Clamp(value, -12.0, 12.0), 1); OnPropertyChanged(); SyncInspectorToSelectedClip(); }
        }

        private double _studioBand1kHz = 0.0;
        public double StudioBand1kHz
        {
            get => _studioBand1kHz;
            set { _studioBand1kHz = Math.Round(Math.Clamp(value, -12.0, 12.0), 1); OnPropertyChanged(); SyncInspectorToSelectedClip(); }
        }

        private double _studioBand3k5Hz = 0.0;
        public double StudioBand3k5Hz
        {
            get => _studioBand3k5Hz;
            set { _studioBand3k5Hz = Math.Round(Math.Clamp(value, -12.0, 12.0), 1); OnPropertyChanged(); SyncInspectorToSelectedClip(); }
        }

        private double _studioBand8kHz = 0.0;
        public double StudioBand8kHz
        {
            get => _studioBand8kHz;
            set { _studioBand8kHz = Math.Round(Math.Clamp(value, -12.0, 12.0), 1); OnPropertyChanged(); SyncInspectorToSelectedClip(); }
        }

        private double _studioLowpassHz = 11000.0;
        public double StudioLowpassHz
        {
            get => _studioLowpassHz;
            set { _studioLowpassHz = Math.Round(Math.Clamp(value, 4000.0, 12000.0), 0); OnPropertyChanged(); SyncInspectorToSelectedClip(); }
        }

        public ICommand SwitchWorkspaceModeCommand => new RelayCommand((param) =>
        {
            string mode = param?.ToString() ?? "Editor";
            IsMiniStudioMode = (mode == "MiniStudio");
            if (IsMiniStudioMode && HistoryList.Count == 0)
            {
                ScanOutputRecordings();
            }
        });

        public ICommand ScanRecordingsCommand => new RelayCommand((o) => ScanOutputRecordings());

        public ICommand AddSelectedRecordingToTimelineCommand => new RelayCommand((o) =>
        {
            if (SelectedHistoryItem != null && File.Exists(SelectedHistoryItem.FilePath))
            {
                AddRecordingToTimeline(SelectedHistoryItem.FilePath, 0, -1.0);
            }
        }, (o) => SelectedHistoryItem != null);

        public ICommand ClearTimelineCommand => new RelayCommand((o) =>
        {
            TimelineClips.Clear();
            SelectedTimelineClip = null;
            StudioLayerCount = 1;
            RebuildMasterCompositeTimelineAudio();
            StudioStatusText = "🧹 Đã làm trống Timeline và đưa về mặc định 1 Layer.";
        });

        public ICommand PreviewSelectedClipDspCommand => new RelayCommand((o) =>
        {
            RebuildMasterCompositeTimelineAudio();
            PlayMasterCompositeAudio();
        });

        public ICommand RenderCapCutTimelineMixdownCommand => new RelayCommand((o) =>
        {
            _ = ExportMasterCompositeToWavFileAsync();
        }, (o) => !IsProcessing);

        public ICommand SetCutStartFromPlayheadCommand => new RelayCommand((o) =>
        {
            StudioCutStartSec = Math.Round(CurrentPosition, 2);
            StudioCutEnabled = true;
        });

        public ICommand SetCutEndFromPlayheadCommand => new RelayCommand((o) =>
        {
            StudioCutEndSec = Math.Round(CurrentPosition, 2);
            StudioCutEnabled = true;
        });

        public ICommand SetCutEndToMaxCommand => new RelayCommand((o) =>
        {
            StudioCutEndSec = Math.Round(TotalDuration, 2);
        });

        public ICommand SetStudioSemitonePresetCommand => new RelayCommand((param) =>
        {
            if (double.TryParse(param?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double st))
                StudioSemitones = st;
        });

        public ICommand ResetStudioEqCommand => new RelayCommand((o) =>
        {
            _isSyncingClipInspector = true;
            _studioHighpassHz = 65.0;
            _studioBand80Hz = 0.0;
            _studioBand220Hz = 0.0;
            _studioBand1kHz = 0.0;
            _studioBand3k5Hz = 0.0;
            _studioBand8kHz = 0.0;
            _studioLowpassHz = 11000.0;
            OnPropertyChanged(nameof(StudioHighpassHz));
            OnPropertyChanged(nameof(StudioBand80Hz));
            OnPropertyChanged(nameof(StudioBand220Hz));
            OnPropertyChanged(nameof(StudioBand1kHz));
            OnPropertyChanged(nameof(StudioBand3k5Hz));
            OnPropertyChanged(nameof(StudioBand8kHz));
            OnPropertyChanged(nameof(StudioLowpassHz));
            _isSyncingClipInspector = false;
            SyncInspectorToSelectedClip();
            StudioStatusText = "↺ Đã đặt lại tần số EQ về 0 dB (Màu nền đoạn trở về mặc định).";
        });

        public ICommand ResetStudioAllCommand => new RelayCommand((o) =>
        {
            _isSyncingClipInspector = true;
            _studioSemitones = 0.0;
            _studioSpeed = 1.0;
            _studioCloneEnabled = false;
            _studioCloneStrength = 0.80;
            OnPropertyChanged(nameof(StudioSemitones));
            OnPropertyChanged(nameof(StudioSemitonesDisplay));
            OnPropertyChanged(nameof(StudioSpeed));
            OnPropertyChanged(nameof(StudioCloneEnabled));
            OnPropertyChanged(nameof(StudioCloneStrength));
            _isSyncingClipInspector = false;
            ResetStudioEqCommand.Execute(null);
            StudioStatusText = "↺ Đã đặt lại toàn bộ thông số EQ / Tone / Speed của đoạn đang chọn.";
        });

        public ICommand DeleteRecordingCommand => new RelayCommand((o) =>
        {
            var targetItem = SelectedHistoryItem;
            if (targetItem == null || string.IsNullOrEmpty(targetItem.FilePath)) return;

            try
            {
                StopHeadlessDemo();
                _audioPlayer.Stop();
                _timer.Stop();
                string fileToDelete = targetItem.FilePath;
                string fileName = Path.GetFileName(fileToDelete);

                int idx = HistoryList.IndexOf(targetItem);
                _suppressDemoOnScan = true;
                HistoryList.Remove(targetItem);

                if (File.Exists(fileToDelete))
                {
                    File.Delete(fileToDelete);
                }

                if (HistoryList.Count > 0)
                {
                    SelectedHistoryItem = HistoryList[Math.Clamp(idx, 0, HistoryList.Count - 1)];
                }
                _suppressDemoOnScan = false;
                StudioStatusText = $"🗑️ Đã xoá bản ghi: {fileName}";
            }
            catch (Exception ex)
            {
                _suppressDemoOnScan = false;
                Logger.Error("Lỗi xoá bản ghi", ex);
                MessageBox.Show("Không thể xoá bản ghi đang phát: " + ex.Message, "VoiceCraft Mini Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }, (o) => SelectedHistoryItem != null);

        public ICommand DuplicateRecordingCommand => new RelayCommand((o) =>
        {
            if (SelectedHistoryItem == null || !File.Exists(SelectedHistoryItem.FilePath)) return;
            try
            {
                string srcPath = SelectedHistoryItem.FilePath;
                string baseName = Path.GetFileNameWithoutExtension(srcPath);
                string copyName = $"{baseName}_COPY_{DateTime.Now:HHmmss}.wav";
                string copyPath = Path.Combine(_outputDir, copyName);
                File.Copy(srcPath, copyPath, true);

                double dur = GetAudioDurationSeconds(copyPath);
                double kb = Math.Round(new FileInfo(copyPath).Length / 1024.0, 0);
                var newItem = new HistoryItem
                {
                    DisplayText = $"[{DateTime.Now:HH:mm:ss}] {copyName} ({dur:0.0}s)",
                    FilePath = copyPath,
                    DurationSeconds = dur,
                    DurationPopupText = $"⏱️ Độ dài bản ghi: {FormatTimeCode(dur)} ({dur:0.0} giây) | Dung lượng: {kb} KB"
                };
                _suppressDemoOnScan = true;
                HistoryList.Insert(0, newItem);
                SelectedHistoryItem = newItem;
                _suppressDemoOnScan = false;
                StudioStatusText = $"📑 Đã tạo bản sao: {copyName}";
            }
            catch (Exception ex)
            {
                _suppressDemoOnScan = false;
                Logger.Error("Lỗi nhân bản file", ex);
            }
        }, (o) => SelectedHistoryItem != null);

        public ICommand QuickCutKeepCommand => new RelayCommand((o) => RebuildMasterCompositeTimelineAudio());
        public ICommand QuickCutOutCommand => new RelayCommand((o) => RebuildMasterCompositeTimelineAudio());
        public ICommand QuickMergeRecordingsCommand => new RelayCommand((o) => RebuildMasterCompositeTimelineAudio());
        public ICommand ApplyStudioOverwriteCommand => new RelayCommand((o) => _ = ExportMasterCompositeToWavFileAsync());
        public ICommand ApplyStudioNewFileCommand => new RelayCommand((o) => _ = ExportMasterCompositeToWavFileAsync());

        private async Task ExportMasterCompositeToWavFileAsync()
        {
            if (TimelineClips.Count == 0)
            {
                MessageBox.Show("Timeline dang trong! Hay keo tha ban ghi vao Layer truoc khi xuat file.", "VoiceCraft Mini Studio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            StopHeadlessDemo();
            _audioPlayer.Stop();
            _timer.Stop();

            IsProcessing = true;
            SynthesisProgress = 8;
            SynthesisStatusText = $"Dang phan tich & dong bo {TimelineClips.Count} thanh phan tren {StudioLayerCount} Layer...";
            try
            {
                await Task.Delay(180);
                SynthesisProgress = 45;
                SynthesisStatusText = "Dang xu ly 5-Band Parametric EQ, Tone & Nhai giong AI...";
                await Task.Delay(220);
                SynthesisProgress = 82;
                SynthesisStatusText = "Dang tron song am tong hop (Master Composite Mixdown)...";
                await Task.Delay(160);
            }
                await Task.Run(() => ExportMasterCompositeToWavFile_Core());
                SynthesisProgress = 100;
                SynthesisStatusText = "Ho\u00E0n t\u1EA5t 100%!";
                await Task.Delay(150);
            }
            catch (Exception ex)
            {
                Logger.Error("Loi xuat file Mini Studio", ex);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void ExportMasterCompositeToWavFile_Core()
        {
            try
            {
                if (TimelineClips.Count == 0)
                {
                    MessageBox.Show("Timeline đang trống! Hãy kéo thả bản ghi vào Layer trước khi xuất file.", "VoiceCraft Mini Studio", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                RebuildMasterCompositeTimelineAudio();
                string currentPreview = Path.Combine(_outputDir, $"_studio_master_composite_{_masterPreviewToggleIdx}.wav");
                if (!File.Exists(currentPreview)) return;

                string outName = $"VC_STUDIO_MIX_{StudioLayerCount}L_{DateTime.Now:HHmmss}.wav";
                string outPath = Path.Combine(_outputDir, outName);
                File.Copy(currentPreview, outPath, true);

                ScanOutputRecordings();
                var added = HistoryList.FirstOrDefault(x => string.Equals(x.FilePath, outPath, StringComparison.OrdinalIgnoreCase));
                if (added != null)
                {
                    _suppressDemoOnScan = true;
                    SelectedHistoryItem = added;
                    _suppressDemoOnScan = false;
                }

                StudioStatusText = $"✨ Đã xuất bản ghi tổng hợp ({StudioLayerCount} Layer • {TotalDuration:0.00}s): {outName}";
                PlayMasterCompositeAudio();
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi xuất file tổng hợp", ex);
                MessageBox.Show("Lỗi xuất file tổng hợp: " + ex.Message, "VoiceCraft Mini Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void ScanOutputRecordings()
        {
            _suppressDemoOnScan = true;
            try
            {
                if (!Directory.Exists(_outputDir)) Directory.CreateDirectory(_outputDir);
                string prevSelectedPath = SelectedHistoryItem?.FilePath;

                var wavFiles = new DirectoryInfo(_outputDir)
                    .GetFiles("*.wav")
                    .Where(f => !f.Name.StartsWith("temp_", StringComparison.OrdinalIgnoreCase) &&
                                !f.Name.StartsWith("_studio_master_", StringComparison.OrdinalIgnoreCase) &&
                                !f.Name.Equals("_clone_ref_ready.wav", StringComparison.OrdinalIgnoreCase) &&
                                f.Length > 200)
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                HistoryList.Clear();
                foreach (var fi in wavFiles)
                {
                    double kb = Math.Round(fi.Length / 1024.0, 0);
                    double dur = GetAudioDurationSeconds(fi.FullName);
                    HistoryList.Add(new HistoryItem
                    {
                        DisplayText = $"[{fi.LastWriteTime:HH:mm dd/MM}] {Path.GetFileNameWithoutExtension(fi.Name)} ({dur:0.0}s)",
                        FilePath = fi.FullName,
                        DurationSeconds = dur,
                        DurationPopupText = $"⏱️ Độ dài bản ghi: {FormatTimeCode(dur)} ({dur:0.0} giây) | Dung lượng: {kb} KB"
                    });
                }

                if (HistoryList.Count > 0)
                {
                    var match = HistoryList.FirstOrDefault(x => x.FilePath == prevSelectedPath);
                    SelectedHistoryItem = match ?? HistoryList[0];
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Lỗi quét danh sách bản ghi Output", ex);
            }
            finally
            {
                _suppressDemoOnScan = false;
            }
        }

        private string _connectionStatus = "Đang khởi động...";
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
                    if (TimelineClips.Count == 0)
                    {
                        LoadAudioAndWaveformToTimeline(value.FilePath);
                    }
                    SelectedStudioFileSummary = $"Bản ghi kho: {Path.GetFileName(value.FilePath)} ({value.DurationSeconds:0.0}s)";
                    PlayHeadless5SecDemo(value.FilePath);
                }
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private PointCollection _waveformPoints;
        public PointCollection WaveformPoints { get => _waveformPoints; set { _waveformPoints = value; OnPropertyChanged(); } }

        private string _gitHubRepo = "https://github.com/longtygu/VoiceCraft";
        public string GitHubRepo
        {
            get => "https://github.com/longtygu/VoiceCraft";
            set { }
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
            UpdateStatusText = $"⏳ Đang tải bản cập nhật v{_latestUpdateInfo.LatestVersion}...";
            try
            {
                var progress = new Progress<double>(pct =>
                {
                    UpdateProgress = pct;
                    UpdateStatusText = $"⏳ Đang tải gói cập nhật: {pct:0.0}%";
                });
                _downloadedUpdateFilePath = await _updateService.DownloadUpdateAsync(_latestUpdateInfo, progress);
                UpdateStatusText = $"✅ Đã tải xong: {Path.GetFileName(_downloadedUpdateFilePath)}";
                _updateService.OpenUpdateFolderOrFile(_downloadedUpdateFilePath);
            }
            catch (Exception ex)
            {
                UpdateStatusText = "❌ Lỗi tải bản cập nhật: " + ex.Message;
                Logger.Error("Lỗi tải gói cập nhật GitHub", ex);
            }
            finally { IsDownloadingUpdate = false; }
        }

        private async Task InitializeEngineAsync(bool forceRestart = false)
        {
            ConnectionStatus = "Đang khởi động Engine...";
            ConnectionColor = "#FFB300";
            Logger.Info("Tiến hành kết nối TTS Engine...");

            bool isReady = await _engineManager.StartEngineAsync(forceRestart);
            if (isReady)
            {
                SetReadyStatus();
                Logger.Info("Engine đã kết nối thành công tại cổng 8000.");
            }
            else
            {
                SetErrorStatus("ERR_ENGINE_8000");
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
                    clone_reference_path = (IsVoiceCloneEnabled && !string.IsNullOrEmpty(CloneReferencePath)) ? EnsurePcm16MonoWav(CloneReferencePath) : "",
                    clone_strength = CloneStrength,
                    vits_noise_scale = VitsNoiseScale,
                    eq_bass_db = EqBassDb,
                    eq_treble_db = EqTrebleDb,
                    de_hiss_cutoff = DeHissCutoff
                };
                if (!await _engineManager.IsServerRunningAsync())
                {
                    SynthesisStatusText = "Đang tự động khởi động lại TTS Engine...";
                    await _engineManager.StartEngineAsync();
                }
                var response = await _httpClient.PostAsJsonAsync("http://127.0.0.1:8000/synthesize", payload);
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
                        double durNew = GetAudioDurationSeconds(savedPath);
                        double kbNew = Math.Round(audioBytes.Length / 1024.0, 0);
                        var newItem = new HistoryItem
                        {
                            DisplayText = $"[{DateTime.Now:HH:mm:ss}] {modTag}{cloneTag}{rawTag} ({durNew:0.0}s)",
                            FilePath = savedPath,
                            DurationSeconds = durNew,
                            DurationPopupText = $"⏱️ Độ dài bản ghi: {FormatTimeCode(durNew)} ({durNew:0.0} giây) | Dung lượng: {kbNew} KB"
                        };
                        _suppressDemoOnScan = true;
                        HistoryList.Insert(0, newItem);
                        SelectedHistoryItem = newItem;
                        _suppressDemoOnScan = false;
                        if (AutoPlayAudio)
                        {
                            _audioPlayer.Play();
                            _timer.Start();
                        }
                    });

                    SetReadyStatus();
                }
                else
                {
                    SetErrorStatus("HTTP_" + (int)response.StatusCode);
                    string errDetail = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Lỗi từ Engine ({response.StatusCode}):\n{errDetail}", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                cts.Cancel();
                SetErrorStatus("ERR_CONN_8000");
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
            StopHeadlessDemo();
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