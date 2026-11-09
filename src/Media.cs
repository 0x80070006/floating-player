using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace FloatingPlayer
{
    class MediaSnapshot
    {
        public bool HasSession;
        public string App = "";
        public string Title = "";
        public string Artist = "";
        public bool Playing;
        public bool CanPrevious, CanNext, CanToggle, CanSeek;
        public byte[] Art;
        public string ArtKey = "";
        public TimeSpan Position, Duration;
        public DateTime PositionAt;
        public bool Retained;           // last paused track, kept after its app withdrew the session
    }

    // Follows the media sessions Windows knows about (System Media Transport Controls): browsers,
    // Spotify and any other app that publishes its playback. Lives on the UI thread.
    class MediaController
    {
        readonly Dispatcher dispatcher;
        readonly DispatcherTimer debounce, poll;
        GlobalSystemMediaTransportControlsSessionManager manager;
        GlobalSystemMediaTransportControlsSession session;
        MediaSnapshot last = new MediaSnapshot();
        bool refreshing, refreshAgain, artDirty, sessionsDirty;
        DateTime toggledAt = DateTime.MinValue, retainedSince = DateTime.MinValue, goneSince = DateTime.MinValue;

        static readonly TimeSpan RetainFor = TimeSpan.FromMinutes(15);

        public event Action<MediaSnapshot> Changed;

        public MediaController(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            debounce = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(90) };
            debounce.Tick += delegate { debounce.Stop(); Refresh(); };
            poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(3) };
            poll.Tick += delegate { Refresh(); };
        }

        public async void Start()
        {
            try
            {
                manager = await Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                manager.SessionsChanged += (m, a) => Schedule(false, true);
                manager.CurrentSessionChanged += (m, a) => Schedule(true, true);
                poll.Start();
                Schedule(true, true);
            }
            catch (Exception e) { Log.Write("Media: " + e); }
        }

        public void Toggle()
        {
            toggledAt = DateTime.UtcNow;
            if (session == null)
            {
                // Browsers can withdraw a paused session; only the media key can still reach them.
                Native.PressPlayPauseKey();
                Schedule(true, true);
                return;
            }
            Send(s => s.TryTogglePlayPauseAsync());
        }

        public void Seek(TimeSpan position)
        {
            if (session == null) return;
            long ticks = (session.GetTimelineProperties().StartTime + position).Ticks;
            Send(s => s.TryChangePlaybackPositionAsync(ticks));
        }

        public void Next()
        {
            Send(s => s.TrySkipNextAsync());
        }

        public void Previous()
        {
            Send(s => s.TrySkipPreviousAsync());
        }

        async void Send(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command)
        {
            if (session == null) return;
            try { await Wait(command(session)); }
            catch (Exception e) { Log.Write("Media command: " + e.Message); }
            Schedule(true, false);
        }

        void Schedule(bool art, bool sessions)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                artDirty |= art;
                sessionsDirty |= sessions;
                debounce.Stop();
                debounce.Start();
            }));
        }

        void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) { Schedule(true, false); }
        void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) { Schedule(false, false); }
        void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) { Schedule(false, false); }

        void Attach(GlobalSystemMediaTransportControlsSession next)
        {
            if (session != null)
            {
                try
                {
                    session.MediaPropertiesChanged -= OnProperties;
                    session.PlaybackInfoChanged -= OnPlayback;
                    session.TimelinePropertiesChanged -= OnTimeline;
                }
                catch { }
            }
            session = next;
            if (session != null)
            {
                session.MediaPropertiesChanged += OnProperties;
                session.PlaybackInfoChanged += OnPlayback;
                session.TimelinePropertiesChanged += OnTimeline;
            }
        }

        // A paused tab must not hide what is actually playing, so playing sessions come first.
        GlobalSystemMediaTransportControlsSession Choose()
        {
            var all = manager.GetSessions().ToList();
            if (all.Count == 0) return null;
            Func<GlobalSystemMediaTransportControlsSession, bool> playing = s =>
            {
                try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
                catch { return false; }
            };
            var current = manager.GetCurrentSession();
            string currentId = current != null ? current.SourceAppUserModelId : null;
            string selectedId = session != null ? SafeId(session) : null;

            var inOrder = all.OrderBy(s => SafeId(s) == selectedId ? 0 : SafeId(s) == currentId ? 1 : 2).ToList();
            return inOrder.FirstOrDefault(playing)
                ?? all.FirstOrDefault(s => SafeId(s) == currentId)
                ?? inOrder[0];
        }

        static string SafeId(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.SourceAppUserModelId; } catch { return null; }
        }

        async void Refresh()
        {
            if (manager == null) return;
            if (refreshing) { refreshAgain = true; return; }
            refreshing = true;
            try
            {
                do
                {
                    refreshAgain = false;
                    await RefreshOnce();
                } while (refreshAgain);
            }
            catch (Exception e)
            {
                Log.Write("Media refresh: " + e.Message);
                Attach(null);   // the app may have closed its session; start over on the next pass
            }
            finally { refreshing = false; }
        }

        async Task RefreshOnce()
        {
            var chosen = Choose();
            if (chosen == null)
            {
                Attach(null);
                // Browsers drop their session for a moment between two tracks.
                if (goneSince == DateTime.MinValue) goneSince = DateTime.UtcNow;
                bool blink = last.HasSession && !last.Retained && last.Playing && (DateTime.UtcNow - goneSince).TotalSeconds < 2;
                if (!blink) Publish(ShouldRetain() ? Retain() : new MediaSnapshot());
                return;
            }
            goneSince = DateTime.MinValue;

            if (session == null || sessionsDirty || SafeId(chosen) != SafeId(session))
            {
                artDirty |= session == null || SafeId(chosen) != SafeId(session);
                Attach(chosen);
            }
            sessionsDirty = false;

            var snapshot = new MediaSnapshot { HasSession = true, App = FriendlyApp(SafeId(session)) };
            var playback = session.GetPlaybackInfo();
            snapshot.Playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            snapshot.CanPrevious = playback.Controls.IsPreviousEnabled;
            snapshot.CanNext = playback.Controls.IsNextEnabled;
            snapshot.CanSeek = playback.Controls.IsPlaybackPositionEnabled;
            snapshot.CanToggle = playback.Controls.IsPlayPauseToggleEnabled || playback.Controls.IsPlayEnabled || playback.Controls.IsPauseEnabled;

            var timeline = session.GetTimelineProperties();
            snapshot.Position = timeline.Position - timeline.StartTime;
            snapshot.Duration = timeline.EndTime - timeline.StartTime;
            snapshot.PositionAt = timeline.LastUpdatedTime.UtcDateTime;

            var properties = await Wait(session.TryGetMediaPropertiesAsync());
            snapshot.Title = (properties.Title ?? "").Trim();
            snapshot.Artist = (properties.Artist ?? "").Trim();
            if (snapshot.Artist.Length == 0) snapshot.Artist = (properties.AlbumArtist ?? "").Trim();

            bool needArt = artDirty || snapshot.Title != last.Title || snapshot.Artist != last.Artist || (last.Art == null && properties.Thumbnail != null);
            artDirty = false;
            if (needArt) await ReadArt(properties.Thumbnail, snapshot);
            else { snapshot.Art = last.Art; snapshot.ArtKey = last.ArtKey; }
            Publish(snapshot);
        }

        // A session that ends while paused (or just after pressing pause) was not closed by the user.
        bool ShouldRetain()
        {
            DateTime now = DateTime.UtcNow;
            if (last.Retained) return now - retainedSince < RetainFor;
            if (!last.HasSession || (last.Playing && (now - toggledAt).TotalSeconds > 8)) return false;
            retainedSince = now;
            return true;
        }

        MediaSnapshot Retain()
        {
            return new MediaSnapshot
            {
                HasSession = true, Retained = true, App = last.App, Title = last.Title, Artist = last.Artist,
                Art = last.Art, ArtKey = last.ArtKey, CanToggle = true,
                Position = last.Position, Duration = last.Duration, PositionAt = last.PositionAt
            };
        }

        static async Task ReadArt(IRandomAccessStreamReference reference, MediaSnapshot snapshot)
        {
            if (reference == null) return;
            try
            {
                using (var stream = await Wait(reference.OpenReadAsync()))
                using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    uint size = (uint)stream.Size;
                    if (size == 0) return;
                    await Wait(reader.LoadAsync(size));
                    var bytes = new byte[size];
                    reader.ReadBytes(bytes);
                    snapshot.Art = bytes;
                    using (var md5 = MD5.Create()) snapshot.ArtKey = Convert.ToBase64String(md5.ComputeHash(bytes));
                }
            }
            catch (Exception e) { Log.Write("Media art: " + e.Message); }
        }

        void Publish(MediaSnapshot snapshot)
        {
            last = snapshot;
            if (Changed != null) Changed(snapshot);
        }

        static string FriendlyApp(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string lower = id.ToLowerInvariant();
            if (id == "308046B0AF4A39CB") return "Firefox";
            if (lower.Contains("msedge")) return "Edge";
            if (lower.Contains("spotify")) return "Spotify";
            if (lower.Contains("zunemusic")) return "Lecteur multimédia";
            if (lower.Contains("chrome")) return "Chrome";
            string name = id;
            int bang = name.IndexOf('!');
            if (bang > 0) name = name.Substring(0, bang);
            int underscore = name.IndexOf('_');
            if (underscore > 0) name = name.Substring(0, underscore);
            int slash = name.LastIndexOf('\\');
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
            return name;
        }

        // The usual "await IAsyncOperation" extension does not bind against the split winmd files
        // that ship with Windows, so the operation is bridged by hand.
        static Task<T> Wait<T>(IAsyncOperation<T> operation)
        {
            var result = new TaskCompletionSource<T>();
            operation.Completed = (o, status) =>
            {
                if (status == AsyncStatus.Completed) result.TrySetResult(o.GetResults());
                else if (status == AsyncStatus.Error) result.TrySetException(o.ErrorCode);
                else result.TrySetCanceled();
            };
            return result.Task;
        }
    }
}
