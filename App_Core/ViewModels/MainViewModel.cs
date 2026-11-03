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
        public bool IsEqEnabled { get; set; } = false;
        public string SelectedGender { get; set; }
        public bool BilingualAuto { get; set; } = true;
        public bool SingleVoiceMode { get; set; } = true;
        public bool AutoPlayAudio { get; set; } = true;
        public double Speed { get; set; } = 1.0;
        public double Pitch { get; set; } = 1.0;
        public double Volume { get; set; } = 100.0;
        public bool IsVoiceCloneEnabled { get; set; } = false;
        public string CloneReferencePath { get; set; } = "";
        public double CloneStrength { get; set; } = 0.80;
        public double EqBassDb { get; set; } = 0.0;
        public double EqTrebleDb { get; set; } = 0.0;
        public double DeHissCutoff { get; set; } = 10000.0;
        public double VitsNoiseScale { get; set; } = 0.67;
        public Dictionary<string, ModelDspProfile> ModelProfiles { get; set; } = new Dictionary<string, ModelDspProfile>();
        public string GitHubRepo { get; set; } = UpdateService.LockedGitHubRepoUrl;
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

            Logger.Info("Ứng dụng VoiceCraft Studio v1.0.2 khởi động.");

            SynthesizeCommand = new RelayCommand(async (o) => await SynthesizeAsync(), (o) => !IsProcessing && !string.IsNullOrWhiteSpace(InputText));
            PlayCommand = new RelayCommand((o) => { _audioPlayer.Play(); _timer.Start(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            PauseCommand = new RelayCommand((o) => { _audioPlayer.Pause(); _timer.Stop(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            StopCommand = new RelayCommand((o) => { _audioPlayer.Stop(); _timer.Stop(); CurrentPosition = 0; UpdateTimeDisplay(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            SaveCommand = new RelayCommand(SaveFile, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));

            RefreshModelsCommand = new RelayCommand((o) => RefreshAvailableModels());

            AdjustSpeakerIdCommand = new RelayCommand((param) =>
            {
                if (int.TryParse(param?.ToString(), out int delta))
                    SpeakerId = Math.Clamp(SpeakerId + delta, -1, 903);
            });

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
                    LoadSampleWaveformToPlayer(dlg.FileName);
                }
            });

            PreviewCloneSampleCommand = new RelayCommand((o) =>
            {
                if (!string.IsNullOrEmpty(CloneReferencePath) && File.Exists(CloneReferencePath))
                {
                    LoadSampleWaveformToPlayer(CloneReferencePath);
                    _audioPlayer.Play();
                    _timer.Start();
                }
            }, (o) => !string.IsNullOrEmpty(CloneReferencePath) && File.Exists(CloneReferencePath));

            ClearCloneSampleCommand = new RelayCommand((o) =>
            {
                CloneReferencePath = "";
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
            RepairAndDownloadModelsCommand = new RelayCommand(async (o) => await RepairAndDownloadModelsAsync(), (o) => !IsRepairingModels);

            OpenFolderCommand = new RelayCommand((param) =>
            {
                string target = param?.ToString();
                string folder = target == "Models" ? _modelsDir : _outputDir;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", folder);
            });

            ClearCacheCommand = new RelayCommand((o) =>
            {
                try
                {
                    _audioPlayer.Stop();
                    int count = 0;
                    foreach (var f in Directory.GetFiles(_outputDir, "*.wav"))
                    {
                        try { File.Delete(f); count++; } catch { }
                    }
                    HistoryList.Clear();
                    _currentAudioPath = null;
                    WaveformPoints = new PointCollection();
                    TotalDuration = 0;
                    CurrentPosition = 0;
                    UpdateTimeDisplay();
                    MessageBox.Show($"Đã dọn dẹp {count} file âm thanh trong thư mục Output!", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { Logger.Error("Lỗi dọn Output", ex); }
            });

            CopyGitHubRepoCommand = new RelayCommand((o) =>
            {
                try
                {
                    Clipboard.SetText(UpdateService.LockedGitHubRepoUrl);
                    UpdateStatusText = "📋 Đã sao chép đường dẫn GitHub vào Clipboard!";
                }
                catch { }
            });

            CheckUpdateCommand = new RelayCommand(async (o) => await CheckForUpdateAsync(), (o) => !IsCheckingUpdate && !IsDownloadingUpdate);
            DownloadUpdateCommand = new RelayCommand(async (o) => await DownloadUpdateAsync(), (o) => !IsDownloadingUpdate && IsUpdateAvailable);

            RefreshAvailableModels();
            LoadSettings();
            UpdateSelectedModelInfo();
            UpdateThemeBrushes();
            UpdateTimeDisplay();
            _ = InitializeEngineAsync();
        }

        private void LoadSampleWaveformToPlayer(string filePath)
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
                Logger.Error("Lỗi nạp sóng âm file mẫu", ex);
            }
        }

        // --- TRANG THAI MODEL SAN SANG / LOI MODEL PHAT SANG NHAP NHAY ---
        private bool _isModelError = false;
        public bool IsModelError
        {
            get => _isModelError;
            set { _isModelError = value; OnPropertyChanged(); }
        }

        private string _statusBadgeBackground = "#3800C853";
        public string StatusBadgeBackground
        {
            get => _statusBadgeBackground;
            set { _statusBadgeBackground = value; OnPropertyChanged(); }
        }

        private string _statusBadgeBorder = "#AA00E676";
        public string StatusBadgeBorder
        {
            get => _statusBadgeBorder;
            set { _statusBadgeBorder = value; OnPropertyChanged(); }
        }

        private string _statusTextForeground = "#E8FFF0";
        public string StatusTextForeground
        {
            get => _statusTextForeground;
            set { _statusTextForeground = value; OnPropertyChanged(); }
        }

        public string StatusPopupText => "thử tải lại và sửa lỗi model trong cài đặt";

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
            StatusBadgeBackground = "#88FF1744";
            StatusBadgeBorder = "#FFFF1744";
            StatusTextForeground = "#FFFFFF";
        }

        // --- TAB 1: CHON MODEL & AN/HIEN VUNG MIEN + GIOI TINH ---
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
                OnPropertyChanged(nameof(AutoModelOptionsVisibility));
                LoadProfileForModel(_selectedModelFile);
                UpdateSelectedModelInfo();
                SaveSettings();
            }
        }

        public Visibility AutoModelOptionsVisibility =>
            (string.IsNullOrEmpty(SelectedModelFile) || SelectedModelFile.Contains("(Auto)"))
                ? Visibility.Visible
                : Visibility.Collapsed;

        private string _selectedModelInfoText = "Chế độ Tự động: Tự chọn model theo vùng miền và giới tính.";
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

        // --- TAB 2: NHAI GIONG AI (THU GON MAC DINH, MO RONG KHI GAT BAT) ---
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

        // --- TAB 3: BO LOC EQ THEO MODEL (THU GON MAC DINH, MO RONG KHI GAT BAT) ---
        private bool _isEqEnabled = false;
        public bool IsEqEnabled
        {
            get => _isEqEnabled;
            set
            {
                _isEqEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EqPanelVisibility));
                SaveSettings();
            }
        }
        public Visibility EqPanelVisibility => IsEqEnabled ? Visibility.Visible : Visibility.Collapsed;

        private bool _bypassDsp = false;
        public bool BypassDsp
        {
            get => _bypassDsp;
            set { _bypassDsp = value; OnPropertyChanged(); SaveSettings(); }
        }

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

        // --- SUA & TAI MODEL TU DONG TRONG CAI DAT ---
        private bool _isRepairingModels;
        public bool IsRepairingModels
        {
            get => _isRepairingModels;
            set
            {
                _isRepairingModels = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelRepairVisibility));
                CommandManager.InvalidateRequerySuggested();
            }
        }
        public Visibility ModelRepairVisibility => (IsRepairingModels || ModelRepairProgress > 0) ? Visibility.Visible : Visibility.Collapsed;

        private double _modelRepairProgress;
        public double ModelRepairProgress
        {
            get => _modelRepairProgress;
            set { _modelRepairProgress = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
        }

        private string _modelRepairStatusText = "Nhấn 'Sửa và Tải Model tự động' để kiểm tra và tải đủ các Model AI.";
        public string ModelRepairStatusText
        {
            get => _modelRepairStatusText;
            set { _modelRepairStatusText = value; OnPropertyChanged(); }
        }

        private async Task RepairAndDownloadModelsAsync()
        {
            IsRepairingModels = true;
            ModelRepairProgress = 5;
            ModelRepairStatusText = "⏳ Đang kiểm tra các gói thư viện Python & Model AI...";
            try
            {
                string setupScript = Path.Combine(Logger.ProjectRoot, "setup_models.ps1");
                if (File.Exists(setupScript))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{setupScript}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    while (!proc.HasExited)
                    {
                        string line = await proc.StandardOutput.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            ModelRepairProgress = Math.Min(92, ModelRepairProgress + 8);
                            ModelRepairStatusText = line;
                        }
                    }
                }
                ModelRepairProgress = 95;
                ModelRepairStatusText = "⏳ Đang khởi động lại TTS Engine...";
                RefreshAvailableModels();
                await InitializeEngineAsync();
                ModelRepairProgress = 100;
                ModelRepairStatusText = "✅ Đã kiểm tra, sửa lỗi và nạp lại toàn bộ Model thành công!";
            }
            catch (Exception ex)
            {
                ModelRepairStatusText = "❌ Lỗi khi sửa Model: " + ex.Message;
                SetErrorStatus("ERR_REPAIR_MODEL");
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
            OnPropertyChanged(nameof(AutoModelOptionsVisibility));
            UpdateSelectedModelInfo();
        }

        private void UpdateSelectedModelInfo()
        {
            if (string.IsNullOrEmpty(SelectedModelFile) || SelectedModelFile.Contains("(Auto)"))
            {
                SelectedModelInfoText = "Tự động phối hợp Model theo Vùng miền và Giới tính.";
                return;
            }
            if (SelectedModelFile.StartsWith("[VieNeu-TTS-24kHz]"))
            {
                SelectedModelInfoText = "Engine: VieNeu-TTS 24000 Hz | Tối ưu ngữ điệu 6 thanh Tiếng Việt.";
                return;
            }
            if (SelectedModelFile.StartsWith("[VietTTS-HiFiGAN]"))
            {
                SelectedModelInfoText = "Engine: VietTTS NAT + HiFi-GAN (100% Offline) | Tối ưu độ ấm giọng Việt.";
                return;
            }
            if (SelectedModelFile.StartsWith("[Multilingual-24kHz]"))
            {
                SelectedModelInfoText = "Engine: Neural Multilingual 24000 Hz | Đọc trực tiếp Việt và Anh bằng 1 giọng duy nhất.";
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
                    _isEqEnabled = data.IsEqEnabled;
                    if (!string.IsNullOrEmpty(data.SelectedGender)) _selectedGender = data.SelectedGender;
                    _bilingualAuto = data.BilingualAuto;
                    _singleVoiceMode = data.SingleVoiceMode;
                    _autoPlayAudio = data.AutoPlayAudio;
                    _speed = Math.Clamp(data.Speed, 0.25, 3.0);
                    _pitch = Math.Clamp(data.Pitch, 0.5, 2.0);
                    _volume = Math.Clamp(data.Volume, 0, 100);
                    _audioPlayer.Volume = (float)(_volume / 100.0);
                    _isVoiceCloneEnabled = data.IsVoiceCloneEnabled;
                    _cloneReferencePath = data.CloneReferencePath ?? "";
                    _cloneStrength = Math.Clamp(data.CloneStrength, 0.0, 1.0);
                    _eqBassDb = Math.Clamp(data.EqBassDb, -12.0, 12.0);
                    _eqTrebleDb = Math.Clamp(data.EqTrebleDb, -12.0, 12.0);
                    _deHissCutoff = Math.Clamp(data.DeHissCutoff, 4000.0, 12000.0);
                    _vitsNoiseScale = Math.Clamp(data.VitsNoiseScale, 0.10, 1.00);
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
                    IsEqEnabled = IsEqEnabled,
                    SelectedGender = SelectedGender,
                    BilingualAuto = BilingualAuto,
                    SingleVoiceMode = SingleVoiceMode,
                    AutoPlayAudio = AutoPlayAudio,
                    Speed = Speed,
                    Pitch = Pitch,
                    Volume = Volume,
                    IsVoiceCloneEnabled = IsVoiceCloneEnabled,
                    CloneReferencePath = CloneReferencePath,
                    CloneStrength = CloneStrength,
                    EqBassDb = EqBassDb,
                    EqTrebleDb = EqTrebleDb,
                    DeHissCutoff = DeHissCutoff,
                    VitsNoiseScale = VitsNoiseScale,
                    ModelProfiles = _modelProfiles,
                    GitHubRepo = UpdateService.LockedGitHubRepoUrl
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
            "Đen Tuyền Studio (Pure Pitch Black)",
            "Xám Khói Hiện Đại (Modern Charcoal Gray)",
            "Đen Khói Windows 10 (Obsidian Glass)",
            "Xanh Băng Windows 7 (Aero Ice Blue)",
            "Xanh Đại Dương Sâu (Deep Ocean Glass)",
            "Xanh Ngọc Lục Bảo (Emerald Glass)",
            "Tím Pha Lê Đêm (Royal Amethyst)",
            "Đỏ Hồng Ngọc (Crimson Ruby Glass)",
            "Xám Bạc Hiện Đại (Frosted Slate)"
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
            "Tím Hiện Đại (#8B5CF6)",
            "Đỏ Hồng Ruby (#F43F5E)",
            "Vàng Hổ Phách (#F59E0B)",
            "Xám Thép Trung Tính (#64748B)",
            "Đen Carbon (#27272A)"
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
            "Tím Sóng Âm (#A855F7)",
            "Cam Hoàng Hôn (#FB923C)",
            "Xám Bạc Sóng Âm (#94A3B8)",
            "Đen Khói Sóng Âm (#3F3F46)",
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
            "Hồng Tâm Điểm (#FF4081)",
            "Xám Bạc (#CBD5E1)",
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
            if (SelectedAeroTheme.Contains("Pitch Black")) baseRgb = "050507";
            else if (SelectedAeroTheme.Contains("Charcoal")) baseRgb = "22262E";
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

        private string _inputText = "Xin chào! Đây là phiên bản VoiceCraft Studio 1.0.2 với thanh trạng thái Model thông minh, nhái giọng AI và tự động cập nhật trực tiếp.";
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

        private string _connectionStatus = "Đang khởi động Model...";
        public string ConnectionStatus { get => _connectionStatus; set { _connectionStatus = value; OnPropertyChanged(); } }

        private string _connectionColor = "#FFB300";
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
                    LoadSampleWaveformToPlayer(value.FilePath);
                }
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private PointCollection _waveformPoints;
        public PointCollection WaveformPoints { get => _waveformPoints; set { _waveformPoints = value; OnPropertyChanged(); } }

        // KHOA CUNG DUONG DAN GITHUB (CHI CHO SAO CHEP, KHONG CHO SUA)
        public string GitHubRepo => UpdateService.LockedGitHubRepoUrl;

        private string _updateStatusText = "Phiên bản hiện tại: v1.0.2 (Nhấn 'Kiểm tra cập nhật' để kiểm tra từ GitHub)";
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
        public ICommand RepairAndDownloadModelsCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ClearCacheCommand { get; }
        public ICommand CopyGitHubRepoCommand { get; }
        public ICommand CheckUpdateCommand { get; }
        public ICommand DownloadUpdateCommand { get; }

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
            UpdateStatusText = $"⏳ Đang tải và thay thế trực tiếp v{_latestUpdateInfo.LatestVersion}...";
            try
            {
                var progress = new Progress<double>(pct =>
                {
                    UpdateProgress = pct;
                    UpdateStatusText = $"⏳ Đang cập nhật trực tiếp: {pct:0.0}%";
                });
                await _updateService.DownloadAndApplyUpdateAsync(
                    _latestUpdateInfo,
                    progress,
                    msg => Application.Current?.Dispatcher?.Invoke(() => UpdateStatusText = msg)
                );
            }
            catch (Exception ex)
            {
                UpdateStatusText = "❌ Lỗi cập nhật: " + ex.Message;
                Logger.Error("Lỗi tự động cập nhật GitHub", ex);
            }
            finally { IsDownloadingUpdate = false; }
        }

        private async Task InitializeEngineAsync()
        {
            IsModelError = false;
            ConnectionStatus = "Đang khởi động Model...";
            ConnectionColor = "#FFB300";
            StatusBadgeBackground = "#38FFB300";
            StatusBadgeBorder = "#AAFFB300";
            StatusTextForeground = "#FFF8E1";

            bool isReady = await _engineManager.StartEngineAsync();
            if (isReady)
            {
                SetReadyStatus();
                Logger.Info("Engine v1.0.2 đã kết nối thành công tại cổng 8000.");
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
            SynthesisStatusText = "Đang phân tích ngữ cảnh và nạp Model...";
            IsModelError = false;
            ConnectionStatus = "Đang tổng hợp giọng nói...";
            ConnectionColor = "#00C8FF";
            StatusBadgeBackground = "#3800B4FF";
            StatusBadgeBorder = "#AA00E5FF";
            StatusTextForeground = "#E6F7FF";

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
                                    ? $"Đang tổng hợp và sao chép vân tay giọng ({CloneStrengthPercent})..."
                                    : "Đang tổng hợp sóng âm nơ-ron (Speaker ID: " + SpeakerId + ")...";
                            }
                            else if (SynthesisProgress < 92)
                            {
                                SynthesisProgress += 1.5;
                                SynthesisStatusText = (!IsEqEnabled || BypassDsp)
                                    ? "Đang xuất sóng âm chuẩn từ Model..."
                                    : $"Đang lọc Studio EQ (Bass: {EqBassDisplay}, Treble: {EqTrebleDisplay})...";
                            }
                        });
                    }
                }
                catch (TaskCanceledException) { }
            });

            try
            {
                if (!await _engineManager.IsServerRunningAsync())
                {
                    await _engineManager.StartEngineAsync();
                }

                var payload = new
                {
                    text = InputText,
                    voice_id = SelectedVoice,
                    model_file = SelectedModelFile,
                    speaker_id = SpeakerId,
                    bypass_dsp = (!IsEqEnabled || BypassDsp),
                    gender = SelectedGender,
                    speed = Speed,
                    pitch = Pitch,
                    bilingual_auto = BilingualAuto,
                    single_voice_mode = SingleVoiceMode,
                    clone_enabled = IsVoiceCloneEnabled,
                    clone_reference_path = CloneReferencePath,
                    clone_strength = CloneStrength,
                    vits_noise_scale = VitsNoiseScale,
                    eq_bass_db = IsEqEnabled ? EqBassDb : 0.0,
                    eq_treble_db = IsEqEnabled ? EqTrebleDb : 0.0,
                    de_hiss_cutoff = IsEqEnabled ? DeHissCutoff : 11000.0
                };

                var response = await _httpClient.PostAsJsonAsync("http://127.0.0.1:8000/synthesize", payload);
                cts.Cancel();

                if (response.IsSuccessStatusCode)
                {
                    SynthesisProgress = 96;
                    SynthesisStatusText = "Đang dựng sóng âm thanh và hoàn tất file .WAV...";

                    byte[] audioBytes = await response.Content.ReadAsByteArrayAsync();
                    string modTag = SelectedModelFile.Contains("(Auto)")
                        ? (SelectedGender.Contains("Nam") ? "AutoNam" : "AutoNu")
                        : Path.GetFileNameWithoutExtension(SelectedModelFile.Replace("[", "").Replace("]", "")).Split(' ')[0];
                    string cloneTag = (IsVoiceCloneEnabled && !string.IsNullOrEmpty(CloneReferencePath)) ? "_CLONE" : "";
                    string fileName = $"VC_{modTag}{cloneTag}_{DateTime.Now:HHmmss}.wav";
                    string savedPath = Path.Combine(_outputDir, fileName);
                    await File.WriteAllBytesAsync(savedPath, audioBytes);

                    SynthesisProgress = 100;
                    SynthesisStatusText = "Hoàn tất 100%!";
                    await Task.Delay(120);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var newItem = new HistoryItem
                        {
                            DisplayText = $"[{DateTime.Now:HH:mm:ss}] {modTag}{cloneTag} | {Speed:0.0#}x",
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

                    SetReadyStatus();
                }
                else
                {
                    SetErrorStatus($"HTTP_{(int)response.StatusCode}");
                    string errDetail = await response.Content.ReadAsStringAsync();
                    Logger.Error($"Lỗi từ FastAPI server ({response.StatusCode}): {errDetail}");
                    MessageBox.Show($"Lỗi từ Model ({response.StatusCode}):\n{errDetail}", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                cts.Cancel();
                SetErrorStatus("ERR_CONN_REFUSED");
                Logger.Error("Lỗi hệ thống khi gọi API Synthesize", ex);
                MessageBox.Show("Không thể kết nối tới TTS Model Engine.\nHãy mở Cài đặt (⚙) -> 'Sửa và Tải Model tự động' hoặc 'Khởi động lại Engine'!\nChi tiết: " + ex.Message, "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private PointCollection GenerateWaveform(string filePath)
        {
            var points = new PointCollection();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return points;

            double width = 1000, height = 100, midY = height / 2;

            try
            {
                using var reader = new AudioFileReader(filePath);
                long totalSamples = Math.Max(1, reader.Length / 4);
                int samplesPerPixel = Math.Max(1, (int)(totalSamples / width));
                float[] buffer = new float[samplesPerPixel];

                var topPoints = new List<Point>();
                var bottomPoints = new List<Point>();

                int x = 0, samplesRead;
                while ((samplesRead = reader.Read(buffer, 0, buffer.Length)) > 0 && x <= width)
                {
                    float max = 0;
                    for (int i = 0; i < samplesRead; i++)
                    {
                        float val = Math.Abs(buffer[i]);
                        if (val > max) max = val;
                    }
                    max = Math.Min(1.0f, max);
                    topPoints.Add(new Point(x, midY - (max * midY * 0.92)));
                    bottomPoints.Insert(0, new Point(x, midY + (max * midY * 0.92)));
                    x++;
                }

                points.Add(new Point(0, midY));
                foreach (var p in topPoints) points.Add(p);
                foreach (var p in bottomPoints) points.Add(p);
                points.Add(new Point(0, midY));
            }
            catch (Exception ex) { Logger.Error("Lỗi vẽ waveform UI", ex); }

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