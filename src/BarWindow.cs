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
        static readonly string[] BrushKeys = { "BarBg", "BarFg", "BarFg2", "BarAccent", "BarAccentFg", "BarHover", "BarPressed" };

        // Resource brushes get frozen by WPF, so a fade replaces them frame by frame.
        readonly Color[] colors = new Color[BrushKeys.Length];
        Color[] fadeFrom, fadeTo;
        readonly Stopwatch fadeClock = new Stopwatch();
        bool fading;

        readonly FrameworkElement view, info, visualizerHost, progressFill, seekZone, progressRail, seekHandle;
        bool seekHover, seeking;
        TimeSpan seekPosition;
        DateTime seekAt = DateTime.MinValue, seekHoldUntil = DateTime.MinValue;

        readonly TextBlock titleText, artistText;
        readonly Rectangle coverBack, coverFront;
        readonly Button previousButton, toggleButton, nextButton;
        readonly ScaleTransform zoom = new ScaleTransform();
        readonly Visualizer visualizer;
        readonly DispatcherTimer progressTimer;

        Settings settings;
        ColorScheme scheme = ColorScheme.Neutral();
        MediaSnapshot current = new MediaSnapshot();
        IntPtr hwnd;
        string shownTitle, shownArtist;

        public event Action SettingsRequested;
        public event Action TogglePressed, NextPressed, PreviousPressed;
        public event Action Moved;
        public event Action<TimeSpan> SeekRequested;

        public BarWindow(Settings settings, AudioCapture audio)
        {
            this.settings = settings;
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
            view.MouseLeftButtonDown += OnDragStart;

            progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            progressTimer.Tick += delegate { UpdateProgress(); };
            progressTimer.Start();

            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.MakeNonActivating(hwnd);
                ApplyChrome();
                EnsureOnScreen();
            };

            ApplyScheme(scheme, false);
            ApplySettings(settings);
            Render(current);
        }

        T Find<T>(string name) where T : class { return view.FindName(name) as T; }

        static void Raise(Action action) { if (action != null) action(); }

        public ColorScheme Scheme { get { return scheme; } }

        public void ApplySettings(Settings s)
        {
            settings = s;
            Width = Math.Round(s.BarWidth * s.Scale);
            Height = Math.Round(BarHeight * s.Scale);
            zoom.ScaleX = zoom.ScaleY = s.Scale;
            Topmost = s.AlwaysOnTop;
            visualizerHost.Visibility = s.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;
            seekZone.Visibility = s.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            ApplyChrome();
            ApplyScheme(scheme, false);   // the opacity lives in the background brush
            UpdateProgress();
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
                s.Foreground, s.Secondary, s.Accent, s.AccentForeground, s.Hover, s.Pressed
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
            UpdateSeekLook();
            UpdateProgress();
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
            progressFill.Width = x;
            seekHandle.Margin = new Thickness(Math.Max(0, Math.Min(settings.BarWidth - 10, x - 5)), 0, 0, 0);
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

        void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (settings.LockPosition || e.ChangedButton != MouseButton.Left) return;
            // Acrylic visibly lags behind a moving window, so it is suspended during the drag.
            bool blurred = settings.Blur && settings.Opacity < 0.995 && hwnd != IntPtr.Zero;
            if (blurred) Native.ApplyBackdrop(hwnd, false);
            try { DragMove(); }
            catch (InvalidOperationException) { }
            if (blurred) Native.ApplyBackdrop(hwnd, true);
            settings.HasPosition = true;
            settings.X = Left;
            settings.Y = Top;
            Raise(Moved);
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
            if (!timer.IsEnabled && (active || shown.Any(v => v > 0.002)))
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
            for (int i = 0; i < shown.Length; i++)
            {
                shown[i] += (target[i] - shown[i]) * (target[i] > shown[i] ? rise : fall);
                if (shown[i] > 0.002) moving = true;
            }

            if (!active && !moving)
            {
                Array.Clear(shown, 0, shown.Length);
                timer.Stop();
            }
            Redraw();
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