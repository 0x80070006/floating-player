using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace FloatingPlayer
{
    struct VolumeState
    {
        public bool Available;      // false when no output device answers
        public bool IsApp;          // true: the source app's own mixer slider; false: the whole system
        public string Name;
        public float Level;         // 0 to 1
        public bool Muted;
    }

    // Reads and sets the volume of the playing app (its slider in the Windows mixer) or of the
    // default output device. Lives on the UI thread; the calls are short COM requests.
    class VolumeControl
    {
        static Guid NoContext = Guid.Empty;

        readonly Dispatcher dispatcher;
        readonly EndpointCallback callback;
        IMMDeviceEnumerator enumerator;
        IMMDevice device;
        IAudioEndpointVolume endpoint;
        IAudioSessionManager2 sessions;
        string deviceId;
        DateTime deviceCheckedAt = DateTime.MinValue;

        // The app's audio sessions, found again at most every 2 s.
        readonly List<ISimpleAudioVolume> appVolumes = new List<ISimpleAudioVolume>();
        string appVolumesFor;
        DateTime appVolumesAt = DateTime.MinValue;
        readonly Dictionary<uint, string> processNames = new Dictionary<uint, string>();

        string appId = "", appName = "";
        public bool PreferApp = true;

        // Raised on the UI thread when the system volume changes from anywhere (keyboard keys, mixer).
        public event Action Changed;

        public VolumeControl(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            callback = new EndpointCallback(this);
        }

        public void SetSource(string sourceAppId, string friendlyName)
        {
            sourceAppId = sourceAppId ?? "";
            if (sourceAppId == appId) return;
            appId = sourceAppId;
            appName = friendlyName ?? "";
            appVolumesAt = DateTime.MinValue;
        }

        public VolumeState Read()
        {
            var state = new VolumeState { Name = "Système" };
            try
            {
                if (!EnsureDevice()) return state;
                if (PreferApp && FindAppVolumes())
                {
                    ISimpleAudioVolume first = appVolumes[0];
                    float level;
                    bool muted;
                    first.GetMasterVolume(out level);
                    first.GetMute(out muted);
                    state.Available = true;
                    state.IsApp = true;
                    state.Name = appName.Length > 0 ? appName : "Application";
                    state.Level = level;
                    state.Muted = muted;
                    return state;
                }
                float system;
                bool systemMuted;
                endpoint.GetMasterVolumeLevelScalar(out system);
                endpoint.GetMute(out systemMuted);
                state.Available = true;
                state.Level = system;
                state.Muted = systemMuted;
            }
            catch (Exception e)
            {
                Log.Write("Volume read: " + e.Message);
                Reset();
            }
            return state;
        }

        public void SetLevel(float level)
        {
            level = Math.Max(0, Math.Min(1, level));
            Apply(v => { v.SetMasterVolume(level, ref NoContext); if (level > 0) v.SetMute(false, ref NoContext); },
                  e => { e.SetMasterVolumeLevelScalar(level, ref NoContext); if (level > 0) e.SetMute(false, ref NoContext); });
        }

        public void SetMuted(bool muted)
        {
            Apply(v => v.SetMute(muted, ref NoContext), e => e.SetMute(muted, ref NoContext));
        }

        void Apply(Action<ISimpleAudioVolume> app, Action<IAudioEndpointVolume> system)
        {
            try
            {
                if (!EnsureDevice()) return;
                if (PreferApp && FindAppVolumes())
                {
                    foreach (var v in appVolumes) app(v);
                }
                else system(endpoint);
            }
            catch (Exception e)
            {
                Log.Write("Volume set: " + e.Message);
                Reset();
            }
        }

        // The default output can change (headset plugged in): the controls follow it.
        bool EnsureDevice()
        {
            DateTime now = DateTime.UtcNow;
            if (endpoint != null && (now - deviceCheckedAt).TotalSeconds < 1.5) return true;
            deviceCheckedAt = now;

            if (enumerator == null) enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice current;
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out current) != 0 || current == null)
            {
                Reset();
                return false;
            }
            string currentId;
            current.GetId(out currentId);
            if (endpoint != null && currentId == deviceId)
            {
                Marshal.ReleaseComObject(current);
                return true;
            }

            Reset();
            device = current;
            deviceId = currentId;
            object unknown;
            Guid id = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref id, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out unknown);
            endpoint = (IAudioEndpointVolume)unknown;
            id = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref id, 1, IntPtr.Zero, out unknown);
            sessions = (IAudioSessionManager2)unknown;
            try { endpoint.RegisterControlChangeNotify(callback); }
            catch (Exception e) { Log.Write("Volume notify: " + e.Message); }
            return true;
        }

        void Reset()
        {
            ReleaseAppVolumes();
            if (endpoint != null)
            {
                try { endpoint.UnregisterControlChangeNotify(callback); } catch { }
                Release(endpoint);
            }
            Release(sessions);
            Release(device);
            endpoint = null;
            sessions = null;
            device = null;
            deviceId = null;
            deviceCheckedAt = DateTime.MinValue;
        }

        static void Release(object com)
        {
            if (com != null) try { Marshal.ReleaseComObject(com); } catch { }
        }

        void ReleaseAppVolumes()
        {
            foreach (var v in appVolumes) Release(v);
            appVolumes.Clear();
            appVolumesFor = null;
        }

        bool FindAppVolumes()
        {
            if (appId.Length == 0 || sessions == null) return false;
            DateTime now = DateTime.UtcNow;
            if (appVolumesFor == appId + "|" + deviceId && (now - appVolumesAt).TotalSeconds < 2) return appVolumes.Count > 0;

            ReleaseAppVolumes();
            appVolumesFor = appId + "|" + deviceId;
            appVolumesAt = now;

            string[] names, packages;
            Candidates(appId, out names, out packages);
            IAudioSessionEnumerator list;
            sessions.GetSessionEnumerator(out list);
            try
            {
                int count;
                list.GetCount(out count);
                var active = new List<ISimpleAudioVolume>();
                var idle = new List<ISimpleAudioVolume>();
                for (int i = 0; i < count; i++)
                {
                    object item;
                    list.GetSession(i, out item);
                    var control = item as IAudioSessionControl2;
                    bool keep = false;
                    try
                    {
                        if (control != null && Matches(control, names, packages))
                        {
                            int state;
                            control.GetState(out state);
                            var volume = item as ISimpleAudioVolume;
                            if (volume != null)
                            {
                                (state == 1 /* active */ ? active : idle).Add(volume);
                                keep = true;
                            }
                        }
                    }
                    catch { }
                    if (!keep) Release(item);
                }
                appVolumes.AddRange(active);
                appVolumes.AddRange(idle);
            }
            finally { Release(list); }
            return appVolumes.Count > 0;
        }

        bool Matches(IAudioSessionControl2 control, string[] names, string[] packages)
        {
            if (packages.Length > 0)
            {
                string identifier;
                control.GetSessionIdentifier(out identifier);
                if (identifier != null)
                    foreach (string p in packages)
                        if (identifier.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            uint pid;
            control.GetProcessId(out pid);
            if (pid == 0) return false;
            string name = ProcessName(pid);
            foreach (string n in names)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Media sessions name their app by AppUserModelID: "Spotify.exe", "MSEdge", "Brave",
        // a packaged "Family_hash!App" or Firefox's install hash.
        internal static void Candidates(string id, out string[] names, out string[] packages)
        {
            var n = new List<string>();
            var p = new List<string>();
            string lower = id.ToLowerInvariant();
            if (id == "308046B0AF4A39CB" || lower.Contains("firefox")) n.Add("firefox");
            if (lower.Contains("msedge")) n.Add("msedge");
            int bang = id.IndexOf('!');
            if (bang > 0)
            {
                p.Add(id.Substring(0, bang));
                string app = id.Substring(bang + 1);
                n.Add(app);
                int dot = app.LastIndexOf('.');
                if (dot >= 0) n.Add(app.Substring(dot + 1));
                int underscore = id.IndexOf('_');
                if (underscore > 0)
                {
                    string family = id.Substring(0, underscore);
                    int familyDot = family.LastIndexOf('.');
                    n.Add(familyDot >= 0 ? family.Substring(familyDot + 1) : family);
                }
            }
            else
            {
                string name = id;
                int slash = name.LastIndexOf('\\');
                if (slash >= 0) name = name.Substring(slash + 1);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                n.Add(name);
                int dot = name.IndexOf('.');
                if (dot > 0) n.Add(name.Substring(0, dot));
            }
            names = n.ToArray();
            packages = p.ToArray();
        }

        string ProcessName(uint pid)
        {
            string name;
            if (processNames.TryGetValue(pid, out name)) return name;
            name = Native.ProcessName(pid);
            if (processNames.Count > 256) processNames.Clear();
            processNames[pid] = name;
            return name;
        }

        internal void OnEndpointNotify()
        {
            dispatcher.BeginInvoke(new Action(() => { if (Changed != null) Changed(); }), DispatcherPriority.Background);
        }


        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
            void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
            void GetChannelCount(out uint count);
            void SetMasterVolumeLevel(float db, ref Guid context);
            void SetMasterVolumeLevelScalar(float level, ref Guid context);
            void GetMasterVolumeLevel(out float db);
            void GetMasterVolumeLevelScalar(out float level);
            void SetChannelVolumeLevel(uint channel, float db, ref Guid context);
            void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            void GetChannelVolumeLevel(uint channel, out float db);
            void GetChannelVolumeLevelScalar(uint channel, out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            void GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
            void GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
            void GetSessionEnumerator(out IAudioSessionEnumerator list);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            void GetCount(out int count);
            void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            void GetState(out int state);
            void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            void GetGroupingParam(out Guid group);
            void SetGroupingParam(ref Guid group, ref Guid context);
            void RegisterAudioSessionNotification(IntPtr events);
            void UnregisterAudioSessionNotification(IntPtr events);
            void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            void SetMasterVolume(float level, ref Guid context);
            void GetMasterVolume(out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }

    // COM only reaches public types, so the volume callback lives outside VolumeControl.
    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    // Called by Windows on one of its own threads whenever the device volume or mute changes.
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class EndpointCallback : IAudioEndpointVolumeCallback
    {
        readonly WeakReference owner;
        internal EndpointCallback(object owner) { this.owner = new WeakReference(owner); }
        public int OnNotify(IntPtr data)
        {
            var o = owner.Target as VolumeControl;
            if (o != null) o.OnEndpointNotify();
            return 0;
        }
    }
}