using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace FloatingPlayer
{
    // Edits the live settings: every change applies to the bar and is saved right away.
    class SettingsWindow : Window
    {
        public event Action Changed;
        public event Action Recenter;
        public event Action Quit;

        static readonly string[] Palette =
        {
            "#2B4C6F", "#24564F", "#3B5A2E", "#6B4A2B", "#6E2F35", "#4F3A6E", "#2A2C31", "#D9D4C7"
        };

        readonly Settings settings;
        readonly FrameworkElement view, customRow, sensitivityRow;
        readonly WrapPanel swatches;
        readonly RadioButton colorCover, colorCustom, hideNever, hideNoMedia, hidePaused;
        readonly Slider opacitySlider, scaleSlider, widthSlider, sensitivitySlider;
        readonly TextBlock opacityValue, scaleValue, widthValue, sensitivityValue;
        readonly CheckBox blurSwitch, visualizerSwitch, progressSwitch, topmostSwitch, lockSwitch, launchSwitch;
        readonly TextBox hexBox;
        readonly Color cardColor;
        bool loading;

        public SettingsWindow(Settings settings)
        {
            this.settings = settings;
            Title = "Paramètres · Floating Player";
            Width = 372;
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height - 24;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            UseLayoutRounding = true;

            bool dark = IsSystemDark();
            cardColor = dark ? Color.FromRgb(0x2B, 0x2B, 0x2E) : Colors.White;
            SetBrush("WinBg", dark ? Color.FromRgb(0x20, 0x20, 0x22) : Color.FromRgb(0xF3, 0xF3, 0xF4));
            SetBrush("CardBg", cardColor);
            SetBrush("CardStroke", dark ? Color.FromRgb(0x3A, 0x3A, 0x3E) : Color.FromRgb(0xE3, 0xE3, 0xE6));
            SetBrush("Fg1", dark ? Color.FromRgb(0xF2, 0xF2, 0xF3) : Color.FromRgb(0x1A, 0x1A, 0x1C));
            SetBrush("Fg2", dark ? Color.FromRgb(0xA9, 0xA9, 0xAF) : Color.FromRgb(0x62, 0x62, 0x68));
            SetBrush("ControlBg", dark ? Color.FromRgb(0x35, 0x35, 0x39) : Color.FromRgb(0xF7, 0xF7, 0xF8));
            SetBrush("ControlHover", dark ? Color.FromRgb(0x40, 0x40, 0x45) : Color.FromRgb(0xEE, 0xEE, 0xF0));
            SetBrush("ControlStroke", dark ? Color.FromRgb(0x48, 0x48, 0x4D) : Color.FromRgb(0xD6, 0xD6, 0xDA));
            SetBrush("TrackStroke", dark ? Color.FromRgb(0x77, 0x77, 0x7D) : Color.FromRgb(0x8E, 0x8E, 0x94));
            SetBrush("TrackRail", dark ? Color.FromRgb(0x55, 0x55, 0x5A) : Color.FromRgb(0xC9, 0xC9, 0xCE));
            SetBrush("ThumbBg", dark ? Color.FromRgb(0x52, 0x52, 0x58) : Colors.White);
            SetBrush("Hover", dark ? Color.FromArgb(0x24, 255, 255, 255) : Color.FromArgb(0x12, 0, 0, 0));
            SetAccent(ColorScheme.Neutral().Accent);

            view = Ui_Load();
            Content = view;

            customRow = Find<FrameworkElement>("CustomRow");
            sensitivityRow = Find<FrameworkElement>("SensitivityRow");
            swatches = Find<WrapPanel>("Swatches");
            hexBox = Find<TextBox>("HexBox");
            colorCover = Find<RadioButton>("ColorCover");
            colorCustom = Find<RadioButton>("ColorCustom");
            hideNever = Find<RadioButton>("HideNever");
            hideNoMedia = Find<RadioButton>("HideNoMedia");
            hidePaused = Find<RadioButton>("HidePaused");
            opacitySlider = Find<Slider>("OpacitySlider");
            scaleSlider = Find<Slider>("ScaleSlider");
            widthSlider = Find<Slider>("WidthSlider");
            sensitivitySlider = Find<Slider>("SensitivitySlider");
            opacityValue = Find<TextBlock>("OpacityValue");
            scaleValue = Find<TextBlock>("ScaleValue");
            widthValue = Find<TextBlock>("WidthValue");
            sensitivityValue = Find<TextBlock>("SensitivityValue");
            blurSwitch = Find<CheckBox>("BlurSwitch");
            visualizerSwitch = Find<CheckBox>("VisualizerSwitch");
            progressSwitch = Find<CheckBox>("ProgressSwitch");
            topmostSwitch = Find<CheckBox>("TopmostSwitch");
            lockSwitch = Find<CheckBox>("LockSwitch");
            launchSwitch = Find<CheckBox>("LaunchSwitch");

            OnChecked(colorCover, () => settings.ColorMode = "cover");
            OnChecked(colorCustom, () => settings.ColorMode = "custom");
            OnChecked(hideNever, () => settings.HideMode = "never");
            OnChecked(hideNoMedia, () => settings.HideMode = "nomedia");
            OnChecked(hidePaused, () => settings.HideMode = "paused");
            OnSlide(opacitySlider, v => settings.Opacity = v / 100);
            OnSlide(scaleSlider, v => settings.Scale = v / 100);
            OnSlide(widthSlider, v => settings.BarWidth = v);
            OnSlide(sensitivitySlider, v => settings.Sensitivity = v / 100);
            OnToggle(blurSwitch, v => settings.Blur = v);
            OnToggle(visualizerSwitch, v => settings.ShowVisualizer = v);
            OnToggle(progressSwitch, v => settings.ShowProgress = v);
            OnToggle(topmostSwitch, v => settings.AlwaysOnTop = v);
            OnToggle(lockSwitch, v => settings.LockPosition = v);
            OnToggle(launchSwitch, v => { settings.LaunchAtStartup = v; Startup.Apply(v); });

            hexBox.TextChanged += delegate
            {
                if (loading) return;
                Color parsed;
                if (!ColorMath.TryParse(hexBox.Text, out parsed)) return;
                settings.CustomColor = ColorMath.ToHex(parsed);
                Notify(false);
            };

            Find<Button>("CloseButton").Click += delegate { Close(); };
            Find<Button>("RecenterButton").Click += delegate { if (Recenter != null) Recenter(); };
            Find<Button>("QuitButton").Click += delegate { if (Quit != null) Quit(); };
            Find<Button>("ResetButton").Click += delegate
            {
                var defaults = new Settings();
                defaults.LaunchAtStartup = settings.LaunchAtStartup;
                settings.CopyFrom(defaults);
                LoadValues();
                Notify(true);
                if (Recenter != null) Recenter();
            };
            Find<FrameworkElement>("Header").MouseLeftButtonDown += (s, e) =>
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            SourceInitialized += delegate { Native.ApplyFrame(new WindowInteropHelper(this).Handle, dark); };

            LoadValues();
        }

        static FrameworkElement Ui_Load() { return Ui.Load("Settings.xaml"); }

        T Find<T>(string name) where T : class { return view.FindName(name) as T; }

        void SetBrush(string key, Color color) { Resources[key] = new SolidColorBrush(color); }

        public void SetAccent(Color color)
        {
            Color accent = ColorMath.EnsureContrast(color, cardColor, 3.0);
            SetBrush("Accent", accent);
            SetBrush("AccentFg", ColorMath.Contrast(accent, Colors.White) >= ColorMath.Contrast(accent, Colors.Black)
                ? Colors.White : Color.FromRgb(0x10, 0x10, 0x10));
        }

        static bool IsSystemDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { return false; }
        }

        void LoadValues()
        {
            loading = true;
            colorCover.IsChecked = settings.ColorMode == "cover";
            colorCustom.IsChecked = settings.ColorMode == "custom";
            hideNever.IsChecked = settings.HideMode == "never";
            hideNoMedia.IsChecked = settings.HideMode == "nomedia";
            hidePaused.IsChecked = settings.HideMode == "paused";
            opacitySlider.Value = Math.Round(settings.Opacity * 100);
            scaleSlider.Value = Math.Round(settings.Scale * 100);
            widthSlider.Value = Math.Round(settings.BarWidth);
            sensitivitySlider.Value = Math.Round(settings.Sensitivity * 100);
            blurSwitch.IsChecked = settings.Blur;
            visualizerSwitch.IsChecked = settings.ShowVisualizer;
            progressSwitch.IsChecked = settings.ShowProgress;
            topmostSwitch.IsChecked = settings.AlwaysOnTop;
            lockSwitch.IsChecked = settings.LockPosition;
            launchSwitch.IsChecked = settings.LaunchAtStartup;
            hexBox.Text = settings.CustomColor;
            loading = false;
            UpdateLabels();
        }

        void UpdateLabels()
        {
            opacityValue.Text = Math.Round(settings.Opacity * 100) + " %";
            scaleValue.Text = Math.Round(settings.Scale * 100) + " %";
            widthValue.Text = Math.Round(settings.BarWidth) + " px";
            sensitivityValue.Text = Math.Round(settings.Sensitivity * 100) + " %";
            customRow.Visibility = settings.ColorMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
            sensitivityRow.IsEnabled = settings.ShowVisualizer;
            sensitivityRow.Opacity = settings.ShowVisualizer ? 1 : 0.45;
            BuildSwatches();
        }

        void Notify(bool relabel)
        {
            if (loading) return;
            settings.Normalize();
            if (relabel) UpdateLabels();
            if (Changed != null) Changed();
        }

        void BuildSwatches()
        {
            swatches.Children.Clear();
            foreach (string hex in Palette)
            {
                string value = hex;
                Color color;
                ColorMath.TryParse(value, out color);
                bool selected = string.Equals(settings.CustomColor, value, StringComparison.OrdinalIgnoreCase);
                var fill = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(color), BorderThickness = new Thickness(1) };
                fill.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
                var ring = new Border
                {
                    Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Padding = new Thickness(2), Margin = new Thickness(0, 0, 2, 0),
                    BorderThickness = new Thickness(1.5), Child = fill, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = value
                };
                if (selected) ring.SetResourceReference(Border.BorderBrushProperty, "Fg1");
                else ring.BorderBrush = Brushes.Transparent;
                ring.MouseLeftButtonUp += delegate
                {
                    settings.CustomColor = value;
                    loading = true;
                    hexBox.Text = value;
                    loading = false;
                    Notify(true);
                };
                swatches.Children.Add(ring);
            }
        }

        void OnChecked(RadioButton button, Action apply)
        {
            button.Checked += delegate { if (loading) return; apply(); Notify(true); };
        }

        void OnSlide(Slider slider, Action<double> apply)
        {
            slider.ValueChanged += delegate { if (loading) return; apply(Math.Round(slider.Value)); Notify(true); };
        }

        void OnToggle(CheckBox box, Action<bool> apply)
        {
            box.Checked += delegate { if (loading) return; apply(true); Notify(true); };
            box.Unchecked += delegate { if (loading) return; apply(false); Notify(true); };
        }

        // Opens next to the bar, on whichever side has room.
        public void PlaceNear(Window bar)
        {
            Rect area = SystemParameters.WorkArea;
            UpdateLayout();
            double height = ActualHeight;
            double left = Math.Max(area.Left + 8, Math.Min(bar.Left + bar.Width - Width, area.Right - Width - 8));
            double top = bar.Top - height - 10;
            if (top < area.Top + 8) top = bar.Top + bar.Height + 10;
            if (top + height > area.Bottom - 8) top = Math.Max(area.Top + 8, area.Bottom - height - 8);
            Left = left;
            Top = top;
        }
    }
}
