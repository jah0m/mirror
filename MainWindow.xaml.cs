using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Mirror;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private readonly DispatcherTimer _recordTicker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly string _saveFolder = GetCameraRoll();

    private List<CameraDevice> _cameras = [];
    private CameraService? _camera;
    private BackgroundBlur? _blur;
    private readonly Dictionary<string, IReplacementBackground> _stillBackgrounds = [];
    private (string Key, AnimatedBackground Player)? _animated;
    private readonly Dictionary<string, ImageSource> _thumbnails = [];
    private WriteableBitmap? _bitmap;
    private CancellationTokenSource? _countdown;
    private DateTime _recordStart;
    private string? _lastCapture;
    private int _renderQueued;
    private bool _awaitingFirstFrame;
    private bool _updatingUi;
    private bool _closeReady;
    private WindowState _stateBeforeFullscreen;

    public MainWindow()
    {
        InitializeComponent();
        Loc.ApplyFont(this);
        Loc.Changed += OnLanguageApplied;
        _recordTicker.Tick += (_, _) => RecTime.Text = (DateTime.Now - _recordStart).ToString(@"mm\:ss");
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
    }

    // ---------- Startup and shutdown ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MirrorToggle.IsChecked = _settings.Mirror;
        GridToggle.IsChecked = _settings.Grid;
        BlurStrengthSlider.Value = _settings.BlurStrength;
        SaveMirroredCheck.IsChecked = _settings.SaveMirrored;
        RecordAudioCheck.IsChecked = _settings.RecordAudio;
        FillLanguageList();
        (_settings.VideoMode ? VideoMode : PhotoMode).IsChecked = true;
        ApplyMirror();
        ApplyGrid();
        ApplyTimerLabel();
        ApplyShutterLook();

        // In demo mode the last real capture is not shown; it could be anyone's photo.
        if (App.DemoPicture is null)
            await ShowLatestSavedAsync();
        await RestartCameraAsync();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeReady)
            return;

        // Finish the MP4 before exiting, or the recording would be unplayable.
        e.Cancel = true;
        IsEnabled = false;
        _countdown?.Cancel();
        _settings.Save();
        await StopCameraAsync();
        _closeReady = true;
        Close();
    }

    private async void OnStateChanged(object? sender, EventArgs e)
    {
        // Let go of the camera while minimized so its light goes off, unless it is recording.
        if (WindowState == WindowState.Minimized)
        {
            if (_camera is { IsRecording: false } && _countdown is null)
                await StopCameraAsync();
        }
        else if (_camera is null && IsLoaded)
        {
            await RestartCameraAsync();
        }
    }

    // ---------- Camera lifecycle ----------

    private async Task RestartCameraAsync()
    {
        await _cameraGate.WaitAsync();
        try
        {
            await ReleaseCameraAsync();
            ShowStatus(() => Loc.T("OpeningCamera"));
            ShutterButton.IsEnabled = false;

            _cameras = await CameraService.ListAsync();
            if (_cameras.Count == 0)
            {
                ShowError(() => Loc.T("NoCamera"));
                return;
            }
            FillCameraList();

            var device = _cameras.FirstOrDefault(c => c.Id == _settings.CameraId) ?? _cameras[0];
            if (App.DemoPicture is { } still)
            {
                // README screenshots: show a still picture instead of opening the camera.
                _camera = NewCamera();
                _camera.StartDemo(still);
            }
            else
            {
                _camera = await OpenAsync(device);
                _settings.CameraId = device.Id;
                _settings.VideoModeKey = _camera.CurrentMode?.Key;
            }
            FillModeList(_camera);
            FillPictureControls(_camera);
            await ApplyBackgroundAsync();
            ShutterButton.IsEnabled = true;
        }
        catch (UnauthorizedAccessException)
        {
            ShowError(() => Loc.T("NoPermission"), showPrivacy: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            string detail = ex.Message.Trim();
            ShowError(() => Loc.T("CameraBusy", detail));
        }
        finally
        {
            _cameraGate.Release();
        }
    }

    private async Task<CameraService> OpenAsync(CameraDevice device)
    {
        var camera = NewCamera();
        try
        {
            await camera.StartAsync(device.Id, _settings.VideoModeKey);
            return camera;
        }
        catch
        {
            await camera.DisposeAsync();
            throw;
        }
    }

    private CameraService NewCamera()
    {
        _awaitingFirstFrame = true;
        var camera = new CameraService();
        camera.FrameReady += OnFrameReady;
        camera.Failed += message => Dispatcher.BeginInvoke(() => OnCameraFailed(camera, message));
        return camera;
    }

    private async Task StopCameraAsync()
    {
        await _cameraGate.WaitAsync();
        try
        {
            await ReleaseCameraAsync();
        }
        finally
        {
            _cameraGate.Release();
        }
    }

    private async Task ReleaseCameraAsync()
    {
        var camera = _camera;
        _camera = null;
        if (camera is null)
            return;

        bool wasRecording = camera.IsRecording;
        camera.FrameReady -= OnFrameReady;
        await camera.DisposeAsync();
        if (wasRecording)
            AfterRecordingStopped();
    }

    private async void OnCameraFailed(CameraService camera, string message)
    {
        if (camera != _camera)
            return;
        await StopCameraAsync();
        ShutterButton.IsEnabled = false;
        ShowError(() => Loc.T("CameraLost", message.Trim()));
    }

    // ---------- Preview ----------

    private void OnFrameReady()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, RenderFrame);
    }

    private void RenderFrame()
    {
        Interlocked.Exchange(ref _renderQueued, 0);
        ApplyMirror();
        _camera?.UseFrame((pixels, width, height) =>
        {
            if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                Preview.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        });

        if (_awaitingFirstFrame && _camera is not null)
        {
            _awaitingFirstFrame = false;
            StatusPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Shutter ----------

    private async void OnShutterClick(object sender, RoutedEventArgs e) => await PressShutterAsync();

    private async Task PressShutterAsync()
    {
        if (_camera is null)
            return;

        if (_countdown is not null)
        {
            _countdown.Cancel();
            return;
        }
        if (_camera.IsRecording)
        {
            await StopRecordingAsync();
            return;
        }
        if (_settings.TimerSeconds > 0 && !await RunCountdownAsync(_settings.TimerSeconds))
            return;

        if (_settings.VideoMode)
            await StartRecordingAsync();
        else
            await TakePhotoAsync();
    }

    private async Task<bool> RunCountdownAsync(int seconds)
    {
        using var cts = new CancellationTokenSource();
        _countdown = cts;
        CountdownText.Visibility = Visibility.Visible;
        try
        {
            for (int i = seconds; i > 0; i--)
            {
                CountdownText.Text = i.ToString();
                await Task.Delay(1000, cts.Token);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            CountdownText.Visibility = Visibility.Collapsed;
            _countdown = null;
        }
    }

    private async Task TakePhotoAsync()
    {
        BitmapSource? shot = null;
        _camera?.UseFrame((pixels, width, height) =>
            shot = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4));
        if (shot is null)
            return;

        if (_settings.Mirror && _settings.SaveMirrored)
            shot = new TransformedBitmap(shot, new ScaleTransform(-1, 1));
        shot.Freeze();

        PlayFlash();
        string path = NewCapturePath("jpg");
        try
        {
            var image = shot;
            await Task.Run(() =>
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(path);
                encoder.Save(stream);
            });
            await ShowCaptureAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(Loc.T("PhotoSaveFailed", ex.Message));
        }
    }

    private async Task StartRecordingAsync()
    {
        if (_camera is null)
            return;
        string path = NewCapturePath("mp4");
        try
        {
            bool mirror = _settings.Mirror && _settings.SaveMirrored;
            if (!await _camera.StartRecordingAsync(path, mirror, _settings.RecordAudio))
                ShowToast(Loc.T("NoMic"));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T("RecordStartFailed", ex.Message.Trim()));
            return;
        }

        _lastCapture = path;
        _recordStart = DateTime.Now;
        RecTime.Text = "00:00";
        RecBadge.Visibility = Visibility.Visible;
        _recordTicker.Start();
        SetRecordingUi(true);
    }

    private async Task StopRecordingAsync()
    {
        if (_camera is null)
            return;
        ShutterButton.IsEnabled = false;
        try
        {
            if (!await _camera.StopRecordingAsync())
                ShowToast(Loc.T("AudioMergeFailed"));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T("RecordStopFailed", ex.Message.Trim()));
        }
        ShutterButton.IsEnabled = true;
        AfterRecordingStopped();
        if (_lastCapture is not null)
            await ShowCaptureAsync(_lastCapture);
    }

    private void AfterRecordingStopped()
    {
        _recordTicker.Stop();
        RecBadge.Visibility = Visibility.Collapsed;
        SetRecordingUi(false);
    }

    private void SetRecordingUi(bool recording)
    {
        // Mirroring and format are baked in when a recording starts, so lock them until it ends.
        MirrorToggle.IsEnabled = !recording;
        PhotoMode.IsEnabled = !recording;
        VideoMode.IsEnabled = !recording;
        CameraCombo.IsEnabled = !recording;
        ModeCombo.IsEnabled = !recording;
        SaveMirroredCheck.IsEnabled = !recording;
        RecordAudioCheck.IsEnabled = !recording;
        SwitchCameraButton.IsEnabled = !recording;
        // The background is part of the recording, so it cannot change midway.
        BackgroundToggle.IsEnabled = !recording;
        BackgroundToggle.SetResourceReference(ToolTipProperty, recording ? "S.Tip.BackgroundLocked" : "S.Tip.Background");
        if (recording)
            SetBackgroundPanelOpen(false);
        ApplyShutterLook(recording);
    }

    private void ApplyShutterLook(bool recording = false)
    {
        if (recording)
        {
            ShutterCore.Width = ShutterCore.Height = 30;
            ShutterCore.CornerRadius = new CornerRadius(6);
            ShutterCore.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x45));
        }
        else
        {
            ShutterCore.Width = ShutterCore.Height = 60;
            ShutterCore.CornerRadius = new CornerRadius(30);
            ShutterCore.Background = _settings.VideoMode
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x45))
                : Brushes.White;
        }
    }

    private void PlayFlash() => Flash.BeginAnimation(OpacityProperty,
        new DoubleAnimation(0.75, 0, TimeSpan.FromMilliseconds(180)));

    private string NewCapturePath(string extension)
    {
        Directory.CreateDirectory(_saveFolder);
        return Path.Combine(_saveFolder, $"Mirror_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{extension}");
    }

    private async Task ShowLatestSavedAsync()
    {
        try
        {
            var latest = new DirectoryInfo(_saveFolder).EnumerateFiles("Mirror_*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is not null)
                await ShowCaptureAsync(latest.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No thumbnail is fine.
        }
    }

    private async Task ShowCaptureAsync(string path)
    {
        _lastCapture = path;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 112);
            using var stream = thumbnail.AsStreamForRead();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            LastCaptureBrush.ImageSource = image;
            LastCaptureButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // The file is saved even if Windows cannot produce a thumbnail for it yet.
            Debug.WriteLine(ex);
            LastCaptureButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Opens the Windows Photos gallery, which includes Camera Roll; falls back to the folder.</summary>
    private void OnLastCaptureClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-photos:") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            OnOpenFolderClick(sender, e);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_saveFolder);
        Process.Start(new ProcessStartInfo(_saveFolder) { UseShellExecute = true });
    }

    // ---------- Toggles ----------

    private void OnMirrorToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _settings.Mirror = MirrorToggle.IsChecked == true;
        ApplyMirror();
        _settings.Save();
    }

    private void ApplyMirror()
    {
        PreviewFlip.ScaleX = _settings.Mirror && _camera?.FramesAlreadyMirrored != true ? -1 : 1;
        // Pre-flip a replacement picture so it reads the right way round in the mirrored preview.
        if (_blur is not null)
            _blur.FlipReplacement = _settings.Mirror;
    }

    private void OnBlurStrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Setting Minimum in XAML raises this during construction, before the saved value is shown.
        if (!IsLoaded)
            return;
        _settings.BlurStrength = (int)Math.Round(e.NewValue);
        if (_blur is not null)
            _blur.Strength = _settings.BlurStrength;
    }

    /// <summary>
    /// Applies the chosen background to the preview, photos and recordings. Recordings with an
    /// effect are encoded from the processed frames (see <see cref="CameraService"/>).
    /// </summary>
    private async Task ApplyBackgroundAsync()
    {
        BackgroundLabel.Text = _settings.Background == Backgrounds.None
            ? Loc.T("Background")
            : Backgrounds.DisplayName(_settings.Background);
        BlurStrengthRow.Visibility = _settings.Background == Backgrounds.Blur ? Visibility.Visible : Visibility.Collapsed;

        string mode = _settings.Background;
        bool active = mode != Backgrounds.None;
        if (active && _blur is null)
        {
            try
            {
                ShowToast(Loc.T("LoadingBackground"));
                _blur = await Task.Run(() => new BackgroundBlur());
                _blur.Strength = _settings.BlurStrength;
                _blur.FlipReplacement = _settings.Mirror;
                if (!_blur.UsesGpu)
                    ShowToast(Loc.T("NoGpu"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                ShowToast(Loc.T("BackgroundFailed", ex.Message.Trim()));
                return;
            }
        }

        IReplacementBackground? replacement = null;
        bool opened = false;
        if (active && Backgrounds.IsImage(mode))
        {
            if (_stillBackgrounds.TryGetValue(mode, out var still))
                replacement = still;
            else if (_animated is { } playing && playing.Key == mode)
                replacement = playing.Player;
            else
            {
                try
                {
                    replacement = await Task.Run(() => Backgrounds.Open(mode));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
                opened = true;
                if (replacement is null)
                {
                    ShowToast(Loc.T("BackgroundMissing"));
                    _settings.Background = Backgrounds.None;
                    _settings.LastBackground = Backgrounds.Blur;
                    _settings.Save();
                    await ApplyBackgroundAsync();
                    return;
                }
                if (replacement is StillBackground)
                    _stillBackgrounds[mode] = replacement;
            }
        }

        // The choice may have changed while something was loading; the latest call wins.
        if (mode != _settings.Background)
        {
            if (opened && replacement is AnimatedBackground orphan)
                orphan.Dispose();
            return;
        }
        if (_blur is not null)
            _blur.Replacement = replacement;
        if (_camera is not null)
            _camera.Blur = active ? _blur : null;

        // Only one animation plays at a time; stop the previous one once it is no longer shown.
        if (_animated is { } previous && !ReferenceEquals(previous.Player, replacement))
        {
            previous.Player.Dispose();
            _animated = null;
        }
        if (replacement is AnimatedBackground animation)
            _animated = (mode, animation);
    }

    private async Task SelectBackgroundAsync(string key)
    {
        _settings.Background = key;
        if (key != Backgrounds.None)
            _settings.LastBackground = key;
        _settings.Save();
        if (BackgroundPanel.Visibility == Visibility.Visible)
            RefreshBackgroundTiles();
        await ApplyBackgroundAsync();
    }

    // ---------- Background picker ----------

    private void OnBackgroundPanelToggled(object sender, RoutedEventArgs e) =>
        SetBackgroundPanelOpen(BackgroundToggle.IsChecked == true);

    private void SetBackgroundPanelOpen(bool open)
    {
        BackgroundToggle.IsChecked = open;
        BackgroundPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            SetSettingsOpen(false);
            RefreshBackgroundTiles();
        }
        else
        {
            Keyboard.ClearFocus();
        }
    }

    private void RefreshBackgroundTiles()
    {
        BackgroundTiles.Children.Clear();
        BackgroundTiles.Children.Add(MakeTile(Backgrounds.None, Loc.T("None"), null, ""));
        BackgroundTiles.Children.Add(MakeTile(Backgrounds.Blur, Loc.T("Blur"), null, ""));
        foreach (var choice in Backgrounds.List())
        {
            if (!_thumbnails.TryGetValue(choice.Key, out var thumb))
            {
                try
                {
                    thumb = Backgrounds.LoadThumbnail(choice.Key);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    thumb = null;
                }
                if (thumb is not null)
                    _thumbnails[choice.Key] = thumb;
            }
            BackgroundTiles.Children.Add(MakeTile(choice.Key, choice.Name, thumb, null, choice.IsCustom, choice.IsAnimated));
        }
        BackgroundTiles.Children.Add(MakeTile("upload", Loc.T("Upload"), null, ""));
    }

    private FrameworkElement MakeTile(string key, string name, ImageSource? thumb, string? icon,
        bool deletable = false, bool animated = false)
    {
        bool selected = key == _settings.Background;
        var content = new Grid();
        if (thumb is not null)
            content.Children.Add(new Image { Source = thumb, Stretch = Stretch.UniformToFill });
        if (animated)
        {
            // A small "play" mark tells animated backgrounds apart from still ones.
            content.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(5),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
                Child = new TextBlock
                {
                    Text = "",
                    FontFamily = (FontFamily)FindResource("Icons"),
                    FontSize = 9,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        if (thumb is null)
            content.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
            });
        content.Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = name, FontSize = 12, Foreground = Brushes.White },
        });

        var tile = new Button
        {
            Width = 104,
            Height = 60,
            Margin = new Thickness(4),
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = name,
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
                BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Child = content,
            },
            Template = TileTemplate,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(tile, "bg:" + key);
        System.Windows.Automation.AutomationProperties.SetName(tile, name);
        tile.Click += async (_, _) =>
        {
            if (key == "upload")
                await UploadBackgroundAsync();
            else
                await SelectBackgroundAsync(key);
        };

        if (!deletable)
            return tile;

        var delete = new Button
        {
            Content = "",
            FontFamily = (FontFamily)FindResource("Icons"),
            FontSize = 10,
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 6, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("DeleteBackground"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            Template = TileTemplate,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(delete, "bgdel:" + key);
        delete.Click += async (_, e) =>
        {
            e.Handled = true;
            // Stop showing it first: a playing video keeps its file open.
            if (_settings.Background == key)
                await SelectBackgroundAsync(Backgrounds.None);
            try
            {
                Backgrounds.DeleteCustom(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowToast(Loc.T("DeleteFailed", ex.Message));
                return;
            }
            _thumbnails.Remove(key);
            _stillBackgrounds.Remove(key);
            if (_settings.LastBackground == key)
                _settings.LastBackground = Backgrounds.Blur;
            RefreshBackgroundTiles();
            _settings.Save();
        };
        return new Grid { Children = { tile, delete } };
    }

    private static readonly ControlTemplate TileTemplate = CreateTileTemplate();

    private static ControlTemplate CreateTileTemplate()
    {
        // A bare template: tiles draw their own border and should not get the default button chrome.
        var presenter = new FrameworkElementFactory(typeof(Border));
        presenter.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        presenter.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
        content.SetValue(VerticalAlignmentProperty, new TemplateBindingExtension(VerticalContentAlignmentProperty));
        presenter.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
        template.Seal();
        return template;
    }

    private async Task UploadBackgroundAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("ChooseBackground"),
            Filter = Loc.T("ImageFilter", Backgrounds.UploadPattern),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            // Videos are checked and copied, which can take a moment for large files.
            string file = dialog.FileName;
            string key = await Task.Run(() => Backgrounds.AddCustom(file));
            await SelectBackgroundAsync(key);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ShowToast(Loc.T("ImageUnusable", ex.Message.Trim()));
        }
    }

    private void OnGridToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _settings.Grid = GridToggle.IsChecked == true;
        ApplyGrid();
        _settings.Save();
    }

    private void ApplyGrid() =>
        GridOverlay.Visibility = _settings.Grid ? Visibility.Visible : Visibility.Collapsed;

    private void OnTimerClick(object sender, RoutedEventArgs e) => CycleTimer();

    private void CycleTimer()
    {
        _settings.TimerSeconds = _settings.TimerSeconds switch { 0 => 3, 3 => 10, _ => 0 };
        ApplyTimerLabel();
        _settings.Save();
    }

    private void ApplyTimerLabel()
    {
        TimerLabel.Text = Loc.T("Seconds", _settings.TimerSeconds);
        TimerLabel.Visibility = _settings.TimerSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        bool video = VideoMode.IsChecked == true;
        if (video != _settings.VideoMode)
            SetMode(video);
    }

    private async void SetMode(bool video)
    {
        _settings.VideoMode = video;
        (video ? VideoMode : PhotoMode).IsChecked = true;
        ApplyShutterLook();
        _settings.Save();
        await ApplyBackgroundAsync();
    }

    private async void OnSwitchCameraClick(object sender, RoutedEventArgs e) => await SwitchCameraAsync();

    private async Task SwitchCameraAsync()
    {
        if (_cameras.Count < 2 || _camera?.IsRecording == true)
            return;
        int index = _cameras.FindIndex(c => c.Id == _settings.CameraId);
        _settings.CameraId = _cameras[(index + 1) % _cameras.Count].Id;
        _settings.VideoModeKey = null;
        await RestartCameraAsync();
        _settings.Save();
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullscreen;
            FullscreenIcon.Text = "";
        }
        else
        {
            _stateBeforeFullscreen = WindowState;
            // Going through Normal makes the maximized window cover the taskbar.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            FullscreenIcon.Text = "";
        }
    }

    // ---------- Settings panel ----------

    private void OnSettingsToggled(object sender, RoutedEventArgs e) =>
        SetSettingsOpen(SettingsToggle.IsChecked == true);

    private void SetSettingsOpen(bool open)
    {
        SettingsToggle.IsChecked = open;
        SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
            SetBackgroundPanelOpen(false);
        else
            Keyboard.ClearFocus();
    }

    private void OnRootClick(object sender, MouseButtonEventArgs e)
    {
        SetSettingsOpen(false);
        SetBackgroundPanelOpen(false);
    }

    private void OnSettingsPanelClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void FillCameraList()
    {
        _updatingUi = true;
        CameraCombo.ItemsSource = _cameras;
        CameraCombo.SelectedItem = _cameras.FirstOrDefault(c => c.Id == _settings.CameraId) ?? _cameras[0];
        SwitchCameraButton.Visibility = _cameras.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _updatingUi = false;
    }

    private void FillModeList(CameraService camera)
    {
        _updatingUi = true;
        ModeCombo.ItemsSource = camera.Modes;
        ModeCombo.SelectedItem = camera.CurrentMode;
        _updatingUi = false;
    }

    private void FillPictureControls(CameraService camera)
    {
        _updatingUi = true;
        bool brightness = !camera.IsDemo && FillSlider(camera.Brightness, BrightnessSlider, BrightnessRow);
        bool contrast = !camera.IsDemo && FillSlider(camera.Contrast, ContrastSlider, ContrastRow);
        if (camera.IsDemo)
            BrightnessRow.Visibility = ContrastRow.Visibility = Visibility.Collapsed;
        ResetPictureButton.Visibility = brightness || contrast ? Visibility.Visible : Visibility.Collapsed;
        _updatingUi = false;
    }

    private static bool FillSlider(MediaDeviceControl control, Slider slider, FrameworkElement row)
    {
        var caps = control.Capabilities;
        row.Visibility = caps.Supported ? Visibility.Visible : Visibility.Collapsed;
        if (!caps.Supported)
            return false;

        slider.Minimum = caps.Min;
        slider.Maximum = caps.Max;
        slider.SmallChange = caps.Step;
        slider.LargeChange = caps.Step * 5;
        slider.Value = control.TryGetValue(out double value) ? value : caps.Default;
        return true;
    }

    private async void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || CameraCombo.SelectedItem is not CameraDevice device || device.Id == _settings.CameraId)
            return;
        _settings.CameraId = device.Id;
        _settings.VideoModeKey = null;
        await RestartCameraAsync();
        _settings.Save();
    }

    private async void OnVideoModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || ModeCombo.SelectedItem is not Mirror.VideoMode mode || mode.Key == _settings.VideoModeKey)
            return;
        _settings.VideoModeKey = mode.Key;
        await RestartCameraAsync();
        _settings.Save();
    }

    private void OnSaveMirroredClick(object sender, RoutedEventArgs e)
    {
        _settings.SaveMirrored = SaveMirroredCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnRecordAudioClick(object sender, RoutedEventArgs e)
    {
        _settings.RecordAudio = RecordAudioCheck.IsChecked == true;
        _settings.Save();
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingUi && _camera is { } camera)
            camera.SetControlValue(camera.Brightness, e.NewValue);
    }

    private void OnContrastChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingUi && _camera is { } camera)
            camera.SetControlValue(camera.Contrast, e.NewValue);
    }

    private void OnResetPictureClick(object sender, RoutedEventArgs e)
    {
        if (_camera is null)
            return;
        BrightnessSlider.Value = _camera.Brightness.Capabilities.Default;
        ContrastSlider.Value = _camera.Contrast.Capabilities.Default;
    }

    // ---------- Status and messages ----------

    // Kept as a function so the message can be shown again in a newly chosen language.
    private Func<string>? _statusMessage;

    private void ShowStatus(Func<string> message)
    {
        _statusMessage = message;
        StatusText.Text = message();
        StatusActions.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void ShowError(Func<string> message, bool showPrivacy = false)
    {
        _statusMessage = message;
        StatusText.Text = message();
        PrivacyButton.Visibility = showPrivacy ? Visibility.Visible : Visibility.Collapsed;
        StatusActions.Visibility = Visibility.Visible;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await RestartCameraAsync();

    // ---------- Language ----------

    private void FillLanguageList()
    {
        _updatingUi = true;
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.T("LanguageAuto"), Tag = Loc.Auto });
        foreach (var code in Loc.Languages)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.NativeNames[code], Tag = code });
        LanguageCombo.SelectedItem = LanguageCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _settings.Language) ?? LanguageCombo.Items[0];
        _updatingUi = false;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code } || code == _settings.Language)
            return;
        _settings.Language = code;
        _settings.Save();
        Loc.Apply(code);
    }

    /// <summary>Refreshes the text that code sets; XAML text follows the resources by itself.</summary>
    private async void OnLanguageApplied()
    {
        FillLanguageList();
        ApplyTimerLabel();
        if (_statusMessage is not null && StatusPanel.Visibility == Visibility.Visible)
            StatusText.Text = _statusMessage();
        if (BackgroundPanel.Visibility == Visibility.Visible)
            RefreshBackgroundTiles();
        await ApplyBackgroundAsync();
    }

    private void OnPrivacyClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });

    // ---------- Keyboard ----------

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_countdown is not null)
                _countdown.Cancel();
            else if (SettingsPanel.Visibility == Visibility.Visible)
                SetSettingsOpen(false);
            else if (BackgroundPanel.Visibility == Visibility.Visible)
                SetBackgroundPanelOpen(false);
            else if (WindowStyle == WindowStyle.None)
                ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        // Leave keys alone while a combo box or slider in the settings panel has focus.
        if (Keyboard.FocusedElement is DependencyObject focused
            && (SettingsPanel.IsAncestorOf(focused) || BackgroundPanel.IsAncestorOf(focused)))
            return;
        if (e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.Space:
            case Key.Enter:
                e.Handled = true;
                if (ShutterButton.IsEnabled)
                    await PressShutterAsync();
                break;
            case Key.M when MirrorToggle.IsEnabled:
                MirrorToggle.IsChecked = !_settings.Mirror;
                break;
            case Key.B when BackgroundToggle.IsEnabled:
                await SelectBackgroundAsync(_settings.Background == Backgrounds.None
                    ? _settings.LastBackground
                    : Backgrounds.None);
                break;
            case Key.G:
                GridToggle.IsChecked = !_settings.Grid;
                break;
            case Key.T:
                CycleTimer();
                break;
            case Key.S:
                SetSettingsOpen(SettingsPanel.Visibility != Visibility.Visible);
                break;
            case Key.C:
                await SwitchCameraAsync();
                break;
            case Key.V when PhotoMode.IsEnabled:
                SetMode(!_settings.VideoMode);
                break;
        }
    }

    // ---------- Save location ----------

    private static string GetCameraRoll()
    {
        var cameraRoll = new Guid("AB5FB87B-7CE2-4F83-915D-550846C9537B");
        const uint KF_FLAG_CREATE = 0x00008000;
        if (SHGetKnownFolderPath(cameraRoll, KF_FLAG_CREATE, IntPtr.Zero, out string? path) == 0 && path is not null)
            return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Camera Roll");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token,
        [MarshalAs(UnmanagedType.LPWStr)] out string? path);
}
