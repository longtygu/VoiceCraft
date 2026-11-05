using System;
using System.Collections.Generic;
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
        private enum InteractionMode { None, ScrubPlayhead, PanViewport, MarqueeSelect, MoveClip, TrimClipLeft, TrimClipRight }
        private InteractionMode _mode = InteractionMode.None;

        private double _pixelsPerSecond = 60.0;
        private const double MinPixelsPerSecond = 1.5;
        private const double MaxPixelsPerSecond = 300.0;
        private const double LayerHeight = 76.0;
        private const int MaxPolygonSamples = 420;

        // Mặc định: Kéo chuột ở vùng trống / thước đo sẽ di chuyển Timeline. Khi bật _isMarqueeSelectMode: Kéo vùng trống sẽ tạo khung chọn nhiều đoạn.
        private bool _isMarqueeSelectMode = false;
        private bool _isUpdatingZoomSlider = false;

        // Điều khiển kéo di chuyển Timeline trên Thước đo (TimeRulerCanvas)
        private bool _isRulerMouseDown = false;
        private bool _isRulerDraggingPan = false;
        private Point _rulerMouseDownViewportPos;
        private double _rulerClickSurfaceX;

        // Điều khiển kéo / mở rộng thành phần đơn hoặc nhóm nhiều thành phần
        private TimelineClipItem _activeDragClip;
        private Border _activeDragBorder;
        private Polygon _activeDragWavePolygon;
        private TextBlock _activeDragTitleText;
        private List<(TimelineClipItem clip, Border border, double origStartSec, int origLayer)> _multiMoveGroup = new List<(TimelineClipItem, Border, double, int)>();

        private Point _mouseDownSurfacePos;
        private Point _panStartViewportMousePos;
        private double _panStartHorizontalOffset;
        private double _panStartVerticalOffset;
        private bool _hasPannedMoreThanThreshold = false;

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
            UpdateMouseModeButtonsUI();
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

        private void UpdateMouseModeButtonsUI()
        {
            if (PanEmptySpaceModeBtn != null && MarqueeSelectModeBtn != null)
            {
                if (_isMarqueeSelectMode)
                {
                    PanEmptySpaceModeBtn.Content = "✋ Chuột vùng trống: Di chuyển Timeline (Tắt)";
                    PanEmptySpaceModeBtn.Foreground = new SolidColorBrush(Color.FromRgb(160, 190, 215));
                    MarqueeSelectModeBtn.Content = "🔲 Chế độ Quét chọn nhiều đoạn: ĐANG BẬT";
                    MarqueeSelectModeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                    if (CapCutTrackSurface != null) CapCutTrackSurface.Cursor = Cursors.Cross;
                }
                else
                {
                    PanEmptySpaceModeBtn.Content = "✋ Chuột vùng trống: Di chuyển Timeline (Đang bật)";
                    PanEmptySpaceModeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                    MarqueeSelectModeBtn.Content = "🔲 Chế độ Quét chọn nhiều đoạn: Tắt";
                    MarqueeSelectModeBtn.Foreground = new SolidColorBrush(Color.FromRgb(128, 216, 255));
                    if (CapCutTrackSurface != null) CapCutTrackSurface.Cursor = Cursors.SizeAll;
                }
            }
        }

        private void PanEmptySpaceModeBtn_Click(object sender, RoutedEventArgs e)
        {
            _isMarqueeSelectMode = false;
            UpdateMouseModeButtonsUI();
            if (DataContext is MainViewModel vm)
            {
                vm.StudioStatusText = "✋ Đã bật chế độ Kéo vùng trống / Thước đo để di chuyển Timeline tự do.";
            }
        }

        private void MarqueeSelectModeBtn_Click(object sender, RoutedEventArgs e)
        {
            _isMarqueeSelectMode = !_isMarqueeSelectMode;
            UpdateMouseModeButtonsUI();
            if (DataContext is MainViewModel vm)
            {
                vm.StudioStatusText = _isMarqueeSelectMode
                    ? "🔲 Đã bật chế độ Quét chọn nhiều thành phần: Nhấn giữ chuột trái và kéo trên vùng trống để tạo khung chọn nhiều đoạn!"
                    : "✋ Đã chuyển về chế độ Kéo chuột ở vùng trống / Thước đo để di chuyển Timeline.";
            }
        }

        private void SelectAllClipsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SelectAllTimelineClips();
                RenderCapCutTimelineClips();
            }
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ClearTimelineSelection();
                RenderCapCutTimelineClips();
            }
        }

        private void ExtendEmpty2SecButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ExtendSelectedClipsWithEmptySpace(2.0);
                RenderAllTimelineVisuals();
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
                Point mouseInViewport = e.GetPosition(TimelineScrollViewer);
                double zoomFactor = e.Delta > 0 ? 1.22 : (1.0 / 1.22);
                SetPixelsPerSecond(_pixelsPerSecond * zoomFactor, Math.Max(0.0, mouseInViewport.X));

                if (DataContext is MainViewModel vm)
                {
                    vm.StudioStatusText = $"🔍 Tỷ lệ thu phóng Timeline: {_pixelsPerSecond:0.0} px/giây (Ctrl + Lăn chuột để thu phóng)";
                }
                e.Handled = true;
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                double newVert = Math.Max(0.0, TimelineScrollViewer.VerticalOffset - (e.Delta * 0.5));
                TimelineScrollViewer.ScrollToVerticalOffset(newVert);
                e.Handled = true;
            }
            else
            {
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

        private void ZoomFitButton_Click(object sender, RoutedEventArgs e) => AutoFitTimelineZoom();
        private void ZoomInButton_Click(object sender, RoutedEventArgs e) => SetPixelsPerSecond(_pixelsPerSecond * 1.30, (TimelineScrollViewer?.ActualWidth ?? 600) * 0.5);
        private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => SetPixelsPerSecond(_pixelsPerSecond / 1.30, (TimelineScrollViewer?.ActualWidth ?? 600) * 0.5);

        private void TimelineZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _isUpdatingZoomSlider) return;
            SetPixelsPerSecond(e.NewValue, (TimelineScrollViewer?.ActualWidth ?? 600) * 0.5);
        }

        // ==================== PHÍM TẮT CHUẨN CAPCUT PC ====================
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if ((e.Key == Key.Space || e.SystemKey == Key.Space) && Keyboard.FocusedElement is not TextBox)
            {
                if (!e.IsRepeat && DataContext is MainViewModel vm && !vm.IsProcessing)
                {
                    vm.ToggleMasterCompositePlayPause();
                }
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if ((e.Key == Key.Space || e.SystemKey == Key.Space) && Keyboard.FocusedElement is not TextBox)
            {
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyUp(e);
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
                else if (e.Key == Key.A)
                {
                    vm.SelectAllTimelineClips();
                    RenderCapCutTimelineClips();
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
                maxSec = Math.Max(25.0, vm.TimelineClips.Max(c => c.TimelineStartSec + c.DurationSec) * 1.35 + 15.0);
            }
            double viewportW = TimelineScrollViewer != null && TimelineScrollViewer.ActualWidth > 100 ? TimelineScrollViewer.ActualWidth : 900.0;
            double totalWidth = Math.Max(viewportW + 300.0, maxSec * _pixelsPerSecond);
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
                    var delLayerBtn = new Button { Focusable = false, 
                        Content = "✕",
                        Width = 20,
                        Height = 20,
                        FontSize = 9.5,
                        Visibility = Visibility.Collapsed,
                        Style = (Style)Application.Current.FindResource("MiniChipBtn"),
                        VerticalAlignment = VerticalAlignment.Top,
                        ToolTip = $"Xoá Layer {i + 1}"
                    };
                    delLayerBtn.Click += (s, e) =>
                    {
                        vm.RemoveLayerAt(capturedLayer);
                        RenderAllTimelineVisuals();
                    };
                    hdrBorder.MouseEnter += (s, e) => delLayerBtn.Visibility = Visibility.Visible;
                    hdrBorder.MouseLeave += (s, e) => delLayerBtn.Visibility = Visibility.Collapsed;
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

            // Nút thêm Layer ở cuối danh sách ẩn mặc định, hiện khi di chuột vào
            var addZoneText = new TextBlock
            {
                Text = "➕ Thêm Layer",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118)),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Opacity = 0.0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var addZoneHeader = new Border
            {
                Height = 48,
                Background = new SolidColorBrush(Color.FromArgb(20, 0, 230, 118)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 230, 118)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Cursor = Cursors.Hand,
                Child = addZoneText
            };
            addZoneHeader.MouseEnter += (s, e) => addZoneText.Opacity = 1.0;
            addZoneHeader.MouseLeave += (s, e) => addZoneText.Opacity = 0.0;
            addZoneHeader.MouseLeftButtonDown += (s, e) => AddLayerButton_Click(s, null);
            LayerHeadersStack.Children.Add(addZoneHeader);
        }

        // ==================== 2. THƯỚC ĐO THỜI GIAN THÍCH ỨNG (ADAPTIVE TIME RULER) ====================
        private void RenderTimeRuler()
        {
            if (TimeRulerCanvas == null) return;
            TimeRulerCanvas.Children.Clear();

            double totalWidth = CapCutTrackSurface.Width;
            double totalSec = totalWidth / _pixelsPerSecond;

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
                    StrokeThickness = 1,
                    IsHitTestVisible = false
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
                    FontFamily = consolas,
                    IsHitTestVisible = false
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
                                StrokeThickness = 1,
                                IsHitTestVisible = false
                            });
                        }
                    }
                }
            }
        }

        // ==================== 3. DỰNG SÓNG ÂM THÀNH PHẦN (HỖ TRỢ PHẦN MỞ RỘNG RỖNG PHẲNG 0dB) ====================
        private PointCollection ComputeCroppedClipWaveformPoints(TimelineClipItem clip, double clipW, double waveH)
        {
            var pts = new PointCollection();
            double midY = waveH * 0.5;
            if (clip.SourcePeaks == null || clip.SourcePeaks.Length == 0 || clipW < 4 || clip.DurationSec <= 0.01)
                return pts;

            int pps = Math.Max(10, clip.PeaksPerSecond);
            int targetCols = (int)Math.Clamp(clipW * 0.65, 24.0, MaxPolygonSamples);

            double eqVisualBoost = 1.0 + ((clip.Band80Hz + clip.Band220Hz + clip.Band1kHz + clip.Band3k5Hz + clip.Band8kHz) / 60.0);
            eqVisualBoost = Math.Clamp(eqVisualBoost, 0.55, 1.55);

            var topPoints = new Point[targetCols];
            var botPoints = new Point[targetCols];

            for (int c = 0; c < targetCols; c++)
            {
                double ratio = (double)c / Math.Max(1, targetCols - 1);
                double x = ratio * clipW;
                double tInClip = ratio * clip.DurationSec;
                double tSrc = clip.SourceTrimStartSec + tInClip;

                // Nếu tSrc nằm ngoài phạm vi file âm thanh gốc (< 0 hoặc >= SourceTotalDurationSec) -> Phần mở rộng là RỖNG (amp = 0)
                if (tSrc < 0.0 || tSrc >= clip.SourceTotalDurationSec)
                {
                    topPoints[c] = new Point(x, midY);
                    botPoints[c] = new Point(x, midY);
                }
                else
                {
                    int pIdx = Math.Clamp((int)(tSrc * pps), 0, clip.SourcePeaks.Length - 1);
                    float maxP = clip.SourcePeaks[pIdx];
                    double amp = Math.Clamp(maxP * eqVisualBoost, 0.02, 0.96) * (midY * 0.90);
                    topPoints[c] = new Point(x, midY - amp);
                    botPoints[c] = new Point(x, midY + amp);
                }
            }

            pts.Add(new Point(0, midY));
            for (int c = 0; c < targetCols; c++) pts.Add(topPoints[c]);
            for (int c = targetCols - 1; c >= 0; c--) pts.Add(botPoints[c]);
            pts.Add(new Point(0, midY));
            return pts;
        }

        private static string FormatClipHeaderTitle(TimelineClipItem clip)
        {
            string badge = "";
            if (clip.HasCustomEq) badge += " [🎛️ ĐÃ CHỈNH EQ]";
            if (clip.HasEmptyExtension) badge += " [↔️ Có mở rộng rỗng]";
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
                bool isPrimarySelected = (vm.SelectedTimelineClip == clip);
                bool isMultiSelected = clip.IsSelected || isPrimarySelected;
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
                    bgBrush = new SolidColorBrush(Color.FromArgb(isMultiSelected ? (byte)230 : (byte)185, 16, 34, 58));
                }

                Brush borderBrush = isPrimarySelected
                    ? new SolidColorBrush(Color.FromRgb(255, 214, 0))
                    : (isMultiSelected
                        ? new SolidColorBrush(Color.FromRgb(0, 230, 118))
                        : (hasEq ? new SolidColorBrush(Color.FromRgb(234, 128, 252)) : new SolidColorBrush(Color.FromRgb(0, 229, 255))));

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
                    BorderThickness = new Thickness(isMultiSelected ? 2.5 : 1.4),
                    ClipToBounds = true,
                    Tag = clip,
                    Cursor = Cursors.SizeAll,
                    ToolTip = $"🎬 {clip.Title} (Layer {clip.LayerIndex + 1})\n• Vị trí: {clip.TimelineStartSec:0.00}s -> {(clip.TimelineStartSec + clip.DurationSec):0.00}s (Độ dài: {clip.DurationSec:0.00}s)\n• Di chuột vào đoạn sẽ hiện 2 nút ◀ / ▶ ở 2 đầu để thu gọn hoặc kéo dài mở rộng rỗng"
                };

                var innerGrid = new Grid();

                var waveContainer = new Canvas
                {
                    Margin = new Thickness(0, 18, 0, 2),
                    ClipToBounds = true,
                    IsHitTestVisible = false
                };

                // Đánh dấu trực quan vùng mở rộng rỗng (nếu có)
                if (clip.SourceTrimStartSec < -0.01 && clip.DurationSec > 0.05)
                {
                    double emptyLeftSec = Math.Min(clip.DurationSec, -clip.SourceTrimStartSec);
                    var emptyLeftRect = new Rectangle
                    {
                        Width = Math.Max(2, (emptyLeftSec / clip.DurationSec) * clipW),
                        Height = Math.Max(20, clipH - 20),
                        Fill = new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)),
                        Stroke = new SolidColorBrush(Color.FromArgb(90, 128, 203, 196)),
                        StrokeDashArray = new DoubleCollection { 3, 2 }
                    };
                    Canvas.SetLeft(emptyLeftRect, 0);
                    Canvas.SetTop(emptyLeftRect, 0);
                    waveContainer.Children.Add(emptyLeftRect);
                }
                if ((clip.SourceTrimStartSec + clip.DurationSec) > clip.SourceTotalDurationSec + 0.01 && clip.DurationSec > 0.05)
                {
                    double audioEndInClipSec = Math.Max(0.0, clip.SourceTotalDurationSec - clip.SourceTrimStartSec);
                    double emptyRightSec = Math.Max(0.0, clip.DurationSec - audioEndInClipSec);
                    double startX = (audioEndInClipSec / clip.DurationSec) * clipW;
                    var emptyRightRect = new Rectangle
                    {
                        Width = Math.Max(2, (emptyRightSec / clip.DurationSec) * clipW),
                        Height = Math.Max(20, clipH - 20),
                        Fill = new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)),
                        Stroke = new SolidColorBrush(Color.FromArgb(90, 128, 203, 196)),
                        StrokeDashArray = new DoubleCollection { 3, 2 }
                    };
                    Canvas.SetLeft(emptyRightRect, startX);
                    Canvas.SetTop(emptyRightRect, 0);
                    waveContainer.Children.Add(emptyRightRect);
                }

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

                // 2 NÚT MỞ RỘNG / THU GỌN THÀNH PHẦN Ở 2 ĐẦU MẶC ĐỊNH ĐƯỢC ẨN (Visibility.Collapsed), CHỈ KHI DI CHUỘT VÀO MỚI HIỂN THỊ!
                var leftHandle = new Border
                {
                    Width = 12,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Color.FromArgb(230, 0, 230, 118)),
                    CornerRadius = new CornerRadius(4, 0, 0, 4),
                    Visibility = Visibility.Collapsed,
                    Child = new TextBlock { Text = "◀", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                    IsHitTestVisible = false
                };
                var rightHandle = new Border
                {
                    Width = 12,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Background = new SolidColorBrush(Color.FromArgb(230, 255, 214, 0)),
                    CornerRadius = new CornerRadius(0, 4, 4, 0),
                    Visibility = Visibility.Collapsed,
                    Child = new TextBlock { Text = "▶", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                    IsHitTestVisible = false
                };
                innerGrid.Children.Add(leftHandle);
                innerGrid.Children.Add(rightHandle);

                clipBorder.MouseEnter += (s, e) =>
                {
                    leftHandle.Visibility = Visibility.Visible;
                    rightHandle.Visibility = Visibility.Visible;
                };
                clipBorder.MouseLeave += (s, e) =>
                {
                    if (_mode == InteractionMode.None)
                    {
                        leftHandle.Visibility = Visibility.Collapsed;
                        rightHandle.Visibility = Visibility.Collapsed;
                    }
                };

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
                Point p = e.GetPosition(bd);
                if (p.X <= 12 || p.X >= Math.Max(13, bd.ActualWidth - 12))
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

            // Hỗ trợ giữ Ctrl + Click để chọn/bỏ chọn nhiều thành phần
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                clip.IsSelected = !clip.IsSelected;
                vm.SelectedTimelineClip = clip;
                RenderCapCutTimelineClips();
                e.Handled = true;
                return;
            }

            if (!clip.IsSelected)
            {
                foreach (var c in vm.TimelineClips) c.IsSelected = false;
                clip.IsSelected = true;
            }
            vm.SelectedTimelineClip = clip;

            foreach (UIElement child in CapCutClipsCanvas.Children)
            {
                if (child is Border otherBd && otherBd.Tag is TimelineClipItem otherClip)
                {
                    bool isPri = (otherClip == clip);
                    bool isMul = otherClip.IsSelected || isPri;
                    otherBd.BorderThickness = new Thickness(isMul ? 2.5 : 1.4);
                    otherBd.BorderBrush = isPri
                        ? new SolidColorBrush(Color.FromRgb(255, 214, 0))
                        : (isMul ? new SolidColorBrush(Color.FromRgb(0, 230, 118)) : (otherClip.HasCustomEq ? new SolidColorBrush(Color.FromRgb(234, 128, 252)) : new SolidColorBrush(Color.FromRgb(0, 229, 255))));
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
                if (g.Children.Count > 0 && g.Children[0] is Canvas wc)
                {
                    _activeDragWavePolygon = wc.Children.OfType<Polygon>().FirstOrDefault();
                }
                if (g.Children.Count > 1 && g.Children[1] is Border hb)
                {
                    _activeDragTitleText = hb.Child as TextBlock;
                }
            }

            _origTimelineStartSec = clip.TimelineStartSec;
            _origSourceTrimStartSec = clip.SourceTrimStartSec;
            _origDurationSec = clip.DurationSec;

            // Lưu danh sách tất cả các thành phần đang được chọn để di chuyển đồng loạt nếu người dùng đã quét chọn nhiều đoạn
            _multiMoveGroup.Clear();
            foreach (UIElement child in CapCutClipsCanvas.Children)
            {
                if (child is Border cBd && cBd.Tag is TimelineClipItem cItem && cItem.IsSelected)
                {
                    _multiMoveGroup.Add((cItem, cBd, cItem.TimelineStartSec, cItem.LayerIndex));
                }
            }

            if (localPos.X <= 12)
                _mode = InteractionMode.TrimClipLeft;
            else if (localPos.X >= Math.Max(13, bd.ActualWidth - 12))
                _mode = InteractionMode.TrimClipRight;
            else
                _mode = InteractionMode.MoveClip;

            CapCutTrackSurface.CaptureMouse();
            e.Handled = true;
        }

        // ==================== DI CHUYỂN TIMELINE BẰNG CHUỘT TRÊN THƯỚC ĐO & Ở VỊ TRÍ TRỐNG + QUÉT CHỌN NHIỀU ĐOẠN ====================
        private void TimeRulerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing || TimelineScrollViewer == null) return;
            this.Focus();

            _isRulerMouseDown = true;
            _isRulerDraggingPan = false;
            _rulerMouseDownViewportPos = e.GetPosition(TimelineScrollViewer);
            _rulerClickSurfaceX = e.GetPosition(TimeRulerCanvas).X;
            _panStartHorizontalOffset = TimelineScrollViewer.HorizontalOffset;
            TimeRulerCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void TimeRulerCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isRulerMouseDown || TimelineScrollViewer == null) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _isRulerMouseDown = false;
                TimeRulerCanvas.ReleaseMouseCapture();
                return;
            }

            Point curVp = e.GetPosition(TimelineScrollViewer);
            double dx = curVp.X - _rulerMouseDownViewportPos.X;

            if (Math.Abs(dx) > 4.0 || _isRulerDraggingPan)
            {
                _isRulerDraggingPan = true;
                TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0.0, _panStartHorizontalOffset - dx));
                if (DataContext is MainViewModel vm)
                {
                    vm.StudioStatusText = "✋ Đang kéo di chuyển Timeline trên Thước đo...";
                }
            }
        }

        private void TimeRulerCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRulerMouseDown) return;
            _isRulerMouseDown = false;
            if (TimeRulerCanvas.IsMouseCaptured)
                TimeRulerCanvas.ReleaseMouseCapture();

            // Nếu chỉ nhấp đơn trên thước đo (không kéo) -> đặt vị trí Vạch đỏ tại điểm nhấp
            if (!_isRulerDraggingPan && DataContext is MainViewModel vm)
            {
                double clickedSec = Math.Max(0.0, _rulerClickSurfaceX / _pixelsPerSecond);
                vm.SetTimelinePlayheadSeconds(clickedSec);
                UpdatePlayheadVisuals();
            }
            _isRulerDraggingPan = false;
        }

        private void StartViewportPan(MouseEventArgs e)
        {
            if (TimelineScrollViewer == null) return;
            _mode = InteractionMode.PanViewport;
            _hasPannedMoreThanThreshold = false;
            _panStartViewportMousePos = e.GetPosition(TimelineScrollViewer);
            _mouseDownSurfacePos = e.GetPosition(CapCutTrackSurface);
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
            if ((_mode == InteractionMode.PanViewport || _mode == InteractionMode.MarqueeSelect) &&
                (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Left))
            {
                if (DataContext is MainViewModel vm)
                    FinishClipInteraction(vm);
                e.Handled = true;
            }
        }

        private void CapCutTrackSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;
            this.Focus();

            // Nếu đang bật chế độ Quét chọn nhiều đoạn HOẶC giữ phím Shift -> Kéo chuột tạo khung chọn nhiều thành phần
            if (_isMarqueeSelectMode || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                _mode = InteractionMode.MarqueeSelect;
                _mouseDownSurfacePos = e.GetPosition(CapCutTrackSurface);
                foreach (var c in vm.TimelineClips) c.IsSelected = false;

                Canvas.SetLeft(MarqueeSelectionBox, _mouseDownSurfacePos.X);
                Canvas.SetTop(MarqueeSelectionBox, _mouseDownSurfacePos.Y);
                MarqueeSelectionBox.Width = 0;
                MarqueeSelectionBox.Height = 0;
                MarqueeSelectionBox.Visibility = Visibility.Visible;

                CapCutTrackSurface.CaptureMouse();
                RenderCapCutTimelineClips();
                e.Handled = true;
                return;
            }

            // Mặc định khi nhấn giữ chuột trái ở vị trí trống trên Layer (không chạm vào thành phần) -> Di chuyển Timeline tự do!
            StartViewportPan(e);
            e.Handled = true;
        }

        private void CapCutTrackSurface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || vm.IsProcessing) return;
            this.Focus();
            StartViewportPan(e);
            e.Handled = true;
        }

        private void CapCutTrackSurface_MouseMove(object sender, MouseEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            // 1. Chế độ Kéo di chuyển Timeline khi đang ở vị trí trống trên Layer
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

                if (Math.Abs(dx) > 4.0 || Math.Abs(dy) > 4.0)
                {
                    _hasPannedMoreThanThreshold = true;
                }

                if (_hasPannedMoreThanThreshold)
                {
                    TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0.0, _panStartHorizontalOffset - dx));
                    TimelineScrollViewer.ScrollToVerticalOffset(Math.Max(0.0, _panStartVerticalOffset - dy));
                    vm.StudioStatusText = "✋ Đang di chuyển Timeline tự do...";
                }
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

            // 2. Chế độ Quét chuột tạo vùng chọn nhiều thành phần (Marquee Selection)
            if (_mode == InteractionMode.MarqueeSelect)
            {
                double x = Math.Min(_mouseDownSurfacePos.X, pos.X);
                double y = Math.Min(_mouseDownSurfacePos.Y, pos.Y);
                double w = Math.Abs(pos.X - _mouseDownSurfacePos.X);
                double h = Math.Abs(pos.Y - _mouseDownSurfacePos.Y);

                Canvas.SetLeft(MarqueeSelectionBox, x);
                Canvas.SetTop(MarqueeSelectionBox, y);
                MarqueeSelectionBox.Width = w;
                MarqueeSelectionBox.Height = h;

                Rect selRect = new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
                int selectedCount = 0;
                TimelineClipItem firstPicked = null;

                foreach (UIElement child in CapCutClipsCanvas.Children)
                {
                    if (child is Border cBd && cBd.Tag is TimelineClipItem clip)
                    {
                        double cLeft = Canvas.GetLeft(cBd);
                        double cTop = Canvas.GetTop(cBd);
                        Rect clipRect = new Rect(cLeft, cTop, cBd.Width, cBd.Height);

                        bool hit = selRect.IntersectsWith(clipRect);
                        clip.IsSelected = hit;
                        if (hit)
                        {
                            selectedCount++;
                            firstPicked ??= clip;
                        }
                        cBd.BorderThickness = new Thickness(hit ? 2.5 : 1.4);
                        cBd.BorderBrush = hit
                            ? new SolidColorBrush(Color.FromRgb(0, 230, 118))
                            : (clip.HasCustomEq ? new SolidColorBrush(Color.FromRgb(234, 128, 252)) : new SolidColorBrush(Color.FromRgb(0, 229, 255)));
                    }
                }

                if (firstPicked != null)
                    vm.SelectedTimelineClip = firstPicked;

                vm.StudioStatusText = $"🔲 Đã quét chọn {selectedCount} thành phần (Kéo 1 đoạn để di chuyển cả nhóm • Ctrl+C/V sao chép • Delete để xoá)";
                return;
            }

            // Tự động cuộn biên khi kéo thành phần chạm mép
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
                int origLeadLayer = _multiMoveGroup.FirstOrDefault(g => g.clip == _activeDragClip).origLayer;
                int hoverLayer = Math.Max(0, (int)(pos.Y / LayerHeight));
                int deltaLayer = hoverLayer - origLeadLayer;

                if (_multiMoveGroup.Count > 1)
                {
                    // Di chuyển đồng loạt tất cả các thành phần đã quét chọn và giữ nguyên khoảng cách tương đối
                    double minOrigStart = _multiMoveGroup.Min(g => g.origStartSec);
                    double clampedDeltaSec = Math.Max(-minOrigStart, deltaSec);
                    int minOrigLayer = _multiMoveGroup.Min(g => g.origLayer);
                    int clampedDeltaLayer = Math.Max(-minOrigLayer, deltaLayer);

                    int maxTargetLayer = _multiMoveGroup.Max(g => g.origLayer + clampedDeltaLayer);
                    if (maxTargetLayer >= vm.StudioLayerCount)
                    {
                        vm.StudioLayerCount = maxTargetLayer + 1;
                        RenderDynamicLayerHeadersAndLanes();
                    }

                    foreach (var item in _multiMoveGroup)
                    {
                        double ns = Math.Max(0.0, item.origStartSec + clampedDeltaSec);
                        int nl = Math.Max(0, item.origLayer + clampedDeltaLayer);
                        item.clip.TimelineStartSec = ns;
                        item.clip.LayerIndex = nl;
                        if (item.border != null)
                        {
                            Canvas.SetLeft(item.border, ns * _pixelsPerSecond);
                            Canvas.SetTop(item.border, (nl * LayerHeight) + 5.0);
                        }
                    }
                    vm.StudioStatusText = $"🎬 Đang di chuyển nhóm {_multiMoveGroup.Count} thành phần cùng lúc...";
                }
                else
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
            }
            else if (_mode == InteractionMode.TrimClipLeft && _activeDragClip != null)
            {
                // Cho phép kéo cạnh Trái sang phải (cắt thu gọn) HOẶC kéo sang trái vượt quá 0s gốc để MỞ RỘNG RỖNG Ở ĐẦU!
                double maxDelta = _origDurationSec - 0.12;
                double minDelta = -_origTimelineStartSec;
                double clampedDelta = Math.Clamp(deltaSec, minDelta, maxDelta);

                _activeDragClip.TimelineStartSec = Math.Max(0.0, _origTimelineStartSec + clampedDelta);
                _activeDragClip.SourceTrimStartSec = Math.Round(_origSourceTrimStartSec + clampedDelta, 3);
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

                if (_activeDragClip.SourceTrimStartSec < -0.01)
                    vm.StudioStatusText = $"↔️ Mở rộng rỗng đầu đoạn: Thêm {(-_activeDragClip.SourceTrimStartSec):0.00}s rỗng | Tổng độ dài: {_activeDragClip.DurationSec:0.00}s";
                else
                    vm.StudioStatusText = $"✂️ Thu gọn đầu đoạn: Bỏ {_activeDragClip.SourceTrimStartSec:0.00}s đầu | Độ dài còn: {_activeDragClip.DurationSec:0.00}s";
            }
            else if (_mode == InteractionMode.TrimClipRight && _activeDragClip != null)
            {
                // Cho phép kéo cạnh Phải sang trái (thu gọn) HOẶC kéo sang phải không giới hạn để MỞ RỘNG RỖNG Ở CUỐI!
                double newDur = Math.Max(0.12, Math.Round(_origDurationSec + deltaSec, 2));
                _activeDragClip.DurationSec = newDur;

                if (_activeDragBorder != null)
                {
                    double newW = Math.Max(14.0, _activeDragClip.DurationSec * _pixelsPerSecond);
                    _activeDragBorder.Width = newW;
                    if (_activeDragTitleText != null)
                        _activeDragTitleText.Text = FormatClipHeaderTitle(_activeDragClip);
                    if (_activeDragWavePolygon != null)
                        _activeDragWavePolygon.Points = ComputeCroppedClipWaveformPoints(_activeDragClip, newW, Math.Max(20, _activeDragBorder.Height - 20));

                    double rightEdgePx = (_activeDragClip.TimelineStartSec * _pixelsPerSecond) + newW;
                    if (rightEdgePx + 150 > CapCutTrackSurface.Width)
                    {
                        CapCutTrackSurface.Width = rightEdgePx + 350;
                        TimeRulerCanvas.Width = CapCutTrackSurface.Width;
                    }
                }

                double maxAudioDur = Math.Max(0.0, _activeDragClip.SourceTotalDurationSec - _activeDragClip.SourceTrimStartSec);
                if (newDur > maxAudioDur + 0.02)
                {
                    double emptyAdded = newDur - maxAudioDur;
                    vm.StudioStatusText = $"↔️ Mở rộng rỗng cuối đoạn: Thêm +{emptyAdded:0.00}s khoảng rỗng | Tổng độ dài đoạn: {newDur:0.00}s";
                }
                else
                {
                    double trimmedTail = Math.Max(0.0, maxAudioDur - newDur);
                    vm.StudioStatusText = $"✂️ Thu gọn cuối đoạn: Bỏ {trimmedTail:0.00}s cuối | Độ dài còn: {newDur:0.00}s";
                }
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
            // Nếu nhấn đơn ở vùng trống (PanViewport nhưng không kéo quá 4px) -> Đặt Vạch đỏ tại vị trí nhấp & bỏ chọn nhóm
            if (_mode == InteractionMode.PanViewport && !_hasPannedMoreThanThreshold)
            {
                double clickedSec = Math.Max(0.0, _mouseDownSurfacePos.X / _pixelsPerSecond);
                vm.SetTimelinePlayheadSeconds(clickedSec);
                foreach (var c in vm.TimelineClips) c.IsSelected = false;
                UpdatePlayheadVisuals();
            }

            if (MarqueeSelectionBox != null)
                MarqueeSelectionBox.Visibility = Visibility.Collapsed;

            bool needRebuildMix = (_mode == InteractionMode.MoveClip || _mode == InteractionMode.TrimClipLeft || _mode == InteractionMode.TrimClipRight);
            _mode = InteractionMode.None;
            _activeDragClip = null;
            _activeDragBorder = null;
            _activeDragWavePolygon = null;
            _activeDragTitleText = null;
            _multiMoveGroup.Clear();
            UpdateMouseModeButtonsUI();
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