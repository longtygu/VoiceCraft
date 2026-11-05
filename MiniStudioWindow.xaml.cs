using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using VoiceCraft.App_Core.Models;
using VoiceCraft.App_Core.ViewModels;

namespace VoiceCraft
{
    public partial class MiniStudioWindow : Window
    {
        private enum InteractionMode { None, ScrubPlayhead, PanViewport, MoveClip, TrimClipLeft, TrimClipRight }
        private InteractionMode _mode = InteractionMode.None;

        private double _pixelsPerSecond = 60.0;
        private const double MinPixelsPerSecond = 1.5;
        private const double MaxPixelsPerSecond = 300.0;
        private const double LayerHeight = 76.0;
        private const int MaxPolygonSamples = 420;

        private bool _isHandPanModeEnabled = false;
        private bool _isUpdatingZoomSlider = false;

        private TimelineClipItem _activeDragClip;
        private Border _activeDragBorder;
        private Polygon _activeDragWavePolygon;
        private TextBlock _activeDragTitleText;

        private Point _mouseDownSurfacePos;
        private Point _panStartViewportMousePos;
        private double _panStartHorizontalOffset;
        private double _panStartVerticalOffset;

        private double _origTimelineStartSec;
        private double _origSourceTrimStartSec;
        private double _origDurationSec;
        private Point _listDragStartPoint;

        public MiniStudioWindow()
        {
            InitializeComponent();
            this.Loaded += MiniStudioWindow_Loaded;
            this.Closed += MiniStudioWindow_Closed;
        }

        private void MiniStudioWindow_Loaded(object sender, RoutedEventArgs e)
        {
            EnableAeroGlassBlur();
            if (DataContext is MainViewModel vm)
            {
                vm.StopHeadlessDemo();
                vm.PropertyChanged += ViewModel_PropertyChanged;
                vm.TimelineClips.CollectionChanged += TimelineClips_CollectionChanged;
                if (vm.HistoryList.Count == 0)
                {
                    vm.ScanOutputRecordings();
                }
                if (vm.TimelineClips.Count == 0 && vm.SelectedHistoryItem != null && File.Exists(vm.SelectedHistoryItem.FilePath))
                {
                    vm.StudioLayerCount = 1;
                    vm.AddRecordingToTimeline(vm.SelectedHistoryItem.FilePath, 0, 0.0);
                }
                else
                {
                    vm.RebuildMasterCompositeTimelineAudio();
                }
            }
            AutoFitTimelineZoom();
            this.Focus();
        }

        private void MiniStudioWindow_Closed(object sender, EventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged -= ViewModel_PropertyChanged;
                vm.TimelineClips.CollectionChanged -= TimelineClips_CollectionChanged;
                vm.StopHeadlessDemo();
            }
        }

        private void TimelineClips_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add)
                {
                    AutoFitTimelineZoomIfOverflowing();
                }
                RenderAllTimelineVisuals();
            });
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPosition) ||
                e.PropertyName == nameof(MainViewModel.TotalDuration))
            {
                Dispatcher.InvokeAsync(UpdatePlayheadVisuals);
            }
            else if (e.PropertyName == nameof(MainViewModel.StudioLayerCount) ||
                     e.PropertyName == nameof(MainViewModel.SelectedTimelineClip) ||
                     e.PropertyName == nameof(MainViewModel.StudioSemitones) ||
                     e.PropertyName == nameof(MainViewModel.StudioSpeed) ||
                     e.PropertyName == nameof(MainViewModel.StudioCloneEnabled) ||
                     e.PropertyName == nameof(MainViewModel.StudioBand80Hz) ||
                     e.PropertyName == nameof(MainViewModel.StudioBand220Hz) ||
                     e.PropertyName == nameof(MainViewModel.StudioBand1kHz) ||
                     e.PropertyName == nameof(MainViewModel.StudioBand3k5Hz) ||
                     e.PropertyName == nameof(MainViewModel.StudioBand8kHz) ||
                     e.PropertyName == nameof(MainViewModel.StudioHighpassHz) ||
                     e.PropertyName == nameof(MainViewModel.StudioLowpassHz))
            {
                if (_mode == InteractionMode.None)
                {
                    Dispatcher.InvokeAsync(RenderAllTimelineVisuals);
                }
            }
        }

        private void SetPixelsPerSecond(double newPps, double? anchorMouseViewportX = null)
        {
            double oldPps = _pixelsPerSecond;
            double clamped = Math.Clamp(newPps, MinPixelsPerSecond, MaxPixelsPerSecond);
            if (Math.Abs(clamped - oldPps) < 0.001) return;

            double anchorSec = 0.0;
            if (anchorMouseViewportX.HasValue && TimelineScrollViewer != null)
            {
                anchorSec = (TimelineScrollViewer.HorizontalOffset + anchorMouseViewportX.Value) / oldPps;
            }

            _pixelsPerSecond = clamped;

            _isUpdatingZoomSlider = true;
            if (TimelineZoomSlider != null)
                TimelineZoomSlider.Value = _pixelsPerSecond;
            if (ZoomLevelText != null)
                ZoomLevelText.Text = _pixelsPerSecond < 10.0 ? $"{_pixelsPerSecond:0.0} px/s" : $"{_pixelsPerSecond:0} px/s";
            _isUpdatingZoomSlider = false;

            RenderAllTimelineVisuals();

            if (anchorMouseViewportX.HasValue && TimelineScrollViewer != null)
            {
                double newOffset = Math.Max(0.0, (anchorSec * _pixelsPerSecond) - anchorMouseViewportX.Value);
                TimelineScrollViewer.ScrollToHorizontalOffset(newOffset);
            }
        }

        private void AutoFitTimelineZoom()
        {
            if (DataContext is not MainViewModel vm || vm.TimelineClips.Count == 0)
            {
                SetPixelsPerSecond(60.0);
                return;
            }

            double maxClipEndSec = vm.TimelineClips.Max(c => c.TimelineStartSec + c.DurationSec);
            double viewportW = TimelineScrollViewer != null && TimelineScrollViewer.ActualWidth > 100
                ? TimelineScrollViewer.ActualWidth - 40.0
                : 620.0;

            if (maxClipEndSec > 0.5)
            {
                double idealPps = Math.Clamp(viewportW / (maxClipEndSec * 1.12), MinPixelsPerSecond, 90.0);
                SetPixelsPerSecond(idealPps);
                TimelineScrollViewer?.ScrollToHorizontalOffset(0);
            }
        }

        private void AutoFitTimelineZoomIfOverflowing()
        {
            if (DataContext is not MainViewModel vm || vm.TimelineClips.Count == 0) return;
            double maxClipEndSec = vm.TimelineClips.Max(c => c.TimelineStartSec + c.DurationSec);
            double viewportW = TimelineScrollViewer != null && TimelineScrollViewer.ActualWidth > 100
                ? TimelineScrollViewer.ActualWidth - 40.0
                : 620.0;

            if (maxClipEndSec * _pixelsPerSecond > viewportW * 1.4)
            {
                double idealPps = Math.Clamp(viewportW / (maxClipEndSec * 1.12), MinPixelsPerSecond, 90.0);
                SetPixelsPerSecond(idealPps);
            }
        }

        private void RenderAllTimelineVisuals()
        {
            RenderDynamicLayerHeadersAndLanes();
            RenderTimeRuler();
            RenderCapCutTimelineClips();
            UpdatePlayheadVisuals();
        }

        // ==================== CTRL + LĂN CHUỘT THU PHÓNG & LĂN CHUỘT DI CHUYỂN TRỤC THỜI GIAN ====================
        private void TimelineScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (TimelineScrollViewer == null) return;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // Ctrl + Lăn chuột: Thu phóng mượt mà giữ nguyên điểm âm thanh ngay dưới trỏ chuột
                Point mouseInViewport = e.GetPosition(TimelineScrollViewer);
                double zoomFactor = e.Delta > 0 ? 1.22 : (1.0 / 1.22);
                SetPixelsPerSecond(_pixelsPerSecond * zoomFactor, Math.Max(0.0, mouseInViewport.X));

                if (DataContext is MainViewModel vm)
                {
                    vm.StudioStatusText = $"🔍 Tỷ lệ thu phóng Timeline: {_pixelsPerSecond:0.0} px/giây (Ctrl + Lăn chuột để thu phóng, Giữ Chuột phải kéo để di chuyển)";
                }
                e.Handled = true;
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                // Shift + Lăn chuột: Cuộn dọc giữa các Layer
                double newVert = Math.Max(0.0, TimelineScrollViewer.VerticalOffset - (e.Delta * 0.5));
                TimelineScrollViewer.ScrollToVerticalOffset(newVert);
                e.Handled = true;
            }
            else
            {
                // Lăn chuột thường: Trượt ngang dọc theo trục thời gian
                double newHoriz = Math.Max(0.0, TimelineScrollViewer.HorizontalOffset - (e.Delta * 0.85));
                TimelineScrollViewer.ScrollToHorizontalOffset(newHoriz);
                e.Handled = true;
            }
        }

        private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (RulerScrollViewer != null)
                RulerScrollViewer.ScrollToHorizontalOffset(e.HorizontalOffset);
            if (LayerHeadersScrollViewer != null)
                LayerHeadersScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
        }

        private void HandPanModeBtn_Click(object sender, RoutedEventArgs e)
        {
            _isHandPanModeEnabled = !_isHandPanModeEnabled;
            if (HandPanModeBtn != null)
            {
                HandPanModeBtn.Content = _isHandPanModeEnabled ? "✋ Kéo khung nhìn: BẬT" : "✋ Kéo khung nhìn: Tắt";
                HandPanModeBtn.Foreground = _isHandPanModeEnabled
                    ? new SolidColorBrush(Color.FromRgb(0, 230, 118))
                    : new SolidColorBrush(Color.FromRgb(128, 216, 255));
            }
            if (CapCutTrackSurface != null)
            {
                CapCutTrackSurface.Cursor = _isHandPanModeEnabled ? Cursors.SizeAll : Cursors.Arrow;
            }
            if (DataContext is MainViewModel vm)
            {
                vm.StudioStatusText = _isHandPanModeEnabled
                    ? "✋ Đã bật chế độ Kéo khung nhìn tự do (Nhấn giữ chuột trái kéo để trượt Timeline)."
                    : "🖱️ Đã tắt chế độ Kéo khung nhìn (Bạn vẫn có thể giữ Chuột phải hoặc Chuột giữa để kéo tự do bất cứ lúc nào).";
            }
        }

        private void ZoomFitButton_Click(object sender, RoutedEventArgs e) => AutoFitTimelineZoom();
        private void ZoomInButton_Click(object sender, RoutedEventArgs e) => SetPixelsPerSecond(_pixelsPerSecond * 1.30, (TimelineScrollViewer?.ActualWidth ?? 600) * 0.5);
        private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => SetPixelsPerSecond(_pixelsPerSecond / 1.30, (TimelineScrollViewer?.ActualWidth ?? 600) * 0.5);

        private void TimelineZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _isUpdatingZoomSlider) return;
            SetPixelsPerSecond(e.NewValue, (TimelineScrollViewer?.ActualWidth ?? 600) * 0.5);
        }

        // ==================== PHÍM TẮT CHUẨN CAPCUT PC ====================
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            if (e.Key == Key.Space)
            {
                if (DataContext is MainViewModel vm && !vm.IsProcessing)
                {
                    vm.ToggleMasterCompositePlayPause();
                    e.Handled = true;
                }
                return;
            }
            Window_KeyDown(sender, e);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.C)
                {
                    vm.CopySelectedClip();
                    e.Handled = true;
                }
                else if (e.Key == Key.V)
                {
                    vm.PasteClipAtPlayhead();
                    RenderAllTimelineVisuals();
                    e.Handled = true;
                }
                else if (e.Key == Key.B)
                {
                    vm.SplitSelectedClipAtPlayhead();
                    RenderAllTimelineVisuals();
                    e.Handled = true;
                }
                else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
                {
                    AutoFitTimelineZoom();
                    e.Handled = true;
                }
                else if (e.Key == Key.OemPlus || e.Key == Key.Add)
                {
                    ZoomInButton_Click(sender, null);
                    e.Handled = true;
                }
                else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
                {
                    ZoomOutButton_Click(sender, null);
                    e.Handled = true;
                }
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.Key == Key.S)
                {
                    vm.SplitSelectedClipAtPlayhead();
                    RenderAllTimelineVisuals();
                    e.Handled = true;
                }
                else if (e.Key == Key.Delete || e.Key == Key.Back)
                {
                    vm.DeleteSelectedClip();
                    RenderAllTimelineVisuals();
                    e.Handled = true;
                }
                else if (e.Key == Key.Space)
                {
                    vm.ToggleMasterCompositePlayPause();
                    e.Handled = true;
                }
            }
        }

        private void AddLayerButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.StudioLayerCount++;
                vm.StudioStatusText = $"➕ Đã thêm Layer {vm.StudioLayerCount} (Kéo thả bản ghi vào Layer {vm.StudioLayerCount} để trộn âm thanh).";
                RenderAllTimelineVisuals();
            }
        }

        private void AddRecordingToCurrentLayer_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.SelectedHistoryItem != null && File.Exists(vm.SelectedHistoryItem.FilePath))
            {
                int targetLayer = vm.SelectedTimelineClip?.LayerIndex ?? 0;
                vm.AddRecordingToTimeline(vm.SelectedHistoryItem.FilePath, targetLayer, -1.0);
                AutoFitTimelineZoomIfOverflowing();
                RenderAllTimelineVisuals();
            }
        }

        private void AddRecordingAsNewLayer_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.SelectedHistoryItem != null && File.Exists(vm.SelectedHistoryItem.FilePath))
            {
                int newLayerIndex = vm.TimelineClips.Count == 0 ? 0 : vm.StudioLayerCount;
                if (newLayerIndex >= vm.StudioLayerCount)
                    vm.StudioLayerCount = newLayerIndex + 1;
                vm.AddRecordingToTimeline(vm.SelectedHistoryItem.FilePath, newLayerIndex, 0.0);
                AutoFitTimelineZoomIfOverflowing();
                RenderAllTimelineVisuals();
            }
        }

        private void MasterPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm) vm.PlayMasterCompositeAudio();
        }

        private void SplitClipButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SplitSelectedClipAtPlayhead();
                RenderAllTimelineVisuals();
            }
        }

        private void CopyClipButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm) vm.CopySelectedClip();
        }

        private void PasteClipButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PasteClipAtPlayhead();
                RenderAllTimelineVisuals();
            }
        }

        private void DeleteClipButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.DeleteSelectedClip();
                RenderAllTimelineVisuals();
            }
        }

        private void ClearAllTimelineButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ClearTimelineCommand.Execute(null);
                RenderAllTimelineVisuals();
            }
        }

        // ==================== 1. DỰNG DANH SÁCH LAYER ĐỘNG ====================
        private void RenderDynamicLayerHeadersAndLanes()
        {
            if (LayerHeadersStack == null || LayerLanesBackgroundCanvas == null || DataContext is not MainViewModel vm) return;

            int maxClipLayer = vm.TimelineClips.Count > 0 ? vm.TimelineClips.Max(c => c.LayerIndex) + 1 : 1;
            if (vm.StudioLayerCount < maxClipLayer)
                vm.StudioLayerCount = maxClipLayer;
            int layerCount = Math.Max(1, vm.StudioLayerCount);

            LayerHeadersStack.Children.Clear();
            LayerLanesBackgroundCanvas.Children.Clear();

            double maxSec = 30.0;
            if (vm.TimelineClips.Count > 0)
            {
                maxSec = Math.Max(25.0, vm.TimelineClips.Max(c => c.TimelineStartSec + c.DurationSec) * 1.25 + 10.0);
            }
            double viewportW = TimelineScrollViewer != null && TimelineScrollViewer.ActualWidth > 100 ? TimelineScrollViewer.ActualWidth : 900.0;
            double totalWidth = Math.Max(viewportW + 200.0, maxSec * _pixelsPerSecond);
            double totalHeight = (layerCount * LayerHeight) + 52.0;

            CapCutTrackSurface.Width = totalWidth;
            CapCutTrackSurface.Height = totalHeight;
            TimeRulerCanvas.Width = totalWidth;
            CapCutPlayheadLine.Y2 = totalHeight;
            CapCutHoverLine.Y2 = totalHeight;

            for (int i = 0; i < layerCount; i++)
            {
                int capturedLayer = i;
                var hdrBorder = new Border
                {
                    Height = LayerHeight,
                    Background = new SolidColorBrush(i % 2 == 0 ? Color.FromArgb(65, 14, 28, 48) : Color.FromArgb(65, 10, 20, 36)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 6, 6, 6)
                };

                var hdrGrid = new Grid();
                hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txtStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txtStack.Children.Add(new TextBlock
                {
                    Text = $"🎵 Layer {i + 1}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 12
                });
                int clipCountOnLayer = vm.TimelineClips.Count(c => c.LayerIndex == i);
                txtStack.Children.Add(new TextBlock
                {
                    Text = $"{clipCountOnLayer} thành phần",
                    Foreground = new SolidColorBrush(Color.FromRgb(144, 180, 212)),
                    FontSize = 10
                });
                Grid.SetColumn(txtStack, 0);
                hdrGrid.Children.Add(txtStack);

                if (layerCount > 1)
                {
                    var delLayerBtn = new Button
                    {
                        Content = "✕",
                        Width = 20,
                        Height = 20,
                        FontSize = 9.5,
                        Style = (Style)Application.Current.FindResource("MiniChipBtn"),
                        VerticalAlignment = VerticalAlignment.Top,
                        ToolTip = $"Xoá Layer {i + 1}"
                    };
                    delLayerBtn.Click += (s, e) =>
                    {
                        vm.RemoveLayerAt(capturedLayer);
                        RenderAllTimelineVisuals();
                    };
                    Grid.SetColumn(delLayerBtn, 1);
                    hdrGrid.Children.Add(delLayerBtn);
                }

                hdrBorder.Child = hdrGrid;
                LayerHeadersStack.Children.Add(hdrBorder);

                double laneTop = i * LayerHeight;
                var laneRect = new Rectangle
                {
                    Width = totalWidth,
                    Height = LayerHeight,
                    Fill = new SolidColorBrush(i % 2 == 0 ? Color.FromArgb(24, 0, 180, 255) : Color.FromArgb(14, 0, 230, 118))
                };
                Canvas.SetLeft(laneRect, 0);
                Canvas.SetTop(laneRect, laneTop);
                LayerLanesBackgroundCanvas.Children.Add(laneRect);

                var sepLine = new Line
                {
                    X1 = 0, Y1 = laneTop + LayerHeight,
                    X2 = totalWidth, Y2 = laneTop + LayerHeight,
                    Stroke = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                    StrokeThickness = 1
                };
                LayerLanesBackgroundCanvas.Children.Add(sepLine);
            }

            var addZoneHeader = new Border
            {
                Height = 48,
                Background = new SolidColorBrush(Color.FromArgb(40, 0, 230, 118)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 230, 118)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = "➕ Thêm Layer",
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            addZoneHeader.MouseLeftButtonDown += (s, e) => AddLayerButton_Click(s, null);
            LayerHeadersStack.Children.Add(addZoneHeader);

            double addZoneTop = layerCount * LayerHeight;
            var addZoneHint = new TextBlock
            {
                Text = $"➕ Kéo thả bản ghi vào dòng này để tự động tạo Layer {layerCount + 1}...",
                Foreground = new SolidColorBrush(Color.FromArgb(140, 0, 230, 118)),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(addZoneHint, 16);
            Canvas.SetTop(addZoneHint, addZoneTop + 14);
            LayerLanesBackgroundCanvas.Children.Add(addZoneHint);
        }

        // ==================== 2. THƯỚC ĐO THỜI GIAN THÍCH ỨNG (ADAPTIVE TIME RULER - KHÔNG BAO GIỜ TREO KHI FILE DÀI) ====================
        private void RenderTimeRuler()
        {
            if (TimeRulerCanvas == null) return;
            TimeRulerCanvas.Children.Clear();

            double totalWidth = CapCutTrackSurface.Width;
            double totalSec = totalWidth / _pixelsPerSecond;

            // Tự động chọn bước chia vạch chính để khoảng cách giữa 2 nhãn luôn ~65-120px
            double[] candidateSteps = { 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 60.0, 120.0, 300.0, 600.0 };
            double majorStepSec = 600.0;
            foreach (double st in candidateSteps)
            {
                if (st * _pixelsPerSecond >= 62.0)
                {
                    majorStepSec = st;
                    break;
                }
            }

            double minorStepSec = majorStepSec / 5.0;
            int maxMajorTicks = Math.Min(350, (int)Math.Ceiling(totalSec / majorStepSec));

            var majorBrush = new SolidColorBrush(Color.FromArgb(155, 200, 230, 255));
            var minorBrush = new SolidColorBrush(Color.FromArgb(75, 200, 230, 255));
            var textBrush = new SolidColorBrush(Color.FromRgb(175, 210, 245));
            var consolas = new FontFamily("Consolas");

            for (int idx = 0; idx <= maxMajorTicks; idx++)
            {
                double s = idx * majorStepSec;
                double x = s * _pixelsPerSecond;
                if (x > totalWidth) break;

                TimeRulerCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = 14, X2 = x, Y2 = 28,
                    Stroke = majorBrush,
                    StrokeThickness = 1
                });

                string labelStr;
                if (s >= 60.0)
                {
                    int mins = (int)(s / 60.0);
                    double remSec = s - (mins * 60.0);
                    labelStr = majorStepSec < 1.0 ? $"{mins:D2}:{remSec:00.0}" : $"{mins:D2}:{(int)remSec:D2}";
                }
                else
                {
                    labelStr = majorStepSec < 1.0 ? $"{s:0.0}s" : $"{(int)s}s";
                }

                var lbl = new TextBlock
                {
                    Text = labelStr,
                    Foreground = textBrush,
                    FontSize = 10,
                    FontFamily = consolas
                };
                Canvas.SetLeft(lbl, x + 3);
                Canvas.SetTop(lbl, 2);
                TimeRulerCanvas.Children.Add(lbl);

                if (minorStepSec * _pixelsPerSecond >= 10.0)
                {
                    for (int m = 1; m < 5; m++)
                    {
                        double mx = (s + m * minorStepSec) * _pixelsPerSecond;
                        if (mx < totalWidth)
                        {
                            TimeRulerCanvas.Children.Add(new Line
                            {
                                X1 = mx, Y1 = 21, X2 = mx, Y2 = 28,
                                Stroke = minorBrush,
                                StrokeThickness = 1
                            });
                        }
                    }
                }
            }
        }

        // ==================== 3. DỰNG SÓNG ÂM GIỚI HẠN ĐỈNH (BOUNDED LOD POLYGON - MƯỢT TUYỆT ĐỐI VỚI FILE DÀI) ====================
        private PointCollection ComputeCroppedClipWaveformPoints(TimelineClipItem clip, double clipW, double waveH)
        {
            var pts = new PointCollection();
            double midY = waveH * 0.5;
            if (clip.SourcePeaks == null || clip.SourcePeaks.Length == 0 || clipW < 4)
                return pts;

            int pps = Math.Max(10, clip.PeaksPerSecond);
            int startPeakIdx = Math.Clamp((int)Math.Floor(clip.SourceTrimStartSec * pps), 0, clip.SourcePeaks.Length - 1);
            int endPeakIdx = Math.Clamp((int)Math.Ceiling((clip.SourceTrimStartSec + clip.DurationSec) * pps), startPeakIdx + 1, clip.SourcePeaks.Length);

            int peakCount = Math.Max(1, endPeakIdx - startPeakIdx);
            // Giới hạn tối đa 420 cột mẫu cho mỗi Clip để dù file dài 30 phút thì vẽ Polygon vẫn dưới 0.3ms!
            int targetCols = (int)Math.Clamp(clipW * 0.65, 24.0, MaxPolygonSamples);
            int step = Math.Max(1, peakCount / targetCols);

            double eqVisualBoost = 1.0 + ((clip.Band80Hz + clip.Band220Hz + clip.Band1kHz + clip.Band3k5Hz + clip.Band8kHz) / 60.0);
            eqVisualBoost = Math.Clamp(eqVisualBoost, 0.55, 1.55);

            pts.Add(new Point(0, midY));
            for (int i = startPeakIdx; i < endPeakIdx; i += step)
            {
                float maxP = 0f;
                int lim = Math.Min(endPeakIdx, i + step);
                for (int k = i; k < lim; k++)
                {
                    if (clip.SourcePeaks[k] > maxP) maxP = clip.SourcePeaks[k];
                }
                double ratio = (double)(i - startPeakIdx) / peakCount;
                double x = Math.Clamp(ratio * clipW, 0, clipW);
                double amp = Math.Clamp(maxP * eqVisualBoost, 0.02, 0.96) * (midY * 0.90);
                pts.Add(new Point(x, midY - amp));
            }
            for (int i = endPeakIdx - 1; i >= startPeakIdx; i -= step)
            {
                float maxP = 0f;
                int lowLim = Math.Max(startPeakIdx, i - step + 1);
                for (int k = i; k >= lowLim; k--)
                {
                    if (clip.SourcePeaks[k] > maxP) maxP = clip.SourcePeaks[k];
                }
                double ratio = (double)(i - startPeakIdx) / peakCount;
                double x = Math.Clamp(ratio * clipW, 0, clipW);
                double amp = Math.Clamp(maxP * eqVisualBoost, 0.02, 0.96) * (midY * 0.90);
                pts.Add(new Point(x, midY + amp));
            }
            pts.Add(new Point(0, midY));
            return pts;
        }

        private static string FormatClipHeaderTitle(TimelineClipItem clip)
        {
            string badge = "";
            if (clip.HasCustomEq) badge += " [🎛️ ĐÃ CHỈNH EQ]";
            if (Math.Abs(clip.Semitones) >= 0.1) badge += $" [Tone {clip.Semitones:+0.#;-0.#}st]";
            if (clip.CloneEnabled) badge += " [🧬 Clone]";
            return $"{clip.Title} ({clip.DurationSec:0.00}s){badge}";
        }

        private void RenderCapCutTimelineClips()
        {
            if (CapCutClipsCanvas == null || DataContext is not MainViewModel vm) return;
            CapCutClipsCanvas.Children.Clear();

            foreach (var clip in vm.TimelineClips)
            {
                bool isSelected = (vm.SelectedTimelineClip == clip);
                bool hasEq = clip.HasCustomEq;

                double leftX = clip.TimelineStartSec * _pixelsPerSecond;
                double topY = (clip.LayerIndex * LayerHeight) + 5.0;
                double clipW = Math.Max(14.0, clip.DurationSec * _pixelsPerSecond);
                double clipH = LayerHeight - 10.0;

                Brush bgBrush;
                if (hasEq)
                {
                    var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                    grad.GradientStops.Add(new GradientStop(Color.FromArgb(235, 88, 22, 98), 0.0));
                    grad.GradientStops.Add(new GradientStop(Color.FromArgb(235, 138, 28, 124), 1.0));
                    bgBrush = grad;
                }
                else
                {
                    bgBrush = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)225 : (byte)185, 16, 34, 58));
                }

                Brush borderBrush = isSelected
                    ? new SolidColorBrush(Color.FromRgb(255, 214, 0))
                    : (hasEq ? new SolidColorBrush(Color.FromRgb(234, 128, 252)) : new SolidColorBrush(Color.FromRgb(0, 229, 255)));

                Brush waveBrush = hasEq
                    ? new SolidColorBrush(Color.FromRgb(255, 128, 171))
                    : new SolidColorBrush(Color.FromRgb(0, 229, 255));

                var clipBorder = new Border
                {
                    Width = clipW,
                    Height = clipH,
                    CornerRadius = new CornerRadius(5),
                    Background = bgBrush,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(isSelected ? 2.4 : 1.4),
                    ClipToBounds = true,
                    Tag = clip,
                    Cursor = _isHandPanModeEnabled ? Cursors.SizeAll : Cursors.Hand,
                    ToolTip = $"🎬 {clip.Title} (Layer {clip.LayerIndex + 1})\n• Vị trí phát: {clip.TimelineStartSec:0.00}s -> {(clip.TimelineStartSec + clip.DurationSec):0.00}s\n• Độ dài: {clip.DurationSec:0.00}s (Cắt từ gốc: {clip.SourceTrimStartSec:0.00}s)\n• Kéo thân để di chuyển • Kéo 2 cạnh ◀ / ▶ để cắt thu gọn • Ctrl + Lăn chuột để thu phóng"
                };

                var innerGrid = new Grid();

                var waveContainer = new Canvas
                {
                    Margin = new Thickness(0, 18, 0, 2),
                    ClipToBounds = true,
                    IsHitTestVisible = false
                };
                var wavePoly = new Polygon
                {
                    Points = ComputeCroppedClipWaveformPoints(clip, clipW, Math.Max(20, clipH - 20)),
                    Fill = waveBrush,
                    Opacity = 0.88,
                    IsHitTestVisible = false
                };
                waveContainer.Children.Add(wavePoly);
                innerGrid.Children.Add(waveContainer);

                var headerBd = new Border
                {
                    Height = 18,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(hasEq ? Color.FromArgb(195, 58, 8, 68) : Color.FromArgb(165, 4, 10, 20)),
                    Padding = new Thickness(8, 1, 8, 1),
                    IsHitTestVisible = false
                };

                var titleTxt = new TextBlock
                {
                    Text = FormatClipHeaderTitle(clip),
                    Foreground = hasEq ? new SolidColorBrush(Color.FromRgb(255, 235, 140)) : Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Consolas"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                headerBd.Child = titleTxt;
                innerGrid.Children.Add(headerBd);

                var leftHandle = new Border
                {
                    Width = 9,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Color.FromArgb(215, 0, 230, 118)),
                    Child = new TextBlock { Text = "◀", FontSize = 7, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                    IsHitTestVisible = false
                };
                var rightHandle = new Border
                {
                    Width = 9,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Background = new SolidColorBrush(Color.FromArgb(215, 255, 214, 0)),
                    Child = new TextBlock { Text = "▶", FontSize = 7, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                    IsHitTestVisible = false
                };
                innerGrid.Children.Add(leftHandle);
                innerGrid.Children.Add(rightHandle);

                clipBorder.Child = innerGrid;
                clipBorder.MouseLeftButtonDown += ClipBorder_MouseLeftButtonDown;
                clipBorder.MouseMove += ClipBorder_MouseMove;

                Canvas.SetLeft(clipBorder, leftX);
                Canvas.SetTop(clipBorder, topY);
                CapCutClipsCanvas.Children.Add(clipBorder);
            }
        }

        private void ClipBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_mode != InteractionMode.None) return;
            if (sender is Border bd)
            {
                if (_isHandPanModeEnabled)
                {
                    bd.Cursor = Cursors.ScrollAll;
                    return;
                }
                Point p = e.GetPosition(bd);
                if (p.X <= 11 || p.X >= Math.Max(12, bd.ActualWidth - 11))
                    bd.Cursor = Cursors.SizeWE;
                else
                    bd.Cursor = Cursors.SizeAll;
            }
        }

        private void ClipBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;
            if (sender is not Border bd || bd.Tag is not TimelineClipItem clip) return;

            this.Focus();
            vm.StopHeadlessDemo();

            // Nếu đang bật chế độ Kéo khung nhìn (Hand Pan) hoặc giữ phím Alt -> Kéo trượt khung nhìn tự do
            if (_isHandPanModeEnabled || (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                StartViewportPan(e);
                e.Handled = true;
                return;
            }

            // Cập nhật thành phần đang chọn và nổi bật viền vàng tức thì (KHÔNG xóa/tái tạo Canvas để không mất chuột!)
            vm.SelectedTimelineClip = clip;
            foreach (UIElement child in CapCutClipsCanvas.Children)
            {
                if (child is Border otherBd && otherBd.Tag is TimelineClipItem otherClip)
                {
                    bool sel = (otherClip == clip);
                    otherBd.BorderThickness = new Thickness(sel ? 2.4 : 1.4);
                    otherBd.BorderBrush = sel
                        ? new SolidColorBrush(Color.FromRgb(255, 214, 0))
                        : (otherClip.HasCustomEq ? new SolidColorBrush(Color.FromRgb(234, 128, 252)) : new SolidColorBrush(Color.FromRgb(0, 229, 255)));
                }
            }

            Point localPos = e.GetPosition(bd);
            _mouseDownSurfacePos = e.GetPosition(CapCutTrackSurface);
            _activeDragClip = clip;
            _activeDragBorder = bd;
            _activeDragWavePolygon = null;
            _activeDragTitleText = null;

            if (bd.Child is Grid g)
            {
                if (g.Children.Count > 0 && g.Children[0] is Canvas wc && wc.Children.Count > 0)
                    _activeDragWavePolygon = wc.Children[0] as Polygon;
                if (g.Children.Count > 1 && g.Children[1] is Border hb)
                    _activeDragTitleText = hb.Child as TextBlock;
            }

            _origTimelineStartSec = clip.TimelineStartSec;
            _origSourceTrimStartSec = clip.SourceTrimStartSec;
            _origDurationSec = clip.DurationSec;

            if (localPos.X <= 11)
                _mode = InteractionMode.TrimClipLeft;
            else if (localPos.X >= Math.Max(12, bd.ActualWidth - 11))
                _mode = InteractionMode.TrimClipRight;
            else
                _mode = InteractionMode.MoveClip;

            CapCutTrackSurface.CaptureMouse();
            e.Handled = true;
        }

        // ==================== DI CHUYỂN KHUNG NHÌN TỰ DO (FREE PANNING) & THAO TÁC THỜI GIAN THỰC O(1) ====================
        private void StartViewportPan(MouseEventArgs e)
        {
            if (TimelineScrollViewer == null) return;
            _mode = InteractionMode.PanViewport;
            _panStartViewportMousePos = e.GetPosition(TimelineScrollViewer);
            _panStartHorizontalOffset = TimelineScrollViewer.HorizontalOffset;
            _panStartVerticalOffset = TimelineScrollViewer.VerticalOffset;
            CapCutTrackSurface.Cursor = Cursors.ScrollAll;
            CapCutTrackSurface.CaptureMouse();
        }

        private void CapCutTrackSurface_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                StartViewportPan(e);
                e.Handled = true;
            }
        }

        private void CapCutTrackSurface_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_mode == InteractionMode.PanViewport && (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Left))
            {
                if (DataContext is MainViewModel vm)
                    FinishClipInteraction(vm);
                e.Handled = true;
            }
        }

        private void TimeRulerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;
            TimeRulerCanvas.CaptureMouse();
            Point pos = e.GetPosition(TimeRulerCanvas);
            double clickedSec = Math.Max(0.0, pos.X / _pixelsPerSecond);
            vm.SetTimelinePlayheadSeconds(clickedSec);
            UpdatePlayheadVisuals();
        }

        private void TimeRulerCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && TimeRulerCanvas.IsMouseCaptured && DataContext is MainViewModel vm)
            {
                Point pos = e.GetPosition(TimeRulerCanvas);
                double sec = Math.Max(0.0, pos.X / _pixelsPerSecond);
                vm.SetTimelinePlayheadSeconds(sec);
                UpdatePlayheadVisuals();
            }
        }

        private void TimeRulerCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TimeRulerCanvas.IsMouseCaptured)
                TimeRulerCanvas.ReleaseMouseCapture();
        }

        private void CapCutTrackSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;
            this.Focus();

            if (_isHandPanModeEnabled || (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                StartViewportPan(e);
                e.Handled = true;
                return;
            }

            Point pos = e.GetPosition(CapCutTrackSurface);
            _mode = InteractionMode.ScrubPlayhead;
            CapCutTrackSurface.CaptureMouse();

            double clickedSec = Math.Max(0.0, pos.X / _pixelsPerSecond);
            vm.SetTimelinePlayheadSeconds(clickedSec);
            UpdatePlayheadVisuals();
        }

        private void CapCutTrackSurface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;
            this.Focus();
            // Nhấn giữ Chuột phải kéo để di chuyển khung nhìn tự do (Pan Viewport), hoặc nhấp đơn để đặt Playhead
            StartViewportPan(e);
            Point pos = e.GetPosition(CapCutTrackSurface);
            double clickedSec = Math.Max(0.0, pos.X / _pixelsPerSecond);
            vm.SetTimelinePlayheadSeconds(clickedSec);
            UpdatePlayheadVisuals();
            e.Handled = true;
        }

        private void CapCutTrackSurface_MouseMove(object sender, MouseEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            if (_mode == InteractionMode.PanViewport && TimelineScrollViewer != null)
            {
                if (e.LeftButton != MouseButtonState.Pressed &&
                    e.MiddleButton != MouseButtonState.Pressed &&
                    e.RightButton != MouseButtonState.Pressed)
                {
                    FinishClipInteraction(vm);
                    return;
                }

                Point curViewportPos = e.GetPosition(TimelineScrollViewer);
                double dx = curViewportPos.X - _panStartViewportMousePos.X;
                double dy = curViewportPos.Y - _panStartViewportMousePos.Y;

                TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0.0, _panStartHorizontalOffset - dx));
                TimelineScrollViewer.ScrollToVerticalOffset(Math.Max(0.0, _panStartVerticalOffset - dy));
                return;
            }

            Point pos = e.GetPosition(CapCutTrackSurface);
            double hoverSec = Math.Max(0.0, pos.X / _pixelsPerSecond);

            CapCutHoverLine.Visibility = Visibility.Visible;
            Canvas.SetLeft(CapCutHoverLine, Math.Max(0, pos.X));
            vm.HoverTimeText = MainViewModel.FormatTimeCode(hoverSec);

            if (_mode == InteractionMode.None) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishClipInteraction(vm);
                return;
            }

            // Tự động cuộn khung nhìn (Auto-Edge Scroll) khi kéo thành phần ra sát mép trái/phải của cửa sổ
            if (TimelineScrollViewer != null)
            {
                Point vpPos = e.GetPosition(TimelineScrollViewer);
                if (vpPos.X > TimelineScrollViewer.ActualWidth - 36)
                {
                    TimelineScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset + 18.0);
                }
                else if (vpPos.X < 36 && TimelineScrollViewer.HorizontalOffset > 0)
                {
                    TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0.0, TimelineScrollViewer.HorizontalOffset - 18.0));
                }
            }

            double deltaX = pos.X - _mouseDownSurfacePos.X;
            double deltaSec = deltaX / _pixelsPerSecond;

            if (_mode == InteractionMode.ScrubPlayhead)
            {
                vm.SetTimelinePlayheadSeconds(hoverSec);
                UpdatePlayheadVisuals();
            }
            else if (_mode == InteractionMode.MoveClip && _activeDragClip != null)
            {
                double newStart = Math.Max(0.0, _origTimelineStartSec + deltaSec);
                if (Math.Abs(newStart - vm.CurrentPosition) < (8.0 / _pixelsPerSecond))
                    newStart = vm.CurrentPosition;

                int targetLayer = Math.Max(0, (int)(pos.Y / LayerHeight));
                if (targetLayer >= vm.StudioLayerCount)
                {
                    vm.StudioLayerCount = targetLayer + 1;
                    RenderDynamicLayerHeadersAndLanes();
                }

                _activeDragClip.TimelineStartSec = newStart;
                _activeDragClip.LayerIndex = targetLayer;

                // CẬP NHẬT TRỰC TIẾP VỊ TRÍ BORDER O(1) - KHÔNG REBUILD CANVAS ĐỂ MƯỢT TUYỆT ĐỐI VỚI FILE DÀI!
                if (_activeDragBorder != null)
                {
                    double newLeftPx = newStart * _pixelsPerSecond;
                    Canvas.SetLeft(_activeDragBorder, newLeftPx);
                    Canvas.SetTop(_activeDragBorder, (targetLayer * LayerHeight) + 5.0);
                    if (newLeftPx + _activeDragBorder.Width + 150 > CapCutTrackSurface.Width)
                    {
                        CapCutTrackSurface.Width = newLeftPx + _activeDragBorder.Width + 300;
                        TimeRulerCanvas.Width = CapCutTrackSurface.Width;
                    }
                }

                vm.StudioStatusText = $"🎬 Di chuyển '{_activeDragClip.Title}' -> Layer {targetLayer + 1} tại {newStart:0.00}s";
            }
            else if (_mode == InteractionMode.TrimClipLeft && _activeDragClip != null)
            {
                double maxDelta = _origDurationSec - 0.12;
                double minDelta = -_origSourceTrimStartSec;
                double clampedDelta = Math.Clamp(deltaSec, minDelta, maxDelta);

                _activeDragClip.TimelineStartSec = Math.Max(0.0, _origTimelineStartSec + clampedDelta);
                _activeDragClip.SourceTrimStartSec = Math.Max(0.0, _origSourceTrimStartSec + clampedDelta);
                _activeDragClip.DurationSec = Math.Max(0.12, _origDurationSec - clampedDelta);

                if (_activeDragBorder != null)
                {
                    double newW = Math.Max(14.0, _activeDragClip.DurationSec * _pixelsPerSecond);
                    Canvas.SetLeft(_activeDragBorder, _activeDragClip.TimelineStartSec * _pixelsPerSecond);
                    _activeDragBorder.Width = newW;
                    if (_activeDragTitleText != null)
                        _activeDragTitleText.Text = FormatClipHeaderTitle(_activeDragClip);
                    if (_activeDragWavePolygon != null)
                        _activeDragWavePolygon.Points = ComputeCroppedClipWaveformPoints(_activeDragClip, newW, Math.Max(20, _activeDragBorder.Height - 20));
                }

                vm.StudioStatusText = $"✂️ Cắt đầu đoạn (Trim Left): Bỏ {_activeDragClip.SourceTrimStartSec:0.00}s đầu | Độ dài còn {_activeDragClip.DurationSec:0.00}s";
            }
            else if (_mode == InteractionMode.TrimClipRight && _activeDragClip != null)
            {
                double maxPossibleDur = Math.Max(0.12, _activeDragClip.SourceTotalDurationSec - _activeDragClip.SourceTrimStartSec);
                double newDur = Math.Clamp(_origDurationSec + deltaSec, 0.12, maxPossibleDur);
                _activeDragClip.DurationSec = newDur;

                if (_activeDragBorder != null)
                {
                    double newW = Math.Max(14.0, _activeDragClip.DurationSec * _pixelsPerSecond);
                    _activeDragBorder.Width = newW;
                    if (_activeDragTitleText != null)
                        _activeDragTitleText.Text = FormatClipHeaderTitle(_activeDragClip);
                    if (_activeDragWavePolygon != null)
                        _activeDragWavePolygon.Points = ComputeCroppedClipWaveformPoints(_activeDragClip, newW, Math.Max(20, _activeDragBorder.Height - 20));
                }

                double trimmedTail = Math.Max(0.0, maxPossibleDur - newDur);
                vm.StudioStatusText = $"✂️ Cắt cuối đoạn (Trim Right): Bỏ {trimmedTail:0.00}s cuối | Độ dài còn {_activeDragClip.DurationSec:0.00}s";
            }
        }

        private void CapCutTrackSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm && _mode != InteractionMode.None)
            {
                FinishClipInteraction(vm);
            }
        }

        private void FinishClipInteraction(MainViewModel vm)
        {
            bool needRebuildMix = (_mode == InteractionMode.MoveClip || _mode == InteractionMode.TrimClipLeft || _mode == InteractionMode.TrimClipRight);
            _mode = InteractionMode.None;
            _activeDragClip = null;
            _activeDragBorder = null;
            _activeDragWavePolygon = null;
            _activeDragTitleText = null;
            CapCutTrackSurface.Cursor = _isHandPanModeEnabled ? Cursors.SizeAll : Cursors.Arrow;
            CapCutTrackSurface.ReleaseMouseCapture();

            if (needRebuildMix)
            {
                vm.RebuildMasterCompositeTimelineAudio();
            }
            RenderAllTimelineVisuals();
        }

        private void UpdatePlayheadVisuals()
        {
            if (DataContext is not MainViewModel vm) return;

            double xTimeline = Math.Max(0.0, vm.CurrentPosition * _pixelsPerSecond);
            Canvas.SetLeft(CapCutPlayheadLine, xTimeline);
            Canvas.SetLeft(CapCutPlayheadHead, xTimeline);

            double stageW = StudioWaveformStage.ActualWidth;
            double stageH = StudioWaveformStage.ActualHeight;
            if (stageW > 4 && stageH > 4)
            {
                CenterZeroLine.Y1 = stageH * 0.5;
                CenterZeroLine.Y2 = stageH * 0.5;
                CenterZeroLine.X2 = stageW;

                double ratio = vm.TotalDuration > 0.05 ? Math.Clamp(vm.CurrentPosition / vm.TotalDuration, 0.0, 1.0) : 0.0;
                Canvas.SetLeft(StudioMainPlayhead, ratio * stageW);
                StudioMainPlayhead.Y2 = stageH;
            }
        }

        private void StudioWaveformStage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlayheadVisuals();

        private void StudioWaveformStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;
            double w = StudioWaveformStage.ActualWidth;
            if (w <= 4) return;
            Point p = e.GetPosition(StudioWaveformStage);
            double ratio = Math.Clamp(p.X / w, 0.0, 1.0);
            if (vm.TotalDuration > 0.05)
            {
                vm.SetTimelinePlayheadSeconds(ratio * vm.TotalDuration);
                UpdatePlayheadVisuals();
            }
        }

        private void StudioWaveformStage_MouseMove(object sender, MouseEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            double w = StudioWaveformStage.ActualWidth;
            if (w <= 4) return;
            Point p = e.GetPosition(StudioWaveformStage);
            double ratio = Math.Clamp(p.X / w, 0.0, 1.0);
            double sec = ratio * Math.Max(0.1, vm.TotalDuration);
            vm.HoverTimeText = MainViewModel.FormatTimeCode(sec);
            if (e.LeftButton == MouseButtonState.Pressed && vm.TotalDuration > 0.05 && !vm.IsProcessing)
            {
                vm.SetTimelinePlayheadSeconds(sec);
                UpdatePlayheadVisuals();
            }
        }

        // ==================== KÉO THẢ BẢN GHI TỪ KHO BÊN PHẢI VÀO LAYER ====================
        private void StudioRecordingsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _listDragStartPoint = e.GetPosition(null);
        }

        private void StudioRecordingsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            Point pos = e.GetPosition(null);
            Vector diff = _listDragStartPoint - pos;

            if (Math.Abs(diff.X) > 8 || Math.Abs(diff.Y) > 8)
            {
                if (StudioRecordingsListBox.SelectedItem is HistoryItem selectedItem)
                {
                    var dataObj = new DataObject("VoiceCraftHistoryItem", selectedItem);
                    DragDrop.DoDragDrop(StudioRecordingsListBox, dataObj, DragDropEffects.Copy);
                }
            }
        }

        private void CapCutTrackSurface_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("VoiceCraftHistoryItem") || e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DropMergeHighlightOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void CapCutTrackSurface_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("VoiceCraftHistoryItem") || e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void CapCutTrackSurface_DragLeave(object sender, DragEventArgs e)
        {
            DropMergeHighlightOverlay.Visibility = Visibility.Collapsed;
        }

        private void CapCutTrackSurface_Drop(object sender, DragEventArgs e)
        {
            DropMergeHighlightOverlay.Visibility = Visibility.Collapsed;
            if (DataContext is not MainViewModel vm) return;

            Point dropPos = e.GetPosition(CapCutTrackSurface);
            int targetLayer = Math.Max(0, (int)(dropPos.Y / LayerHeight));
            if (targetLayer >= vm.StudioLayerCount)
            {
                vm.StudioLayerCount = targetLayer + 1;
            }
            double targetStartSec = Math.Max(0.0, Math.Round(dropPos.X / _pixelsPerSecond, 2));

            if (e.Data.GetDataPresent("VoiceCraftHistoryItem"))
            {
                if (e.Data.GetData("VoiceCraftHistoryItem") is HistoryItem droppedItem)
                {
                    vm.AddRecordingToTimeline(droppedItem.FilePath, targetLayer, targetStartSec);
                    AutoFitTimelineZoomIfOverflowing();
                    RenderAllTimelineVisuals();
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    foreach (string f in files)
                    {
                        if (f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            vm.AddRecordingToTimeline(f, targetLayer, targetStartSec);
                            targetStartSec += 2.0;
                        }
                    }
                    AutoFitTimelineZoomIfOverflowing();
                    RenderAllTimelineVisuals();
                }
            }
            this.Focus();
            e.Handled = true;
        }

        private void EnableAeroGlassBlur()
        {
            try
            {
                var windowHelper = new WindowInteropHelper(this);
                var accent = new SettingsWindow.AccentPolicy
                {
                    AccentState = SettingsWindow.AccentState.ACCENT_ENABLE_BLURBEHIND,
                    AccentFlags = 2,
                    GradientColor = 0x99101824
                };

                int accentStructSize = Marshal.SizeOf(accent);
                IntPtr accentPtr = Marshal.AllocHGlobal(accentStructSize);
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new SettingsWindow.WindowCompositionAttributeData
                {
                    Attribute = SettingsWindow.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    SizeOfData = accentStructSize,
                    Data = accentPtr
                };

                SettingsWindow.SetWindowCompositionAttribute(windowHelper.Handle, ref data);
                Marshal.FreeHGlobal(accentPtr);
            }
            catch { }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                Maximize_Click(sender, e);
            else if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            this.WindowState = (this.WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();
    }
}