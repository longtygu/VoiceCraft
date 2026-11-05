using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VoiceCraft.App_Core.Models
{
    public class HistoryItem
    {
        public string DisplayText { get; set; }
        public string FilePath { get; set; }
        public double DurationSeconds { get; set; }
        public string DurationPopupText { get; set; } = "⏱️ Độ dài: 00:00.0";
    }

    public class TimelineClipItem : INotifyPropertyChanged
    {
        public string ClipId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string SourceFilePath { get; set; }

        private string _title;
        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }

        private int _layerIndex;
        public int LayerIndex
        {
            get => _layerIndex;
            set { _layerIndex = Math.Max(0, value); OnPropertyChanged(); }
        }

        private double _timelineStartSec;
        public double TimelineStartSec
        {
            get => _timelineStartSec;
            set { _timelineStartSec = Math.Max(0.0, Math.Round(value, 3)); OnPropertyChanged(); }
        }

        private double _sourceTrimStartSec;
        public double SourceTrimStartSec
        {
            get => _sourceTrimStartSec;
            set { _sourceTrimStartSec = Math.Max(0.0, Math.Round(value, 3)); OnPropertyChanged(); }
        }

        private double _durationSec = 3.0;
        public double DurationSec
        {
            get => _durationSec;
            set { _durationSec = Math.Max(0.1, Math.Round(value, 3)); OnPropertyChanged(); }
        }

        public double SourceTotalDurationSec { get; set; } = 3.0;

        // Mảng biên độ đỉnh gốc (PeaksPerSecond = 50) để khi thu gọn 2 đầu chỉ cắt ngắn sóng âm chứ KHÔNG thu nhỏ theo thị giác
        public float[] SourcePeaks { get; set; } = Array.Empty<float>();
        public int PeaksPerSecond { get; set; } = 50;

        // Mẫu âm thanh gốc (22050Hz Mono float32) và bộ đệm sau khi áp DSP để trộn đa Layer tức thì
        public float[] RawSamples22k { get; set; }
        public float[] ProcessedFullSamples22k { get; set; }
        public string LastDspSignature { get; set; } = "";

        public PointCollection MiniWaveform { get; set; } = new PointCollection();

        // Thông số DSP riêng cho từng thành phần (Clip)
        public double Semitones { get; set; } = 0.0;
        public double Speed { get; set; } = 1.0;
        public bool CloneEnabled { get; set; } = false;
        public string CloneRefPath { get; set; } = "";
        public double CloneStrength { get; set; } = 0.80;
        public double HighpassHz { get; set; } = 65.0;
        public double Band80Hz { get; set; } = 0.0;
        public double Band220Hz { get; set; } = 0.0;
        public double Band1kHz { get; set; } = 0.0;
        public double Band3k5Hz { get; set; } = 0.0;
        public double Band8kHz { get; set; } = 0.0;
        public double LowpassHz { get; set; } = 11000.0;

        public bool HasCustomEq =>
            Math.Abs(Band80Hz) >= 0.1 ||
            Math.Abs(Band220Hz) >= 0.1 ||
            Math.Abs(Band1kHz) >= 0.1 ||
            Math.Abs(Band3k5Hz) >= 0.1 ||
            Math.Abs(Band8kHz) >= 0.1 ||
            Math.Abs(HighpassHz - 65.0) >= 2.0 ||
            Math.Abs(LowpassHz - 11000.0) >= 100.0;

        public bool HasAnyDsp =>
            HasCustomEq ||
            Math.Abs(Semitones) >= 0.1 ||
            Math.Abs(Speed - 1.0) >= 0.02 ||
            (CloneEnabled && !string.IsNullOrEmpty(CloneRefPath));

        public string GetDspSignature() =>
            $"{Semitones:0.00}|{Speed:0.00}|{CloneEnabled}|{CloneRefPath}|{CloneStrength:0.00}|{HighpassHz:0}|{Band80Hz:0.0}|{Band220Hz:0.0}|{Band1kHz:0.0}|{Band3k5Hz:0.0}|{Band8kHz:0.0}|{LowpassHz:0}";

        public TimelineClipItem CloneCopy(double newTimelineStart)
        {
            return new TimelineClipItem
            {
                SourceFilePath = this.SourceFilePath,
                Title = this.Title,
                LayerIndex = this.LayerIndex,
                TimelineStartSec = Math.Max(0.0, newTimelineStart),
                SourceTrimStartSec = this.SourceTrimStartSec,
                DurationSec = this.DurationSec,
                SourceTotalDurationSec = this.SourceTotalDurationSec,
                SourcePeaks = this.SourcePeaks,
                PeaksPerSecond = this.PeaksPerSecond,
                RawSamples22k = this.RawSamples22k,
                ProcessedFullSamples22k = this.ProcessedFullSamples22k,
                LastDspSignature = this.LastDspSignature,
                MiniWaveform = this.MiniWaveform != null ? new PointCollection(this.MiniWaveform) : new PointCollection(),
                Semitones = this.Semitones,
                Speed = this.Speed,
                CloneEnabled = this.CloneEnabled,
                CloneRefPath = this.CloneRefPath,
                CloneStrength = this.CloneStrength,
                HighpassHz = this.HighpassHz,
                Band80Hz = this.Band80Hz,
                Band220Hz = this.Band220Hz,
                Band1kHz = this.Band1kHz,
                Band3k5Hz = this.Band3k5Hz,
                Band8kHz = this.Band8kHz,
                LowpassHz = this.LowpassHz
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}