using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

[assembly: AssemblyTitle("Floating Player")]
[assembly: AssemblyProduct("Floating Player")]
[assembly: AssemblyCompany("Floating Player")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace FloatingPlayer
{
    static class Program
    {
        const string InstanceMutex = @"Local\FloatingPlayer.Instance";
        const string QuitEvent = @"Global\FloatingPlayer.Quit";

        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);
            Func<string, bool> has = flag => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

            if (has("--quit")) { StopRunningInstances(); return 0; }

            string demo = args.FirstOrDefault(a => a.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase));
            if (demo != null)
            {
                new Player(new Settings { Opacity = 1 }, has("--settings"), false, Demo.Snapshot(demo.Substring(7))).Run();
                return 0;
            }

            var settings = Settings.Load();
            if (has("--autostart") && !settings.LaunchAtStartup) return 0;

            bool created;
            using (var mutex = new Mutex(true, InstanceMutex, out created))
            {
                if (!created) return 0;
                new Player(settings, has("--settings"), has("--diag"), null).Run();
            }
            return 0;
        }

        // Used by the installer before uninstalling or upgrading: ask politely, then force.
        static void StopRunningInstances()
        {
            try
            {
                EventWaitHandle signal;
                if (EventWaitHandle.TryOpenExisting(QuitEvent, out signal)) using (signal) signal.Set();
            }
            catch { }

            int self = Process.GetCurrentProcess().Id;
            DateTime deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                if (!Process.GetProcessesByName("FloatingPlayer").Any(p => p.Id != self)) return;
                Thread.Sleep(150);
            }
            foreach (var p in Process.GetProcessesByName("FloatingPlayer"))
                if (p.Id != self) try { p.Kill(); } catch { }
        }

        class Player
        {
            const double HideDelaySeconds = 4;

            readonly Settings settings;
            readonly bool openSettings, diagnostics;
            readonly MediaSnapshot demo;
            readonly AudioCapture audio = new AudioCapture();
            Application app;
            BarWindow bar;
            MediaController media;
            VolumeControl volume;
            WinForms.ContextMenuStrip barMenu;
            SettingsWindow settingsWindow;
            WinForms.NotifyIcon tray;
            WinForms.ToolStripMenuItem toggleItem;
            DispatcherTimer saveTimer;
            MediaSnapshot snapshot = new MediaSnapshot();
            ColorScheme coverScheme = ColorScheme.Neutral();
            string artKey = "";
            bool userHidden, sessionLocked;
            DateTime lastWanted = DateTime.MinValue;

            public Player(Settings settings, bool openSettings, bool diagnostics, MediaSnapshot demo)
            {
                this.demo = demo;
                this.settings = settings;
                this.openSettings = openSettings;
                this.diagnostics = diagnostics;
            }

            public void Run()
            {
                WinForms.Application.EnableVisualStyles();
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.SessionEnding += delegate { app.Shutdown(); };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Write("UI: " + e.Exception);
                    e.Handled = true;
                };

                audio.Sensitivity = settings.Sensitivity;
                volume = new VolumeControl(app.Dispatcher) { PreferApp = settings.VolumeTarget == "app" };
                bar = new BarWindow(settings, audio, volume);
                bar.CoverClicked += OpenSourceApp;
                bar.ContextRequested += ShowBarMenu;
                bar.VolumeTargetChanged += delegate { QueueSave(); if (settingsWindow != null) settingsWindow.Reload(); };
                bar.SessionLockChanged += locked => { sessionLocked = locked; UpdateVisibility(); };
                bar.TogglePressed += delegate { media.Toggle(); };
                bar.NextPressed += delegate { media.Next(); };
                bar.PreviousPressed += delegate { media.Previous(); };
                bar.SeekRequested += position => media.Seek(position);
                bar.SettingsRequested += ToggleSettings;
                bar.Moved += delegate { QueueSave(); };

                saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                saveTimer.Tick += delegate { saveTimer.Stop(); settings.Save(); };

                media = new MediaController(app.Dispatcher);
                media.Changed += OnMedia;

                var visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                visibilityTimer.Tick += delegate { UpdateVisibility(); };
                visibilityTimer.Start();

                if (diagnostics)
                {
                    var diag = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    diag.Tick += delegate { WriteDiagnostics(); };
                    diag.Start();
                }

                CreateTray();
                WatchQuitSignal();
                UpdateVisibility();
                if (demo != null) app.Dispatcher.BeginInvoke(new Action(() => OnMedia(demo)));
                else media.Start();
                if (openSettings) app.Dispatcher.BeginInvoke(new Action(ToggleSettings), DispatcherPriority.ApplicationIdle);

                app.Run();

                audio.Stop();
                if (demo == null) settings.Save();
                tray.Visible = false;
                tray.Dispose();
            }

            void OnMedia(MediaSnapshot next)
            {
                bool sourceChanged = next.AppId != snapshot.AppId;
                snapshot = next;
                if (sourceChanged)
                {
                    volume.SetSource(next.AppId, next.App);
                    if (bar.IsVisible) bar.RefreshVolume();
                }
                bar.Render(next);
                if (next.ArtKey != artKey)
                {
                    artKey = next.ArtKey;
                    LoadCover(next);
                }
                UpdateVisibility();
            }

            async void LoadCover(MediaSnapshot source)
            {
                string key = source.ArtKey;
                if (source.Art == null)
                {
                    bar.SetCover(null);
                    coverScheme = ColorScheme.Neutral();
                    ApplyColors(true);
                    return;
                }

                BitmapImage image = null;
                ColorScheme scheme = null;
                await Task.Run(() =>
                {
                    try
                    {
                        image = CoverAnalyzer.Decode(source.Art);
                        scheme = CoverAnalyzer.Analyze(image);
                    }
                    catch (Exception e) { Log.Write("Cover: " + e.Message); }
                });
                if (key != artKey) return;   // the track changed again in the meantime

                bar.SetCover(image);
                coverScheme = scheme ?? ColorScheme.Neutral();
                ApplyColors(true);
            }

            void ApplyColors(bool animate)
            {
                System.Windows.Media.Color custom;
                ColorScheme scheme = settings.ColorMode == "custom" && ColorMath.TryParse(settings.CustomColor, out custom)
                    ? ColorScheme.FromCustom(custom)
                    : coverScheme;
                bar.ApplyScheme(scheme, animate);
                if (settingsWindow != null) settingsWindow.SetAccent(scheme.Accent);
            }

            void UpdateVisibility()
            {
                bool wanted = settingsWindow != null
                    || !userHidden && (settings.HideMode == "never"
                        || settings.HideMode == "nomedia" && snapshot.HasSession
                        || settings.HideMode == "paused" && snapshot.HasSession && snapshot.Playing);
                if (userHidden && settingsWindow == null) wanted = false;

                // Sessions blink when a browser changes tab or track, so hiding waits a little.
                if (wanted) lastWanted = DateTime.UtcNow;
                bool show = wanted || (!userHidden && (DateTime.UtcNow - lastWanted).TotalSeconds < HideDelaySeconds && bar.IsVisible);
                // A full-screen video or game on the same screen hides the bar right away.
                if (show && settingsWindow == null && settings.HideInFullscreen && bar.Handle != IntPtr.Zero
                    && Native.IsFullscreenAppOver(bar.Handle)) show = false;

                if (show && !bar.IsVisible) bar.Show();
                else if (!show && bar.IsVisible) bar.Hide();

                string toggleText = bar.IsVisible ? "Masquer la barre" : "Afficher la barre";
                if (toggleItem != null && toggleItem.Text != toggleText) toggleItem.Text = toggleText;

                // Nobody sees the visualizer on a locked session: the capture stops.
                bool listen = bar.IsVisible && !sessionLocked && settings.ShowVisualizer && snapshot.HasSession && snapshot.Playing;
                if (listen) audio.Start(); else audio.Stop();
                bar.SetVisualizerActive(listen);
            }

            void SettingsChanged()
            {
                audio.Sensitivity = settings.Sensitivity;
                bar.ApplySettings(settings);
                ApplyColors(false);
                UpdateVisibility();
                QueueSave();
            }

            void QueueSave()
            {
                if (demo != null) return;
                saveTimer.Stop();
                saveTimer.Start();
            }

            void ToggleSettings()
            {
                if (settingsWindow != null) { settingsWindow.Close(); return; }

                settingsWindow = new SettingsWindow(settings);
                settingsWindow.SetAccent(bar.Scheme.Accent);
                settingsWindow.Changed += SettingsChanged;
                settingsWindow.Recenter += delegate { bar.Recenter(); QueueSave(); };
                settingsWindow.Quit += delegate { app.Shutdown(); };
                settingsWindow.Closed += delegate { settingsWindow = null; UpdateVisibility(); };
                UpdateVisibility();     // keeps the bar on screen while it is being adjusted
                settingsWindow.Loaded += delegate { settingsWindow.PlaceNear(bar); };
                settingsWindow.Show();
                settingsWindow.Activate();
            }

            void OpenSourceApp()
            {
                if (!snapshot.HasSession || snapshot.AppId.Length == 0) return;
                string[] names, packages;
                VolumeControl.Candidates(snapshot.AppId, out names, out packages);
                try
                {
                    if (!Native.ActivateApp(names, snapshot.Title) && snapshot.AppId.IndexOf('!') > 0)
                        Process.Start("explorer.exe", "shell:AppsFolder\\" + snapshot.AppId);
                }
                catch (Exception e) { Log.Write("Open app: " + e.Message); }
            }

            static WinForms.ToolStripMenuItem Item(string text, Action click, bool isChecked = false, bool enabled = true)
            {
                var item = new WinForms.ToolStripMenuItem(text) { Checked = isChecked, Enabled = enabled };
                item.Click += delegate { click(); };
                return item;
            }

            // Right click on the bar: what does not deserve a button of its own.
            void ShowBarMenu()
            {
                if (barMenu != null) barMenu.Dispose();
                barMenu = new WinForms.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
                var items = barMenu.Items;
                bool has = snapshot.HasSession && !snapshot.Retained;

                if (snapshot.HasSession && snapshot.App.Length > 0) items.Add(Item("Ouvrir " + snapshot.App, OpenSourceApp));
                if (snapshot.HasSession && snapshot.Title.Length > 0)
                {
                    string text = snapshot.Artist.Length > 0 ? snapshot.Artist + " \u2013 " + snapshot.Title : snapshot.Title;
                    items.Add(Item("Copier le titre", () =>
                    {
                        try { Clipboard.SetText(text); } catch (Exception e) { Log.Write("Clipboard: " + e.Message); }
                    }));
                }
                if (has && (snapshot.CanShuffle || snapshot.CanRepeat))
                {
                    items.Add(new WinForms.ToolStripSeparator());
                    if (snapshot.CanShuffle)
                        items.Add(Item("Lecture aléatoire", () => media.SetShuffle(!snapshot.Shuffle), snapshot.Shuffle));
                    if (snapshot.CanRepeat)
                    {
                        var repeat = new WinForms.ToolStripMenuItem("Répéter");
                        string[] labels = { "Non", "Le morceau", "Toute la liste" };
                        for (int i = 0; i < labels.Length; i++)
                        {
                            int mode = i;
                            repeat.DropDownItems.Add(Item(labels[i], () => media.SetRepeat(mode), snapshot.Repeat == mode));
                        }
                        items.Add(repeat);
                    }
                }
                if (items.Count > 0) items.Add(new WinForms.ToolStripSeparator());
                items.Add(Item("Verrouiller la position", () =>
                {
                    settings.LockPosition = !settings.LockPosition;
                    SettingsChanged();
                    if (settingsWindow != null) settingsWindow.Reload();
                }, settings.LockPosition));
                items.Add(Item("Paramètres", ToggleSettings));
                items.Add(Item("Masquer la barre", () => { userHidden = true; UpdateVisibility(); }));
                items.Add(new WinForms.ToolStripSeparator());
                items.Add(Item("Quitter", () => app.Shutdown()));
                barMenu.Show(WinForms.Cursor.Position);
            }

            void CreateTray()
            {
                var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
                toggleItem = new WinForms.ToolStripMenuItem("Masquer la barre");
                toggleItem.Click += delegate { userHidden = bar.IsVisible; UpdateVisibility(); };
                menu.Items.Add(toggleItem);
                menu.Items.Add("Paramètres", null, delegate { ToggleSettings(); });
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add("Quitter", null, delegate { app.Shutdown(); });

                tray = new WinForms.NotifyIcon { ContextMenuStrip = menu, Text = "Floating Player", Icon = LoadIcon(), Visible = true };
                tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) ToggleSettings(); };
            }

            static System.Drawing.Icon LoadIcon()
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FloatingPlayer.app.ico"))
                    return new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
            }

            void WatchQuitSignal()
            {
                EventWaitHandle signal;
                try
                {
                    signal = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEvent);
                    signal.Reset();   // a request still pending was meant for the previous instance
                }
                catch (Exception e) { Log.Write("Quit signal unavailable: " + e.Message); return; }
                ThreadPool.RegisterWaitForSingleObject(signal, delegate { app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown())); }, null, -1, true);
            }

            // Development aid: renders the views to PNG, which also works on a locked session.
            static void RenderToFile(FrameworkElement element, string name)
            {
                if (element == null || element.ActualWidth < 1) return;
                var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(element);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatingPlayer", name);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                using (var file = System.IO.File.Create(path)) encoder.Save(file);
            }

            void WriteDiagnostics()
            {
                RenderToFile(bar.Content as FrameworkElement, "render-bar.png");
                if (settingsWindow != null) RenderToFile(settingsWindow.Content as FrameworkElement, "render-settings.png");
                var s = bar.Scheme;
                Log.Write("diag session=" + snapshot.HasSession + " app=" + snapshot.App + " playing=" + snapshot.Playing
                    + " canSeek=" + snapshot.CanSeek + " pos=" + (int)snapshot.Position.TotalSeconds + "/" + (int)snapshot.Duration.TotalSeconds + " titleLen=" + snapshot.Title.Length + " art=" + (snapshot.ArtKey.Length > 6 ? snapshot.ArtKey.Substring(0, 6) : "-")
                    + " bg=" + ColorMath.ToHex(s.Background) + " accent=" + ColorMath.ToHex(s.Accent)
                    + " peakDb=" + audio.LastPeakDb.ToString("F1") + " visible=" + bar.IsVisible + " fps=" + Visualizer.TakeFrameCount());
            }
        }
    }
}
