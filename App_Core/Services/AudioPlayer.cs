using NAudio.Wave;
using System;

namespace VoiceCraft.App_Core.Services
{
    public class AudioPlayer : IDisposable
    {
        private WaveOutEvent _outputDevice;
        private AudioFileReader _audioFile;
        private float _volume = 1.0f;

        public event EventHandler PlaybackStopped;

        public bool IsPlaying => _outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing;
        public TimeSpan TotalTime => _audioFile?.TotalTime ?? TimeSpan.Zero;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_audioFile != null) _audioFile.Volume = _volume;
            }
        }

        public TimeSpan CurrentTime
        {
            get => _audioFile?.CurrentTime ?? TimeSpan.Zero;
            set
            {
                if (_audioFile != null && value >= TimeSpan.Zero && value <= _audioFile.TotalTime)
                    _audioFile.CurrentTime = value;
            }
        }

        public void Load(string filePath)
        {
            DisposeOutDevice();
            try
            {
                _outputDevice = new WaveOutEvent();
                _outputDevice.PlaybackStopped += (s, e) => PlaybackStopped?.Invoke(this, EventArgs.Empty);
                _audioFile = new AudioFileReader(filePath) { Volume = _volume };
                _outputDevice.Init(_audioFile);
            }
            catch (Exception ex)
            {
                Logger.Error($"Lỗi load âm thanh: {filePath}", ex);
            }
        }

        public void Play()
        {
            if (_outputDevice == null || _audioFile == null) return;
            if (_audioFile.CurrentTime >= _audioFile.TotalTime - TimeSpan.FromMilliseconds(100))
                _audioFile.CurrentTime = TimeSpan.Zero;
            _outputDevice.Play();
        }

        public void Pause() => _outputDevice?.Pause();

        public void Stop()
        {
            _outputDevice?.Stop();
            if (_audioFile != null) _audioFile.CurrentTime = TimeSpan.Zero;
        }

        private void DisposeOutDevice()
        {
            try { _outputDevice?.Stop(); } catch { }
            _outputDevice?.Dispose();
            _outputDevice = null;
            _audioFile?.Dispose();
            _audioFile = null;
        }

        public void Dispose() => DisposeOutDevice();
    }
}