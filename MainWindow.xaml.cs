using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using VoiceCraft.App_Core.ViewModels;

namespace VoiceCraft
{
    public partial class MainWindow : Window
    {
        private bool _isDraggingWaveform;
        private SettingsWindow _settingsWindow;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            EnableAeroGlassBlur();

            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += ViewModel_PropertyChanged;
            }
            UpdateMainPlayheadVisual();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            if (_settingsWindow != null)
            {
                try { _settingsWindow.Close(); } catch { }
                _settingsWindow = null;
            }
        }

        // Mở cửa sổ Cài đặt riêng biệt từ nút Icon Bánh răng (⚙) trên thanh tiêu đề
        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow != null && _settingsWindow.IsLoaded)
            {
                if (_settingsWindow.WindowState == WindowState.Minimized)
                    _settingsWindow.WindowState = WindowState.Normal;
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow
            {
                Owner = this,
                DataContext = this.DataContext
            };
            _settingsWindow.Closed += (s, args) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPosition) ||
                e.PropertyName == nameof(MainViewModel.TotalDuration))
            {
                Dispatcher.InvokeAsync(UpdateMainPlayheadVisual);
            }
        }

        private void UpdateMainPlayheadVisual()
        {
            if (DataContext is not MainViewModel vm) return;
            double width = WaveformContainer.ActualWidth;
            double height = WaveformContainer.ActualHeight;
            if (width <= 1) return;

            MainPlayheadLine.Y2 = Math.Max(80, height);
            SecondaryHoverLine.Y2 = Math.Max(80, height);

            double ratio = vm.TotalDuration > 0.05 ? Math.Clamp(vm.CurrentPosition / vm.TotalDuration, 0.0, 1.0) : 0.0;
            Canvas.SetLeft(MainPlayheadLine, ratio * width);
        }

        private void WaveformContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMainPlayheadVisual();
        }

        private void WaveformContainer_MouseEnter(object sender, MouseEventArgs e)
        {
            SecondaryHoverLine.Visibility = Visibility.Visible;
            SecondaryTimePopup.Visibility = Visibility.Visible;
        }

        private void WaveformContainer_MouseLeave(object sender, MouseEventArgs e)
        {
            _isDraggingWaveform = false;
            SecondaryHoverLine.Visibility = Visibility.Collapsed;
            SecondaryTimePopup.Visibility = Visibility.Collapsed;
        }

        private void WaveformContainer_MouseMove(object sender, MouseEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            double width = WaveformContainer.ActualWidth;
            if (width <= 1) return;

            Point pos = e.GetPosition(WaveformContainer);
            double clampedX = Math.Clamp(pos.X, 0.0, width);
            double ratio = clampedX / width;

            // Di chuyển Vạch phụ theo đúng vị trí chuột
            Canvas.SetLeft(SecondaryHoverLine, clampedX);

            // Đặt vị trí nhãn bộ đếm thời gian phụ đi kèm vạch phụ (tránh tràn lề phải)
            double popupWidth = SecondaryTimePopup.ActualWidth > 10 ? SecondaryTimePopup.ActualWidth : 58;
            double popupLeft = (clampedX + popupWidth + 8 < width) ? (clampedX + 6) : Math.Max(2, clampedX - popupWidth - 6);
            Canvas.SetLeft(SecondaryTimePopup, popupLeft);

            // Cập nhật thời gian của bộ đếm phụ
            double hoverSeconds = vm.TotalDuration > 0.05 ? (ratio * vm.TotalDuration) : 0.0;
            vm.HoverTimeText = MainViewModel.FormatTimeCode(hoverSeconds);

            // Nếu người dùng đang giữ chuột trái kéo trên sóng âm -> tua trực tiếp Vạch chính theo Vạch phụ
            if (_isDraggingWaveform && e.LeftButton == MouseButtonState.Pressed && vm.TotalDuration > 0.05 && !vm.IsProcessing)
            {
                vm.CurrentPosition = hoverSeconds;
                UpdateMainPlayheadVisual();
            }
        }

        private void WaveformContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;
            double width = WaveformContainer.ActualWidth;
            if (width <= 1) return;

            _isDraggingWaveform = true;
            WaveformContainer.CaptureMouse();

            Point pos = e.GetPosition(WaveformContainer);
            double ratio = Math.Clamp(pos.X / width, 0.0, 1.0);
            if (vm.TotalDuration > 0.05)
            {
                vm.CurrentPosition = ratio * vm.TotalDuration;
                UpdateMainPlayheadVisual();
            }
        }

        private void WaveformContainer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingWaveform)
            {
                _isDraggingWaveform = false;
                WaveformContainer.ReleaseMouseCapture();
            }
        }

        private void EnableAeroGlassBlur()
        {
            try
            {
                var windowHelper = new WindowInteropHelper(this);
                var accent = new AccentPolicy
                {
                    AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND,
                    AccentFlags = 2,
                    GradientColor = 0x99101824
                };

                int accentStructSize = Marshal.SizeOf(accent);
                IntPtr accentPtr = Marshal.AllocHGlobal(accentStructSize);
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    SizeOfData = accentStructSize,
                    Data = accentPtr
                };

                SetWindowCompositionAttribute(windowHelper.Handle, ref data);
                Marshal.FreeHGlobal(accentPtr);
            }
            catch { }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                Maximize_Click(sender, e);
            }
            else if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        #region Native Win32 Aero Blur Interop
        [DllImport("user32.dll")]
        internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        internal enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        internal enum WindowCompositionAttribute
        {
            WCA_ACCENT_POLICY = 19
        }
        #endregion

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }
    }
}