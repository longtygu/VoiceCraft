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
        public string SelectedModelFile { get; set; } = "T\u1EF1 \u0111\u1ED9ng theo v\u00F9ng mi\u1EC1n (Auto)";
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
        public bool IsCustomEqEnabled { get; set; } = false;
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
        private const string AutoModelName = "T\u1EF1 \u0111\u1ED9ng theo v\u00F9ng mi\u1EC1n (Auto)";
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
        private bool _isRepairingModels;
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
            _modelsDir = Path.Combine(Logger.ProjectRoot, "Models");
            if (!Directory.Exists(_outputDir)) Directory.CreateDirectory(_outputDir);
            if (!Directory.Exists(_modelsDir)) Directory.CreateDirectory(_modelsDir);
            _settingsFilePath = Path.Combine(Logger.ProjectRoot, "settings.json");

            SynthesizeCommand = new RelayCommand(async (o) => await SynthesizeAsync(), (o) => !IsProcessing && !string.IsNullOrWhiteSpace(InputText));
            PlayCommand = new RelayCommand((o) => { _audioPlayer.Play(); _timer.Start(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            PauseCommand = new RelayCommand((o) => { _audioPlayer.Pause(); _timer.Stop(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            StopCommand = new RelayCommand((o) => { _audioPlayer.Stop(); _timer.Stop(); CurrentPosition = 0; UpdateTimeDisplay(); }, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));
            SaveCommand = new RelayCommand(SaveFile, (o) => !IsProcessing && !string.IsNullOrEmpty(_currentAudioPath));

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

            // SUA LOI KHONG HIEN SONG AM KHI CHON GIONG DOC MAU (.WAV / .MP3)
            SelectCloneSampleCommand = new RelayCommand((o) =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Ch\u1ECDn file \u00E2m thanh m\u1EABu (5 - 15 gi\u00E2y) \u0111\u1EC3 Nh\u00E1i gi\u1ECDng",
                    Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    CloneReferencePath = dlg.FileName;
                    IsVoiceCloneEnabled = true;
                    LoadAudioToTimeline(CloneReferencePath, false);
                }
            });

            PreviewCloneSampleCommand = new RelayCommand((o) =>
            {
                if (!string.IsNullOrEmpty(CloneReferencePath) && File.Exists(CloneReferencePath))
                {
                    LoadAudioToTimeline(CloneReferencePath, true);
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

            ApplyAudioPresetCommand = new RelayCommand((param) =>
            {
                string preset = param?.ToString() ?? "Clean";
                BypassDsp = false;
                IsCustomEqEnabled = true;
                if (preset == "Broadcast") { Speed = 1.0; Pitch = 1.0; EqBassDb = 2.5; EqTrebleDb = 2.0; DeHissCutoff = 9500; VitsNoiseScale = 0.60; }
                else if (preset == "Story") { Speed = 0.92; Pitch = 0.96; EqBassDb = 4.0; EqTrebleDb = 0.5; DeHissCutoff = 8000; VitsNoiseScale = 0.55; }
                else if (preset == "Review") { Speed = 1.18; Pitch = 1.02; EqBassDb = 1.5; EqTrebleDb = 3.0; DeHissCutoff = 10000; VitsNoiseScale = 0.65; }
                else { Speed = 1.0; Pitch = 1.0; EqBassDb = 0.0; EqTrebleDb = 2.0; DeHissCutoff = 8500; VitsNoiseScale = 0.60; }
            });

            ClearTextCommand = new RelayCommand((o) => InputText = "");
            PasteTextCommand = new RelayCommand((o) =>
            {
                if (Clipboard.ContainsText()) InputText = Clipboard.GetText();
            });
            OpenSelectedHistoryFileCommand = new RelayCommand((o) =>
            {
                if (SelectedHistoryItem != null && File.Exists(SelectedHistoryItem.FilePath))
                    Process.Start("explorer.exe", $"/select,\"{SelectedHistoryItem.FilePath}\"");
            }, (o) => SelectedHistoryItem != null);

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
            RepairModelsCommand = new RelayCommand(async (o) => await RepairAndDownloadModelsAsync(), (o) => !_isRepairingModels);

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
                    MessageBox.Show($"\u0110\u00E3 d\u1ECDn d\u1EB9p {count} file \u00E2m thanh trong th\u01B0 m\u1EE5c Output!", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { Logger.Error("L\u1ED7i d\u1ECDn Output", ex); }
            });

            CheckUpdateCommand = new RelayCommand(async (o) => await CheckForUpdateAsync(), (o) => !IsCheckingUpdate && !IsDownloadingUpdate);
            DownloadUpdateCommand = new RelayCommand(async (o) => await DownloadUpdateAsync(), (o) => !IsDownloadingUpdate && _latestUpdateInfo != null && !string.IsNullOrEmpty(_latestUpdateInfo.DownloadUrl));
            OpenUpdateFolderCommand = new RelayCommand((o) => _updateService.OpenUpdateFolderOrFile(_downloadedUpdateFilePath));

            RefreshAvailableModels();
            LoadSettings();
            // Dam bao Tab 2 va Tab 3 mac dinh thu gon khi moi mo app theo yeu cau
            _isVoiceCloneEnabled = false;
            _isCustomEqEnabled = false;
            OnPropertyChanged(nameof(IsVoiceCloneEnabled));
            OnPropertyChanged(nameof(VoiceClonePanelVisibility));
            OnPropertyChanged(nameof(IsCustomEqEnabled));
            OnPropertyChanged(nameof(CustomEqPanelVisibility));

            UpdateSelectedModelInfo();
            UpdateThemeBrushes();
            UpdateTimeDisplay();
            _ = InitializeEngineAsync();
        }

        private void LoadAudioToTimeline(string filePath, bool autoPlay)
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
                if (autoPlay)
                {
                    _audioPlayer.Play();
                    _timer.Start();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("L\u1ED7i n\u1EA1p file \u00E2m thanh l\u00EAn Timeline", ex);
            }
        }

        // --- TAB 1: CHON MODEL & AN/HIEN VUNG MIEN + GIOI TINH ---
        public ObservableCollection<string> AvailableModels { get; } = new ObservableCollection<string>();

        private string _selectedModelFile = AutoModelName;
        public string SelectedModelFile
        {
            get => _selectedModelFile;
            set
            {
                string nextVal = string.IsNullOrEmpty(value) ? AutoModelName : value;
                if (_selectedModelFile == nextVal) return;

                SaveCurrentModelProfile();
                _selectedModelFile = nextVal;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAutoModelSelected));
                OnPropertyChanged(nameof(AutoModelOptionsVisibility));
                OnPropertyChanged(nameof(MultiSpeakerVisibility));
                LoadProfileForModel(_selectedModelFile);
                UpdateSelectedModelInfo();
                SaveSettings();
            }
        }

        public bool IsAutoModelSelected => string.IsNullOrEmpty(SelectedModelFile) || SelectedModelFile == AutoModelName;
        public Visibility AutoModelOptionsVisibility => IsAutoModelSelected ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MultiSpeakerVisibility => (!IsAutoModelSelected && (SelectedModelFile.Contains("vivos") || SelectedModelFile.Contains("libritts") || SelectedModelFile.Contains("904spk")))
            ? Visibility.Visible
            : Visibility.Collapsed;

        private string _selectedModelInfoText = "Ch\u1EBF \u0111\u1ED9 T\u1EF1 \u0111\u1ED9ng: Cho ph\u00E9p ch\u1ECDn V\u00F9ng mi\u1EC1n & Gi\u1EDB;i t\u00EDnh.";
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

        // --- TAB 2: NHAI GIONG AI (CONG TAC THU GON / MO RONG) ---
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
            ? "Ch\u01B0a ch\u1ECDn m\u1EABu gi\u1ECDng (.wav/.mp3)"
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

        // --- TAB 3: BO LOC EQ THEO MODEL (CONG TAC THU GON / MO RONG) ---
        private bool _isCustomEqEnabled = false;
        public bool IsCustomEqEnabled
        {
            get => _isCustomEqEnabled;
            set
            {
                _isCustomEqEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CustomEqPanelVisibility));
                SaveSettings();
            }
        }
        public Visibility CustomEqPanelVisibility => IsCustomEqEnabled ? Visibility.Visible : Visibility.Collapsed;

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
        public string DeHissDisplay => _deHissCutoff >= 10500 ? "T\u1EAFt (Full)" : $"{_deHissCutoff:0} Hz";

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

        // --- CUA SO TRANG THAI SUA & TAI MODEL TU DONG TRONG CAI DAT ---
        private string _modelSetupLogText = "S\u1EE7n s\u00E0ng. Nh\u1EA5n 'S\u1EEDa & T\u1EA3i Model ngay' \u0111\u1EC3 ki\u1EC3m tra v\u00E0 t\u1EA3i t\u1EF1 \u0111\u1ED9ng to\u00E0n b\u1ED9 Model AI.";
        public string ModelSetupLogText
        {
            get => _modelSetupLogText;
            set { _modelSetupLogText = value; OnPropertyChanged(); }
        }

        private async Task RepairAndDownloadModelsAsync()
        {
            if (_isRepairingModels) return;
            _isRepairingModels = true;
            CommandManager.InvalidateRequerySuggested();
            ModelSetupLogText = "[B\u1EAET \u0110\u1EA6U] \u0110ang ki\u1EC3m tra th\u01B0 vi\u1EC7n Python & t\u1EA3i b\u1ED9 Model AI...\n";

            try
            {
                string scriptPath = Path.Combine(Logger.ProjectRoot, "setup_models.ps1");
                if (!File.Exists(scriptPath))
                {
                    ModelSetupLogText += "[TH\u00D4NG B\u00C1O] \u0110ang t\u1EA1o file setup_models.ps1 m\u1EB7c \u0111\u1ECBnh...\n";
                    File.WriteAllText(scriptPath, "python -m pip install edge-tts kokoro-onnx miniaudio pedalboard cmudict scipy numpy transformers torch piper-tts onnxruntime fastapi uvicorn pydantic\n");
                }

                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        WorkingDirectory = Logger.ProjectRoot,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var proc = new Process { StartInfo = psi };
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            Application.Current?.Dispatcher?.Invoke(() => ModelSetupLogText += e.Data + "\n");
                    };
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            Application.Current?.Dispatcher?.Invoke(() => ModelSetupLogText += "[LOG] " + e.Data + "\n");
                    };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    proc.WaitForExit();
                });

                RefreshAvailableModels();
                ModelSetupLogText += "[HO\u00C0N T\u1EA4T] \u0110\u00E3 ki\u1EC3m tra & c\u1EADp nh\u1EADt xong danh s\u00E1ch Model AI!";
            }
            catch (Exception ex)
            {
                ModelSetupLogText += "[L\u1ED6I] " + ex.Message;
            }
            finally
            {
                _isRepairingModels = false;
                CommandManager.InvalidateRequerySuggested();
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
                        _eqBassDb = 2.0; _eqTrebleDb = 1.5; _deHissCutoff = 7500.0; _vitsNoiseScale = 0.55;
                    }
                    else
                    {
                        _eqBassDb = 0.0; _eqTrebleDb = 0.0; _deHissCutoff = 10000.0; _vitsNoiseScale = 0.67;
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
            finally { _isSwitchingModelProfile = false; }
        }

        public void RefreshAvailableModels()
        {
            string currentSelection = _selectedModelFile;
            AvailableModels.Clear();
            AvailableModels.Add(AutoModelName);

            try
            {
                AvailableModels.Add("[VieNeu-TTS-24kHz] Gi\u1ECDng Nam Truy\u1EC1n C\u1EA3m (Vietnamese Neural Prosody)");
                AvailableModels.Add("[VieNeu-TTS-24kHz] Gi\u1ECDng N\u1EEF Truy\u1EC1n C\u1EA3m (Vietnamese Neural Prosody)");
                AvailableModels.Add("[VietTTS-HiFiGAN] Gi\u1ECDng N\u1EEF Mi\u1EC1n B\u1EAFc Chu\u1EA9n (Offline NAT + Prosody)");
                AvailableModels.Add("[VietTTS-HiFiGAN] Gi\u1ECDng Nam Tr\u1EA7m Chu\u1EA9n (Offline VITS + HiFi-GAN)");

                AvailableModels.Add("[Multilingual-24kHz] en-US-AndrewMultilingualNeural (Nam \u0110\u1ECDc Vi\u1EC7t+Anh \u0110\u1ED3ng Nh\u1EA5t)");
                AvailableModels.Add("[Multilingual-24kHz] en-US-AvaMultilingualNeural (N\u1EEF \u0110\u1ECDc Vi\u1EC7t+Anh \u0110\u1ED3ng Nh\u1EA5t)");
                AvailableModels.Add("[Multilingual-24kHz] en-US-BrianMultilingualNeural (Nam Tr\u1EA7m Vi\u1EC7t+Anh \u0110\u1ED3ng Nh\u1EA5t)");
                AvailableModels.Add("[Multilingual-24kHz] en-US-EmmaMultilingualNeural (N\u1EEF \u1EA4m Vi\u1EC7t+Anh \u0110\u1ED3ng Nh\u1EA5t)");
                AvailableModels.Add("[Neural-24kHz] vi-VN-NamMinhNeural (Gi\u1ECDng Nam Vi\u1EC7t Si\u00EAu Th\u1EF1c)");
                AvailableModels.Add("[Neural-24kHz] vi-VN-HoaiMyNeural (Gi\u1ECDng N\u1EEF Vi\u1EC7t Si\u00EAu Th\u1EF1c)");

                if (File.Exists(Path.Combine(_modelsDir, "kokoro_24k", "kokoro-v1.0.onnx")))
                {
                    AvailableModels.Add("[Kokoro-82M-24kHz] af_heart (N\u1EEF M\u1EF9 Ph\u00F2ng Thu Top #1)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] af_bella (N\u1EEF M\u1EF9 Truy\u1EC1n C\u1EA3m)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] am_adam (Nam M\u1EF9 Tr\u1EA7m \u1EA4m)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] am_michael (Nam M\u1EF9 Ph\u00E1t Thanh Vi\u00EAn)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] bf_emma (N\u1EEF Anh Qu\u1ED1c Ho\u00E0ng Gia)");
                    AvailableModels.Add("[Kokoro-82M-24kHz] bm_george (Nam Anh Qu\u1ED1c C\u1ED5 \u0110i\u1EC3n)");
                }

                if (Directory.Exists(Path.Combine(_modelsDir, "mms_vie_male")) &&
                    File.Exists(Path.Combine(_modelsDir, "mms_vie_male", "config.json")))
                {
                    AvailableModels.Add("[PyTorch] mms_vie_male (Meta AI Gi\u1ECDng Nam)");
                }

                if (Directory.Exists(_modelsDir))
                {
                    var onnxFiles = Directory.GetFiles(_modelsDir, "*.onnx")
                        .Where(f => new FileInfo(f).Length > 1000000)
                        .Select(Path.GetFileName)
                        .OrderBy(n => n);

                    foreach (var name in onnxFiles) AvailableModels.Add(name);
                }
            }
            catch (Exception ex) { Logger.Error("L\u1ED7i qu\u00E9t model", ex); }

            _selectedModelFile = AvailableModels.Contains(currentSelection) ? currentSelection : AutoModelName;
            OnPropertyChanged(nameof(SelectedModelFile));
            OnPropertyChanged(nameof(IsAutoModelSelected));
            OnPropertyChanged(nameof(AutoModelOptionsVisibility));
            OnPropertyChanged(nameof(MultiSpeakerVisibility));
            UpdateSelectedModelInfo();
        }

        private void UpdateSelectedModelInfo()
        {
            if (IsAutoModelSelected)
            {
                SelectedModelInfoText = "Ch\u1EBF \u0111\u1ED9 T\u1EF1 \u0111\u1ED9ng: Cho ph\u00E9p t\u00F9y ch\u1ECDn V\u00F9ng mi\u1EC1n & Gi\u1EDB;i t\u00EDnh b\u00EAn d\u01B0\u1EDBi.";
                return;
            }
            if (SelectedModelFile.StartsWith("[VieNeu-TTS-24kHz]"))
            {
                SelectedModelInfoText = "Engine: VieNeu-TTS 24000 Hz | T\u1ED1i \u01B0u ng\u1EEF \u0111i\u1EC7u 6 thanh Ti\u1EBFng Vi\u1EC7t (V\u00F9ng mi\u1EC1n/Gi\u1EDB;i t\u00EDnh \u0111\u00E3 c\u1ED1 \u0111\u1ECBnh theo Model).";
                return;
            }
            if (SelectedModelFile.StartsWith("[VietTTS-HiFiGAN]"))
            {
                SelectedModelInfoText = "Engine: VietTTS NAT + HiFi-GAN Offline | T\u1ED1i \u01B0u \u0111\u1ED9 \u1EA5m gi\u1ECDng Vi\u1EC7t.";
                return;
            }
            if (SelectedModelFile.StartsWith("[Multilingual-24kHz]"))
            {
                SelectedModelInfoText = "Engine: Neural Multilingual 24000 Hz | \u0110\u1ECDc tr\u1EF1c ti\u1EBFp Vi\u1EC7t & Anh b\u1EB1ng 1 gi\u1ECDng duy nh\u1EA5t.";
                return;
            }
            if (SelectedModelFile.StartsWith("[Neural-24kHz]"))
            {
                SelectedModelInfoText = "Engine: Microsoft Neural 24000 Hz | Gi\u1ECDng Ti\u1EBFng Vi\u1EC7t chu\u1EA9n ph\u00F2ng thu.";
                return;
            }
            if (SelectedModelFile.StartsWith("[Kokoro-82M-24kHz]"))
            {
                SelectedModelInfoText = "Engine: Kokoro-82M ONNX (100% Offline) | T\u1EA7n s\u1ED1: 24000 Hz.";
                return;
            }
            if (SelectedModelFile.StartsWith("[PyTorch]"))
            {
                SelectedModelInfoText = "Engine: PyTorch VITS (100% Offline) | Gi\u1ECDng Nam b\u1EA3n ng\u1EEF Meta AI.";
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
                    string spkNote = numSpk > 1 ? $"{numSpk} gi\u1ECDng (Speaker ID: 0 -> {numSpk - 1})" : "1 gi\u1ECDng \u0111\u01A1n";
                    SelectedModelInfoText = $"ONNX | T\u1EA7n s\u1ED1 g\u1ED1c: {sr} Hz | {spkNote}";
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
            catch (Exception ex) { Logger.Error("Kh\u00F4ng th\u1EC3 \u0111\u1ECDc settings.json", ex); }
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
                    IsCustomEqEnabled = IsCustomEqEnabled,
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
            catch (Exception ex) { Logger.Error("Kh\u00F4ng th\u1EC3 l\u01B0u settings.json", ex); }
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
            double totSec = Math.Max(0, TotalDuration > 0.05 ? TotalDuration : 0);
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

        // BO SUNG MAU DEN TUYEN & XAM DAM VAO BANG MAU CAI DAT
        public string[] AeroThemes { get; } = {
            "\u0110en Tuy\u1EC1n Nguy\u00EAn B\u1EA3n (Pure Pitch Black #050507)",
            "X\u00E1m \u0110\u1EADm T\u1ED1i Gi\u1EA3n (Dark Charcoal Gray #22262C)",
            "\u0110en Kh\u00F3i Windows 10 (Obsidian Glass)",
            "Xanh B\u0103ng Windows 7 (Aero Ice Blue)",
            "Xanh \u0110\u1EA1i D\u01B0\u01A1ng S\u00E2u (Deep Ocean Glass)",
            "Xanh Ng\u1ECDc L\u1EE5c B\u1EA3o (Emerald Glass)",
            "T\u00EDm Pha L\u00EA \u0110\u00EAm (Royal Amethyst)",
            "\u0110\u1ECF H\u1ED3ng Ng\u1ECDc (Crimson Ruby Glass)",
            "X\u00E1m B\u1EA1c Hi\u1EC7n \u0110\u1EA1i (Frosted Slate)"
        };
        private string _selectedAeroTheme = "\u0110en Kh\u00F3i Windows 10 (Obsidian Glass)";
        public string SelectedAeroTheme
        {
            get => _selectedAeroTheme;
            set { _selectedAeroTheme = value; OnPropertyChanged(); UpdateThemeBrushes(); SaveSettings(); }
        }

        public string[] AccentColorList { get; } = {
            "Xanh Windows 10 (#0078D4)",
            "Xanh Aero Cyan (#00B4FF)",
            "X\u00E1m Kim Lo\u1EA1i (#64748B)",
            "\u0110en Than Ch\u00EC (#3F3F46)",
            "Xanh Ng\u1ECDc L\u1EE5c B\u1EA3o (#10B981)",
            "T\u00EDm Hi\u1EC7n \u0110\u1EA1i (#8B5CF6)",
            "\u0110\u1ECF H\u1ED3ng Ruby (#F43F5E)",
            "V\u00E0ng H\u1ED5 Ph\u00E1ch (#F59E0B)"
        };
        private string _selectedAccentItem = "Xanh Windows 10 (#0078D4)";
        public string SelectedAccentItem
        {
            get => _selectedAccentItem;
            set { _selectedAccentItem = value; OnPropertyChanged(); UpdateThemeBrushes(); SaveSettings(); }
        }

        public string[] WaveformColorList { get; } = {
            "Xanh S\u00F3ng \u00C2m Aero (#00B4FF)",
            "X\u00E1m B\u1EA1c S\u00F3ng \u00C2m (#94A3B8)",
            "\u0110en Kh\u00F3i S\u00F3ng \u00C2m (#52525B)",
            "Xanh Windows 10 (#0078D4)",
            "Xanh L\u1EE5c Neon (#00E676)",
            "T\u00EDm S\u00F3ng \u00C2m (#A855F7)",
            "Cam Ho\u00E0ng H\u00F4n (#FB923C)",
            "Tr\u1EAFng B\u0103ng Tuy\u1EBFt (#E2E8F0)"
        };
        private string _selectedWaveformColorItem = "Xanh S\u00F3ng \u00C2m Aero (#00B4FF)";
        public string SelectedWaveformColorItem
        {
            get => _selectedWaveformColorItem;
            set { _selectedWaveformColorItem = value; OnPropertyChanged(); UpdateThemeBrushes(); SaveSettings(); }
        }

        public string[] SecondaryLineColorList { get; } = {
            "V\u00E0ng Tia Ch\u1EDBp (#FFD600)",
            "X\u00E1m B\u1EA1c S\u00E1ng (#CBD5E1)",
            "\u0110en Than (#71717A)",
            "Xanh Ng\u1ECDc S\u00E1ng (#00E5FF)",
            "Xanh L\u00E1 Neon (#00E676)",
            "H\u1ED3ng T\u00E2m \u0110i\u1EC3m (#FF4081)",
            "Tr\u1EAFng Tinh Khi\u1EBFt (#FFFFFF)"
        };
        private string _selectedSecondaryLineColorItem = "V\u00E0ng Tia Ch\u1EDBp (#FFD600)";
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
            string cardRgb = "080D14";
            if (SelectedAeroTheme.Contains("Pure Pitch Black")) { baseRgb = "050507"; cardRgb = "0A0A0D"; }
            else if (SelectedAeroTheme.Contains("Dark Charcoal Gray")) { baseRgb = "22262C"; cardRgb = "16191E"; }
            else if (SelectedAeroTheme.Contains("Windows 7")) baseRgb = "0D1F38";
            else if (SelectedAeroTheme.Contains("\u0110\u1EA1i D\u01B0\u01A1ng")) baseRgb = "071B2E";
            else if (SelectedAeroTheme.Contains("Ng\u1ECDc L\u1EE5c B\u1EA3o")) baseRgb = "092622";
            else if (SelectedAeroTheme.Contains("T\u00EDm")) baseRgb = "1B102E";
            else if (SelectedAeroTheme.Contains("\u0110\u1ECF")) baseRgb = "2B0D16";
            else if (SelectedAeroTheme.Contains("X\u00E1m B\u1EA1c")) baseRgb = "282E36";

            WindowGlassBrush = $"#{winHexA}{baseRgb}";
            CardGlassBrush = $"#{cardHexA}{cardRgb}";

            AccentBrush = ExtractHex(SelectedAccentItem, "#0078D4");
            WaveformBrush = ExtractHex(SelectedWaveformColorItem, "#00B4FF");
            SecondaryLineBrush = ExtractHex(SelectedSecondaryLineColorItem, "#FFD600");
            WindowBorderBrush = "#66" + AccentBrush.TrimStart('#');
        }

        private string _inputText = "Xin ch\u00E0o! \u0110\u00E2y l\u00E0 phi\u00EAn b\u1EA3n n\u00E2ng c\u1EA5p VoiceCraft 0.0.2 t\u00EDch h\u1EE3p VieNeu-TTS, t\u00EDnh n\u0103ng Nh\u00E1i gi\u1ECDng AI t\u1EEB file m\u1EABu v\u00E0 b\u1ED9 l\u1ECDc \u00E2m thanh EQ ri\u00EAng cho t\u1EEBng Model.";
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
            "Song ng\u1EEF Vi\u1EC7t - Anh (T\u1EF1 \u0111\u1ED9ng)",
            "Ti\u1EBFng Vi\u1EC7t - Mi\u1EC1n B\u1EAFc",
            "Ti\u1EBFng Vi\u1EC7t - Mi\u1EC1n Trung",
            "Ti\u1EBFng Vi\u1EC7t - Mi\u1EC1n Nam",
            "English - US (Chu\u1EA9n M\u1EF9 22.05kHz)"
        };
        private string _selectedVoice = "Song ng\u1EEF Vi\u1EC7t - Anh (T\u1EF1 \u0111\u1ED9ng)";
        public string SelectedVoice { get => _selectedVoice; set { _selectedVoice = value; OnPropertyChanged(); SaveSettings(); } }

        public string[] Genders { get; } = { "N\u1EEF (Female)", "Nam (Male)" };
        private string _selectedGender = "N\u1EEF (Female)";
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

        private string _connectionStatus = "\u0110ang ch\u1EDD...";
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

        private string _synthesisStatusText = "\u0110ang chu\u1EA9n b\u1ECB t\u1ED5ng h\u1EE3p...";
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
                    LoadAudioToTimeline(value.FilePath, false);
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

        private string _updateStatusText = $"Phi\u00EAn b\u1EA3n hi\u1EC7n t\u1EA1i: {UpdateService.CurrentAppVersion} (Nh\u1EA5n 'Ki\u1EC3m tra c\u1EADp nh\u1EADt' \u0111\u1EC3 ki\u1EC3m tra t\u1EEB GitHub)";
        public string UpdateStatusText { get => _updateStatusText; set { _updateStatusText = value; OnPropertyChanged(); } }

        private string _updateChangelog = "Ch\u01B0a c\u00F3 d\u1EEF li\u1EC7u b\u1EA3n c\u1EADp nh\u1EADt m\u1EDB;i.";
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
        public ICommand SwitchTabCommand { get; }
        public ICommand RefreshModelsCommand { get; }
        public ICommand AdjustSpeakerIdCommand { get; }
        public ICommand SelectCloneSampleCommand { get; }
        public ICommand PreviewCloneSampleCommand { get; }
        public ICommand ClearCloneSampleCommand { get; }
        public ICommand ResetEqCommand { get; }
        public ICommand ApplyAudioPresetCommand { get; }
        public ICommand ClearTextCommand { get; }
        public ICommand PasteTextCommand { get; }
        public ICommand OpenSelectedHistoryFileCommand { get; }
        public ICommand AdjustSpeedCommand { get; }
        public ICommand SetSpeedPresetCommand { get; }
        public ICommand AdjustPitchCommand { get; }
        public ICommand SetPitchPresetCommand { get; }
        public ICommand RestartEngineCommand { get; }
        public ICommand RepairModelsCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ClearCacheCommand { get; }
        public ICommand CheckUpdateCommand { get; }
        public ICommand DownloadUpdateCommand { get; }
        public ICommand OpenUpdateFolderCommand { get; }

        private async Task CheckForUpdateAsync()
        {
            IsCheckingUpdate = true;
            UpdateStatusText = "\u23F3 \u0110ang k\u1EBFt n\u1ED1i GitHub Releases API...";
            try
            {
                _latestUpdateInfo = await _updateService.CheckForUpdatesAsync(GitHubRepo);
                IsUpdateAvailable = _latestUpdateInfo.IsUpdateAvailable;
                UpdateChangelog = string.IsNullOrWhiteSpace(_latestUpdateInfo.ReleaseNotes)
                    ? "Kh\u00F4ng c\u00F3 ghi ch\u00FA ph\u00E1t h\u00E0nh."
                    : _latestUpdateInfo.ReleaseNotes;

                if (_latestUpdateInfo.IsUpdateAvailable)
                    UpdateStatusText = $"\uD83C\uDF89 Ph\u00E1t hi\u1EC7n phi\u00EAn b\u1EA3n m\u1EDBi: v{_latestUpdateInfo.LatestVersion} (Hi\u1EC7n t\u1EA1i: v{AppVersion}) - Nh\u1EA5n 'T\u1EA3i b\u1EA3n m\u1EDBi'!";
                else
                    UpdateStatusText = $"\u2714 B\u1EA1n \u0111ang s\u1EED d\u1EE5ng phi\u00EAn b\u1EA3n m\u1EDBi nh\u1EA5t (v{_latestUpdateInfo.LatestVersion}). V\u1EABn c\u00F3 th\u1EC3 nh\u1EA5n 'T\u1EA3i b\u1EA3n m\u1EDBi' \u0111\u1EC3 t\u1EA3i l\u1EA1i g\u00F3i Release.";
            }
            catch (Exception ex)
            {
                UpdateStatusText = "\u274C L\u1ED7i ki\u1EC3m tra c\u1EADp nh\u1EADt: " + ex.Message;
            }
            finally { IsCheckingUpdate = false; }
        }

        private async Task DownloadUpdateAsync()
        {
            if (_latestUpdateInfo == null || string.IsNullOrEmpty(_latestUpdateInfo.DownloadUrl)) return;
            IsDownloadingUpdate = true;
            UpdateProgress = 0;
            UpdateStatusText = $"\u23F3 \u0110ang t\u1EA3i b\u1EA3n c\u1EADp nh\u1EADt v{_latestUpdateInfo.LatestVersion}...";
            try
            {
                var progress = new Progress<double>(pct =>
                {
                    UpdateProgress = pct;
                    UpdateStatusText = $"\u23F3 \u0110ang t\u1EA3i g\u00F3i c\u1EADp nh\u1EADt: {pct:0.0}%";
                });
                _downloadedUpdateFilePath = await _updateService.DownloadUpdateAsync(_latestUpdateInfo, progress);
                UpdateStatusText = $"\u2705 \u0110\u00E3 t\u1EA3i xong: {Path.GetFileName(_downloadedUpdateFilePath)}";
                _updateService.OpenUpdateFolderOrFile(_downloadedUpdateFilePath);
            }
            catch (Exception ex)
            {
                UpdateStatusText = "\u274C L\u1ED7i t\u1EA3i b\u1EA3n c\u1EADp nh\u1EADt: " + ex.Message;
            }
            finally { IsDownloadingUpdate = false; }
        }

        private async Task InitializeEngineAsync()
        {
            ConnectionStatus = "\u0110ang kh\u1EDFi \u0111\u1ED9ng Engine...";
            ConnectionColor = "#FFB300";

            bool isReady = await _engineManager.StartEngineAsync();
            if (isReady)
            {
                ConnectionStatus = "S\u1EE7n s\u00E0ng (Studio v0.0.2)";
                ConnectionColor = "#00E676";
            }
            else
            {
                ConnectionStatus = "L\u1ED7i k\u1EBFt n\u1ED1i Engine";
                ConnectionColor = "#FF5252";
            }
        }

        private async Task SynthesizeAsync()
        {
            IsProcessing = true;
            SynthesisProgress = 5;
            SynthesisStatusText = "\u0110ang ph\u00E2n t\u00EDch ng\u1EEF c\u1EA3nh & n\u1EA1p Model...";
            ConnectionStatus = "\u0110ang t\u1ED5ng h\u1EE3p gi\u1ECDng n\u00F3i...";
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
                                SynthesisStatusText = $"\u0110ang x\u1EED l\u00FD v\u0103n b\u1EA3n v\u1EDBi Model: {SelectedModelFile}...";
                            }
                            else if (SynthesisProgress < 72)
                            {
                                SynthesisProgress += 3;
                                SynthesisStatusText = IsVoiceCloneEnabled
                                    ? $"\u0110ang nh\u00E1i gi\u1ECDng theo m\u1EABu ({CloneStrengthPercent})..."
                                    : "\u0110ang t\u1ED5ng h\u1EE3p s\u00F3ng \u00E2m n\u01A1-ron...";
                            }
                            else if (SynthesisProgress < 92)
                            {
                                SynthesisProgress += 1.5;
                                SynthesisStatusText = "\u0110ang x\u1EED l\u00FD h\u1EADu k\u1EF3 DSP & chu\u1EA9n h\u00F3a \u00E2m l\u01B0\u1EE3ng...";
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
                    bypass_dsp = BypassDsp || (!IsCustomEqEnabled && !IsVoiceCloneEnabled),
                    gender = SelectedGender,
                    speed = Speed,
                    pitch = Pitch,
                    bilingual_auto = BilingualAuto,
                    single_voice_mode = SingleVoiceMode,
                    clone_enabled = IsVoiceCloneEnabled,
                    clone_reference_path = CloneReferencePath,
                    clone_strength = CloneStrength,
                    vits_noise_scale = VitsNoiseScale,
                    eq_bass_db = IsCustomEqEnabled ? EqBassDb : 0.0,
                    eq_treble_db = IsCustomEqEnabled ? EqTrebleDb : 0.0,
                    de_hiss_cutoff = IsCustomEqEnabled ? DeHissCutoff : 11500.0
                };
                var response = await _httpClient.PostAsJsonAsync("http://127.0.0.1:8000/synthesize", payload);
                cts.Cancel();

                if (response.IsSuccessStatusCode)
                {
                    SynthesisProgress = 96;
                    byte[] audioBytes = await response.Content.ReadAsByteArrayAsync();
                    string modTag = IsAutoModelSelected
                        ? (SelectedGender.Contains("Nam") ? "AutoNam" : "AutoNu")
                        : Path.GetFileNameWithoutExtension(SelectedModelFile.Replace("[", "").Replace("]", "")).Split(' ')[0];
                    string cloneTag = (IsVoiceCloneEnabled && !string.IsNullOrEmpty(CloneReferencePath)) ? "_CLONE" : "";
                    string fileName = $"VC_{modTag}{cloneTag}_{DateTime.Now:HHmmss}.wav";
                    string savedPath = Path.Combine(_outputDir, fileName);
                    await File.WriteAllBytesAsync(savedPath, audioBytes);

                    SynthesisProgress = 100;
                    await Task.Delay(100);

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

                    ConnectionStatus = "S\u1EE7n s\u00E0ng (Studio v0.0.2)";
                    ConnectionColor = "#00E676";
                }
                else
                {
                    ConnectionStatus = "C\u1EA3nh b\u00E1o t\u1EEB Engine";
                    ConnectionColor = "#FF5252";
                    string errDetail = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"L\u1ED7i t\u1EEB Engine ({response.StatusCode}):\n{errDetail}", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                cts.Cancel();
                ConnectionStatus = "L\u1ED7i k\u1EBFt n\u1ED1i Engine";
                ConnectionColor = "#FF5252";
                MessageBox.Show("Kh\u00F4ng th\u1EC3 k\u1EBFt n\u1ED1i t\u1EDB;i TTS Engine!\nChi ti\u1EBFt: " + ex.Message, "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { IsProcessing = false; }
        }

        private void SaveFile(object obj)
        {
            if (string.IsNullOrEmpty(_currentAudioPath) || !File.Exists(_currentAudioPath)) return;
            var dialog = new SaveFileDialog { Filter = "Wave Audio|*.wav", Title = "L\u01B0u file \u00E2m thanh", FileName = Path.GetFileName(_currentAudioPath) };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    File.Copy(_currentAudioPath, dialog.FileName, true);
                    MessageBox.Show("\u0110\u00E3 l\u01B0u file \u00E2m thanh th\u00E0nh c\u00F4ng!", "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("L\u1ED7i khi l\u01B0u file: " + ex.Message, "VoiceCraft", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // NANG CAP GENERATE WAVEFORM DUNG AUDIOFILEREADER (DOC DUOC MOI CHUAN .WAV VA .MP3)
        private PointCollection GenerateWaveform(string filePath)
        {
            var points = new PointCollection();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return points;

            double width = 1000, height = 100, midY = height / 2;

            try
            {
                using (var reader = new AudioFileReader(filePath))
                {
                    int bytesPerSample = Math.Max(4, reader.WaveFormat.BitsPerSample / 8);
                    long totalSamples = Math.Max(1, reader.Length / bytesPerSample);
                    int samplesPerPixel = Math.Max(1, (int)(totalSamples / width));
                    float[] sampleBuffer = new float[Math.Max(64, samplesPerPixel)];

                    var topPoints = new List<Point>();
                    var bottomPoints = new List<Point>();

                    int x = 0, samplesRead;
                    while ((samplesRead = reader.Read(sampleBuffer, 0, sampleBuffer.Length)) > 0 && x <= width)
                    {
                        float max = 0;
                        for (int i = 0; i < samplesRead; i++)
                        {
                            float val = Math.Abs(sampleBuffer[i]);
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
            }
            catch (Exception ex) { Logger.Error("L\u1ED7i v\u1EBD waveform UI", ex); }

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