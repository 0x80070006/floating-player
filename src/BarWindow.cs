using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace FloatingPlayer
{
    // The views are plain XAML embedded as resources and loaded at runtime (no XAML compiler needed).
    static class Ui
    {
        public static FrameworkElement Load(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FloatingPlayer." + name))
                return (FrameworkElement)XamlReader.Load(s);
        }
    }

    class BarWindow : Window
    {
        const double BarHeight = 64;
        const string PlayGlyph = "", PauseGlyph = "";
        const double ColorFadeSeconds = 0.65;
        static readonly string[] BrushKeys = { "BarBg", "BarFg", "BarFg2", "BarAccent", "BarAccentFg", "BarHover", "BarPressed", "BarPanel" };

        // Resource brushes get frozen by WPF, so a fade replaces them frame by frame.
        readonly Color[] colors = new Color[BrushKeys.Length];
        Color[] fadeFrom, fadeTo;
        readonly Stopwatch fadeClock = new Stopwatch();
        bool fading;

        readonly FrameworkElement view, info, cover, visualizerHost, progressFill, seekZone, progressRail, seekHandle;
        readonly ScaleTransform progressScale;
        readonly TranslateTransform handleShift;
        double shownRatio = -1;
        bool seekHover, seeking;
        TimeSpan seekPosition;
        DateTime seekAt = DateTime.MinValue, seekHoldUntil = DateTime.MinValue;

        readonly TextBlock titleText, artistText;
        readonly Rectangle coverBack, coverFront;
        readonly Button previousButton, toggleButton, nextButton;
        readonly ScaleTransform zoom = new ScaleTransform();
        readonly Visualizer visualizer;
        readonly DispatcherTimer progressTimer;

        // Volume: the panel replaces the title while it is open.
        readonly VolumeControl volume;
        readonly FrameworkElement volumePanel, volumeTrack, volumeFill, volumeKnob;
        readonly System.Windows.Controls.Primitives.Popup volumePopup;
        readonly TextBlock volumeValue, volumeTarget;
        readonly Button volumeButton, muteButton;
        readonly DispatcherTimer volumeTimer;
        VolumeState volumeState;
        bool volumeOpen, volumeDragging;
        DateTime volumeTouched;

        Settings settings;
        ColorScheme scheme = ColorScheme.Neutral();
        MediaSnapshot current = new MediaSnapshot();
        IntPtr hwnd;
        string shownTitle, shownArtist;

        public event Action SettingsRequested;
        public event Action TogglePressed, NextPressed, PreviousPressed;
        public event Action Moved;
        public event Action<TimeSpan> SeekRequested;
        public event Action CoverClicked, ContextRequested, VolumeTargetChanged;
        public event Action<bool> SessionLockChanged;

        public BarWindow(Settings settings, AudioCapture audio, VolumeControl volume)
        {
            this.settings = settings;
            this.volume = volume;
            Title = "Floating Player";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Background = Brushes.Transparent;
            UseLayoutRounding = true;
            WindowStartupLocation = WindowStartupLocation.Manual;

            WriteBrushes(Palette(scheme));

            view = Ui.Load("Bar.xaml");
            view.LayoutTransform = zoom;
            Content = view;

            info = Find<FrameworkElement>("Info");
            cover = Find<FrameworkElement>("Cover");
            progressScale = Find<ScaleTransform>("ProgressScale");
            handleShift = Find<TranslateTransform>("HandleShift");
            titleText = Find<TextBlock>("TitleText");
            artistText = Find<TextBlock>("ArtistText");
            coverBack = Find<Rectangle>("CoverBack");
            coverFront = Find<Rectangle>("CoverFront");
            visualizerHost = Find<FrameworkElement>("VisualizerHost");
            progressFill = Find<FrameworkElement>("ProgressFill");
            seekZone = Find<FrameworkElement>("SeekZone");
            progressRail = Find<FrameworkElement>("ProgressRail");
            seekHandle = Find<FrameworkElement>("SeekHandle");
            seekZone.MouseEnter += delegate { seekHover = true; UpdateSeekLook(); };
            seekZone.MouseLeave += delegate { seekHover = false; UpdateSeekLook(); };
            seekZone.MouseLeftButtonDown += OnSeekDown;
            seekZone.MouseMove += OnSeekMove;
            seekZone.MouseLeftButtonUp += OnSeekUp;
            seekZone.LostMouseCapture += delegate { seeking = false; UpdateSeekLook(); };
            previousButton = Find<Button>("PreviousButton");
            toggleButton = Find<Button>("ToggleButton");
            nextButton = Find<Button>("NextButton");

            visualizer = new Visualizer(audio, (Brush)Resources["BarAccent"]);
            ((Border)visualizerHost).Child = visualizer;

            previousButton.Click += delegate { Raise(PreviousPressed); };
            toggleButton.Click += delegate { Raise(TogglePressed); };
            nextButton.Click += delegate { Raise(NextPressed); };
            Find<Button>("SettingsButton").Click += delegate { Raise(SettingsRequested); };
            view.MouseLeftButtonDown += OnBarPress;
            view.MouseDown += (s, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; Raise(TogglePressed); } };
            view.MouseRightButtonUp += (s, e) => { e.Handled = true; Raise(ContextRequested); };
            view.MouseWheel += OnWheel;
            Find<FrameworkElement>("VolumeCard").MouseWheel += OnWheel;

            volumePanel = Find<FrameworkElement>("VolumePanel");
            volumePopup = Find<System.Windows.Controls.Primitives.Popup>("VolumePopup");
            volumePopup.PlacementTarget = view;
            volumePopup.CustomPopupPlacementCallback = PlaceVolume;
            Find<FrameworkElement>("VolumeCard").LayoutTransform = zoom;
            volumeTrack = Find<FrameworkElement>("VolumeTrack");
            volumeFill = Find<FrameworkElement>("VolumeFill");
            volumeKnob = Find<FrameworkElement>("VolumeKnob");
            volumeValue = Find<TextBlock>("VolumeValue");
            volumeTarget = Find<TextBlock>("VolumeTarget");
            volumeButton = Find<Button>("VolumeButton");
            muteButton = Find<Button>("MuteButton");
            volumeButton.Click += delegate { if (volumeOpen) CloseVolume(); else OpenVolume(); };
            muteButton.Click += delegate { Touch(); volume.SetMuted(!volumeState.Muted); RefreshVolume(); };
            volumeTarget.MouseLeftButtonDown += OnVolumeTargetClick;
            volumeTrack.MouseLeftButtonDown += OnVolumeDown;
            volumeTrack.MouseMove += OnVolumeMove;
            volumeTrack.MouseLeftButtonUp += OnVolumeUp;
            volumeTrack.LostMouseCapture += delegate { volumeDragging = false; };
            volumeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            volumeTimer.Tick += OnVolumeTimer;
            volume.Changed += delegate { if (!volumeDragging && IsVisible) RefreshVolume(); };

            progressTimer = new DispatcherTimer(DispatcherPriority.Background);
            progressTimer.Tick += delegate { UpdateProgress(); };
            IsVisibleChanged += delegate
            {
                UpdateProgressTimer();
                if (IsVisible) RefreshVolume();
                else CloseVolume();
            };

            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                var source = HwndSource.FromHwnd(hwnd);
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                source.AddHook(WndProc);
                Native.MakeNonActivating(hwnd);
                Native.WatchSession(hwnd, true);
                ApplyChrome();
                EnsureOnScreen();
            };
            Closed += delegate { if (hwnd != IntPtr.Zero) Native.WatchSession(hwnd, false); };

            ApplyScheme(scheme, false);
            ApplySettings(settings);
            Render(current);
        }

        T Find<T>(string name) where T : class { return view.FindName(name) as T; }

        static void Raise(Action action) { if (action != null) action(); }

        public ColorScheme Scheme { get { return scheme; } }

        public IntPtr Handle { get { return hwnd; } }

        public void ApplySettings(Settings s)
        {
            settings = s;
            Width = Math.Round(s.BarWidth * s.Scale);
            Height = Math.Round(BarHeight * s.Scale);
            zoom.ScaleX = zoom.ScaleY = s.Scale;
            Topmost = s.AlwaysOnTop;
            visualizerHost.Visibility = s.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;
            seekZone.Visibility = s.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            volume.PreferApp = s.VolumeTarget == "app";
            ApplyChrome();
            ApplyScheme(scheme, false);   // the opacity lives in the background brush
            shownRatio = -1;
            UpdateProgress();
            UpdateProgressTimer();
            if (IsVisible) RefreshVolume();
        }

        void ApplyChrome()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.ApplyFrame(hwnd, scheme.Dark);
            Native.ApplyBackdrop(hwnd, settings.Blur && settings.Opacity < 0.995);
        }

        public void ApplyScheme(ColorScheme next, bool animate)
        {
            scheme = next;
            Color[] target = Palette(next);
            if (hwnd != IntPtr.Zero) Native.ApplyFrame(hwnd, next.Dark);
            if (!animate)
            {
                StopFade();
                WriteBrushes(target);
                return;
            }
            fadeFrom = (Color[])colors.Clone();
            fadeTo = target;
            fadeClock.Restart();
            if (!fading)
            {
                fading = true;
                CompositionTarget.Rendering += OnFadeFrame;
            }
        }

        Color[] Palette(ColorScheme s)
        {
            byte alpha = (byte)Math.Round(settings.Opacity * 255);
            return new[]
            {
                Color.FromArgb(alpha, s.Background.R, s.Background.G, s.Background.B),
                s.Foreground, s.Secondary, s.Accent, s.AccentForeground, s.Hover, s.Pressed,
                // The volume panel has no blur behind it: it stays nearly opaque to remain readable.
                Color.FromArgb((byte)Math.Max(alpha, (byte)247), s.Background.R, s.Background.G, s.Background.B)
            };
        }

        void OnFadeFrame(object sender, EventArgs e)
        {
            double t = Math.Min(1, fadeClock.Elapsed.TotalSeconds / ColorFadeSeconds);
            double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            var frame = new Color[fadeTo.Length];
            for (int i = 0; i < frame.Length; i++) frame[i] = Lerp(fadeFrom[i], fadeTo[i], eased);
            WriteBrushes(frame);
            if (t >= 1) StopFade();
        }

        void StopFade()
        {
            if (!fading) return;
            fading = false;
            CompositionTarget.Rendering -= OnFadeFrame;
        }

        static Color Lerp(Color a, Color b, double t)
        {
            Func<byte, byte, byte> mix = (x, y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromArgb(mix(a.A, b.A), mix(a.R, b.R), mix(a.G, b.G), mix(a.B, b.B));
        }

        void WriteBrushes(Color[] values)
        {
            for (int i = 0; i < BrushKeys.Length; i++)
            {
                if (colors[i] == values[i] && Resources.Contains(BrushKeys[i])) continue;
                colors[i] = values[i];
                var brush = new SolidColorBrush(values[i]);
                brush.Freeze();
                Resources[BrushKeys[i]] = brush;
                if (i == 3 && visualizer != null) visualizer.Brush = brush;
            }
        }

        public void SetCover(BitmapSource image)
        {
            ImageBrush paint = null;
            if (image != null)
            {
                paint = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                paint.Freeze();
            }

            coverFront.BeginAnimation(OpacityProperty, null);
            coverFront.Fill = paint;
            coverFront.Opacity = 0;
            var fade = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(380)));
            fade.Completed += delegate
            {
                if (coverFront.Fill != paint) return;   // a newer cover already took over
                coverBack.Fill = paint;
                coverFront.BeginAnimation(OpacityProperty, null);
                coverFront.Opacity = 0;
            };
            coverFront.BeginAnimation(OpacityProperty, fade);
        }

        public void Render(MediaSnapshot snapshot)
        {
            current = snapshot;
            string title, artist;
            if (!snapshot.HasSession)
            {
                title = "Aucune lecture";
                artist = "En attente de média";
            }
            else
            {
                title = snapshot.Title.Length > 0 ? snapshot.Title : snapshot.App;
                artist = snapshot.Artist.Length > 0 ? snapshot.Artist : snapshot.App;
            }

            if (shownTitle == null) { ShowText(title, artist); }
            else if (title != shownTitle || artist != shownArtist) SwapText(title, artist);

            toggleButton.Content = snapshot.Playing ? PauseGlyph : PlayGlyph;
            toggleButton.ToolTip = snapshot.Playing ? "Pause" : "Lecture";
            previousButton.IsEnabled = snapshot.HasSession && snapshot.CanPrevious;
            nextButton.IsEnabled = snapshot.HasSession && snapshot.CanNext;
            toggleButton.IsEnabled = snapshot.HasSession && snapshot.CanToggle;
            if (!CanSeek && seeking) seekZone.ReleaseMouseCapture();
            cover.Cursor = snapshot.HasSession && snapshot.App.Length > 0 ? Cursors.Hand : null;
            cover.ToolTip = snapshot.HasSession && snapshot.App.Length > 0 ? "Ouvrir " + snapshot.App : null;
            UpdateSeekLook();
            UpdateProgress();
            UpdateProgressTimer();
        }

        // The timeline only needs a new frame when it moves by a pixel: the tick follows the track length.
        void UpdateProgressTimer()
        {
            bool run = IsVisible && settings.ShowProgress && current.HasSession && current.Playing && current.Duration > TimeSpan.Zero;
            if (!run) { progressTimer.Stop(); return; }
            double perPixel = current.Duration.TotalMilliseconds / Math.Max(100, settings.BarWidth);
            var interval = TimeSpan.FromMilliseconds(Math.Max(250, Math.Min(1000, perPixel)));
            if (progressTimer.Interval != interval) progressTimer.Interval = interval;
            if (!progressTimer.IsEnabled) progressTimer.Start();
        }

        void ShowText(string title, string artist)
        {
            shownTitle = title;
            shownArtist = artist;
            titleText.Text = title;
            artistText.Text = artist;
            titleText.ToolTip = title;
            artistText.ToolTip = artist;
        }

        void SwapText(string title, string artist)
        {
            shownTitle = title;
            shownArtist = artist;
            var fadeOut = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(130)));
            fadeOut.Completed += delegate
            {
                ShowText(shownTitle, shownArtist);
                info.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(220))));
            };
            info.BeginAnimation(OpacityProperty, fadeOut);
        }

        void UpdateProgress()
        {
            double ratio = 0;
            if (current.HasSession && current.Duration > TimeSpan.Zero)
            {
                TimeSpan position = current.Position;
                if (DateTime.UtcNow < seekHoldUntil)
                {
                    // Windows reports the new position a moment after the jump.
                    position = seekPosition;
                    if (current.Playing) position += DateTime.UtcNow - seekAt;
                }
                else if (current.Playing) position += DateTime.UtcNow - current.PositionAt;
                ratio = Math.Max(0, Math.Min(1, position.TotalSeconds / current.Duration.TotalSeconds));
            }
            if (!seeking) ShowProgressAt(ratio);
        }

        void ShowProgressAt(double ratio)
        {
            double x = Math.Round(ratio * settings.BarWidth);
            if (x == shownRatio) return;
            shownRatio = x;
            progressScale.ScaleX = x / Math.Max(1, settings.BarWidth);
            handleShift.X = Math.Max(0, Math.Min(settings.BarWidth - 10, x - 5));
        }

        bool CanSeek { get { return current.HasSession && current.CanSeek && current.Duration > TimeSpan.Zero; } }

        void UpdateSeekLook()
        {
            bool active = CanSeek && (seekHover || seeking);
            seekZone.Cursor = CanSeek ? Cursors.Hand : null;
            progressRail.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            seekHandle.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            progressFill.Height = active ? 4 : 2;
            progressFill.Margin = new Thickness(0, 0, 0, active ? 3 : 0);   // same center as the 10 px handle
            if (!active) artistText.Text = shownArtist;
        }

        double RatioAt(MouseEventArgs e)
        {
            return Math.Max(0, Math.Min(1, e.GetPosition(seekZone).X / Math.Max(1, seekZone.ActualWidth)));
        }

        static string FormatTime(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }

        void ShowTimecode(double ratio)
        {
            artistText.Text = FormatTime(TimeSpan.FromSeconds(ratio * current.Duration.TotalSeconds)) + " / " + FormatTime(current.Duration);
        }

        void OnSeekDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;       // the timeline must not start moving the window
            if (!CanSeek) return;
            seeking = true;
            seekZone.CaptureMouse();
            UpdateSeekLook();
            ShowProgressAt(RatioAt(e));
            ShowTimecode(RatioAt(e));
        }

        void OnSeekMove(object sender, MouseEventArgs e)
        {
            if (!CanSeek) return;
            double ratio = RatioAt(e);
            if (seeking) ShowProgressAt(ratio);
            if (seeking || seekHover) ShowTimecode(ratio);
        }

        void OnSeekUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!seeking) return;
            double ratio = RatioAt(e);
            seeking = false;
            seekZone.ReleaseMouseCapture();
            seekPosition = TimeSpan.FromSeconds(ratio * current.Duration.TotalSeconds);
            seekAt = DateTime.UtcNow;
            seekHoldUntil = seekAt.AddSeconds(2);
            ShowProgressAt(ratio);
            if (SeekRequested != null) SeekRequested(seekPosition);
        }

        public void SetVisualizerActive(bool active)
        {
            visualizer.SetActive(active);
        }

        static bool IsInside(FrameworkElement container, object source)
        {
            var node = source as DependencyObject;
            return node != null && (node == container || container.IsAncestorOf(node));
        }

        // A press on the bar moves it; a click on the cover that does not move it opens the app.
        void OnBarPress(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (IsInside((FrameworkElement)volumePopup.Child, e.OriginalSource)) { e.Handled = true; Touch(); return; }
            bool onCover = IsInside(cover, e.OriginalSource);
            bool moved = false;
            if (!settings.LockPosition)
            {
                CloseVolume();   // the panel would stay behind while the bar moves
                double left = Left, top = Top;
                // Acrylic visibly lags behind a moving window, so it is suspended during the drag.
                bool blurred = settings.Blur && settings.Opacity < 0.995 && hwnd != IntPtr.Zero;
                if (blurred) Native.ApplyBackdrop(hwnd, false);
                try { DragMove(); }
                catch (InvalidOperationException) { }
                if (blurred) Native.ApplyBackdrop(hwnd, true);
                moved = Math.Abs(Left - left) >= 1 || Math.Abs(Top - top) >= 1;
                if (moved)
                {
                    settings.HasPosition = true;
                    settings.X = Left;
                    settings.Y = Top;
                    Raise(Moved);
                }
            }
            if (onCover && !moved && current.HasSession) Raise(CoverClicked);
        }

        // ---- Window messages: edge magnet while moving, screens, session lock ----

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOVING = 0x0216, WM_DISPLAYCHANGE = 0x007E, WM_WTSSESSION_CHANGE = 0x02B1;
            if (msg == WM_MOVING) SnapWhileMoving(lParam);
            else if (msg == WM_DISPLAYCHANGE)
                Dispatcher.BeginInvoke(new Action(EnsureOnScreen), DispatcherPriority.Background);
            else if (msg == WM_WTSSESSION_CHANGE)
            {
                int code = wParam.ToInt32();
                if ((code == 7 || code == 8) && SessionLockChanged != null) SessionLockChanged(code == 7);   // lock, unlock
            }
            return IntPtr.Zero;
        }

        // Near an edge (12 px from it) or the middle of the screen, the bar settles there.
        void SnapWhileMoving(IntPtr lParam)
        {
            var rect = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(Native.RECT));
            var source = HwndSource.FromHwnd(hwnd);
            double dpi = source != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
            int gap = (int)Math.Round(12 * dpi), reach = (int)Math.Round(14 * dpi);
            Native.RECT work = Native.WorkAreaOf(rect);
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            int x = rect.Left, y = rect.Top;

            int centerX = work.Left + (work.Right - work.Left - width) / 2;
            if (Math.Abs(x - (work.Left + gap)) <= reach) x = work.Left + gap;
            else if (Math.Abs(x + width - (work.Right - gap)) <= reach) x = work.Right - gap - width;
            else if (Math.Abs(x - centerX) <= reach) x = centerX;
            if (Math.Abs(y - (work.Top + gap)) <= reach) y = work.Top + gap;
            else if (Math.Abs(y + height - (work.Bottom - gap)) <= reach) y = work.Bottom - gap - height;

            if (x == rect.Left && y == rect.Top) return;
            rect.Left = x; rect.Top = y; rect.Right = x + width; rect.Bottom = y + height;
            System.Runtime.InteropServices.Marshal.StructureToPtr(rect, lParam, false);
        }

        // ---- Volume ----

        static string VolumeGlyph(VolumeState s)
        {
            if (s.Muted) return "\uE74F";
            if (s.Level < 0.01) return "\uE992";
            if (s.Level < 0.34) return "\uE993";
            if (s.Level < 0.67) return "\uE994";
            return "\uE995";
        }

        public void RefreshVolume()
        {
            volumeState = volume.Read();
            ShowVolume();
        }

        void ShowVolume()
        {
            VolumeState s = volumeState;
            int percent = (int)Math.Round(s.Level * 100);
            string glyph = VolumeGlyph(s);
            if (!Equals(volumeButton.Content, glyph)) volumeButton.Content = glyph;
            volumeButton.IsEnabled = s.Available;
            volumeButton.ToolTip = !s.Available ? "Aucune sortie audio"
                : "Volume " + (s.IsApp ? "de " + s.Name : "système") + " : " + (s.Muted ? "coupé" : percent + " %");
            if (!volumeOpen) return;

            muteButton.Content = glyph;
            muteButton.ToolTip = s.Muted ? "Rétablir le son" : "Couper le son";
            volumeValue.Text = s.Muted ? "Coupé" : percent + " %";
            volumeTarget.Text = s.IsApp ? s.Name : "Système";
            volumeTarget.ToolTip = s.IsApp ? "Volume de " + s.Name + " seulement. Cliquer pour régler tout le système."
                : settings.VolumeTarget == "app" ? "L\u2019application n\u2019a pas de son actif : volume système."
                : "Volume de tout le système. Cliquer pour régler l\u2019application seule.";
            if (!volumeDragging) ShowVolumeAt(s.Muted ? 0 : s.Level);
        }

        void ShowVolumeAt(double ratio)
        {
            double width = Math.Max(1, volumeTrack.ActualWidth);
            double x = Math.Round(Math.Max(0, Math.Min(1, ratio)) * width);
            volumeFill.Width = x;
            volumeKnob.Margin = new Thickness(Math.Max(0, Math.Min(width - 10, x - 5)), 0, 0, 0);
        }

        void Touch() { volumeTouched = DateTime.UtcNow; }

        // Right-aligned with the bar, 6 px above it; below it when the screen top is too close.
        System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceVolume(Size popup, Size target, Point offset)
        {
            double gap = 6 * settings.Scale;
            double x = target.Width - popup.Width;
            return new[]
            {
                new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(x, -popup.Height - gap), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
                new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(x, target.Height + gap), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
            };
        }

        void OpenVolume()
        {
            Touch();
            if (volumeOpen) return;
            volumeOpen = true;
            volumePopup.IsOpen = true;
            volumePanel.UpdateLayout();     // the track needs its width before the knob is placed
            RefreshVolume();
            volumeTimer.Start();
        }

        void CloseVolume()
        {
            if (!volumeOpen) return;
            volumeOpen = false;
            volumeTimer.Stop();
            if (volumeDragging) volumeTrack.ReleaseMouseCapture();
            volumePopup.IsOpen = false;
        }

        // Closes by itself once left alone; while open, it follows changes made elsewhere.
        void OnVolumeTimer(object sender, EventArgs e)
        {
            bool hovered = volumePanel.IsMouseOver || volumeButton.IsMouseOver || view.IsMouseOver;
            if (!volumeDragging && !hovered && (DateTime.UtcNow - volumeTouched).TotalSeconds > 2.5) { CloseVolume(); return; }
            if (!volumeDragging) RefreshVolume();
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (!volumeState.Available) RefreshVolume();
            if (!volumeState.Available) return;
            double step = 0.02 * e.Delta / 120.0;
            double from = volumeState.Muted && step > 0 ? 0 : volumeState.Level;
            volume.SetLevel((float)Math.Round(Math.Max(0, Math.Min(1, from + step)), 2));
            OpenVolume();
            RefreshVolume();
        }

        double VolumeRatioAt(MouseEventArgs e)
        {
            return Math.Max(0, Math.Min(1, e.GetPosition(volumeTrack).X / Math.Max(1, volumeTrack.ActualWidth)));
        }

        void OnVolumeDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Touch();
            volumeDragging = true;
            volumeTrack.CaptureMouse();
            ApplyVolumeAt(VolumeRatioAt(e));
        }

        void OnVolumeMove(object sender, MouseEventArgs e)
        {
            if (!volumeDragging) return;
            Touch();
            ApplyVolumeAt(VolumeRatioAt(e));
        }

        void OnVolumeUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!volumeDragging) return;
            ApplyVolumeAt(VolumeRatioAt(e));
            volumeDragging = false;
            volumeTrack.ReleaseMouseCapture();
            RefreshVolume();
        }

        void ApplyVolumeAt(double ratio)
        {
            float level = (float)Math.Round(ratio, 2);
            ShowVolumeAt(level);
            volume.SetLevel(level);
            volumeState.Level = level;
            volumeState.Muted = volumeState.Muted && level == 0;
            int percent = (int)Math.Round(level * 100);
            volumeValue.Text = percent + " %";
            volumeButton.Content = muteButton.Content = VolumeGlyph(volumeState);
        }

        void OnVolumeTargetClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Touch();
            settings.VolumeTarget = settings.VolumeTarget == "app" ? "system" : "app";
            volume.PreferApp = settings.VolumeTarget == "app";
            RefreshVolume();
            Raise(VolumeTargetChanged);
        }

        public void Recenter()
        {
            settings.HasPosition = false;
            PlaceDefault();
        }

        void PlaceDefault()
        {
            Rect area = SystemParameters.WorkArea;
            Left = Math.Round(area.Left + (area.Width - Width) / 2);
            Top = Math.Round(area.Bottom - Height - 18);
        }

        // A saved position can point at a monitor that is no longer connected.
        public void EnsureOnScreen()
        {
            if (!settings.HasPosition) { PlaceDefault(); return; }
            Left = settings.X;
            Top = settings.Y;
            var source = hwnd != IntPtr.Zero ? HwndSource.FromHwnd(hwnd) : null;
            Matrix toDevice = source != null ? source.CompositionTarget.TransformToDevice : Matrix.Identity;
            Point center = toDevice.Transform(new Point(Left + Width / 2, Top + Height / 2));
            var pixel = new System.Drawing.Point((int)center.X, (int)center.Y);
            if (!WinForms.Screen.AllScreens.Any(s => s.WorkingArea.Contains(pixel))) PlaceDefault();
        }
    }

    // Draws the frequency bands captured from the sound card, about 30 times a second: smooth
    // enough for the eye and half the work of following the 60 Hz display.
    class Visualizer : FrameworkElement
    {
        const double Gap = 2.5, MinBar = 3;
        // Windows timers tick every 15.6 ms: 31 ms lands on two ticks (32 fps), 33 ms would need three (21 fps).
        static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(31);

        readonly AudioCapture audio;
        readonly double[] target = new double[AudioCapture.BandCount];
        readonly double[] shown = new double[AudioCapture.BandCount];
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly DispatcherTimer timer;
        // Redrawing into a drawing group updates the screen without a layout pass.
        readonly DrawingGroup drawing = new DrawingGroup();
        Brush brush;
        double lastTick;
        bool active;
        static int frames;

        public static int TakeFrameCount() { return System.Threading.Interlocked.Exchange(ref frames, 0); }

        public Visualizer(AudioCapture audio, Brush brush)
        {
            this.audio = audio;
            this.brush = brush;
            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
            timer.Tick += OnFrame;
        }

        public Brush Brush
        {
            get { return brush; }
            set { brush = value; if (!timer.IsEnabled) Redraw(); }
        }

        public void SetActive(bool value)
        {
            active = value;
            if (!timer.IsEnabled && (active || Array.Exists(shown, v => v > 0)))
            {
                lastTick = clock.Elapsed.TotalSeconds;
                timer.Start();
            }
        }

        void OnFrame(object sender, EventArgs e)
        {
            // The smoothing follows the real time elapsed, whatever the actual frame rate.
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.1, now - lastTick);
            lastTick = now;

            frames++;
            if (active) audio.Read(target);
            else Array.Clear(target, 0, target.Length);

            double rise = 1 - Math.Exp(-dt / 0.025), fall = 1 - Math.Exp(-dt / 0.14);
            bool moving = false;
            double change = 0;
            for (int i = 0; i < shown.Length; i++)
            {
                double before = shown[i];
                shown[i] += (target[i] - shown[i]) * (target[i] > shown[i] ? rise : fall);
                if (shown[i] < 0.002) shown[i] = 0;
                if (shown[i] > 0) moving = true;
                change = Math.Max(change, Math.Abs(shown[i] - before));
            }

            if (!active && !moving) { timer.Stop(); Redraw(); return; }
            // Silence, or a sound that holds still: the last frame is still right, nothing to draw.
            if (change * ActualHeight >= 0.25) Redraw();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            Redraw();
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawDrawing(drawing);
        }

        void Redraw()
        {
            double width = ActualWidth, height = ActualHeight;
            using (DrawingContext dc = drawing.Open())
            {
                if (width < 1 || height < 1) return;
                int count = shown.Length;
                double bar = (width - Gap * (count - 1)) / count;
                double radius = Math.Min(bar / 2, 1.5);
                for (int i = 0; i < count; i++)
                {
                    double h = Math.Max(MinBar, shown[i] * height);
                    dc.DrawRoundedRectangle(brush, null, new Rect(i * (bar + Gap), (height - h) / 2, bar, h), radius, radius);
                }
            }
        }
    }
}