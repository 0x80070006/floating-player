using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FloatingPlayer
{
    // Listens to what the default output device is playing (WASAPI loopback) and turns it into
    // frequency bands for the visualizer. Everything here runs on its own background thread.
    class AudioCapture
    {
        public const int BandCount = 16;

        const int FftSize = 2048;
        const double MinHz = 45, MaxHz = 15000;
        const uint StreamLoopback = 0x00020000;
        const uint BufferSilent = 0x2;
        const long BufferDuration = 1000000;    // 100 ms, in 100 ns units

        readonly object gate = new object();
        readonly double[] published = new double[BandCount];
        object token;                           // identifies the running thread; null when stopped
        double sensitivity = 1;

        public double LastPeakDb { get; private set; }

        public double Sensitivity
        {
            set { Volatile.Write(ref sensitivity, value); }
        }

        public void Start()
        {
            lock (gate)
            {
                if (token != null) return;
                object mine = new object();
                token = mine;
                var thread = new Thread(() => Run(mine)) { IsBackground = true, Name = "Audio capture" };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                token = null;
                Array.Clear(published, 0, published.Length);
            }
        }

        public void Read(double[] destination)
        {
            lock (gate) Array.Copy(published, destination, BandCount);
        }

        bool IsCurrent(object mine)
        {
            lock (gate) return ReferenceEquals(token, mine);
        }

        void Publish(object mine, double[] bands)
        {
            lock (gate) if (ReferenceEquals(token, mine)) Array.Copy(bands, published, BandCount);
        }

        void Run(object mine)
        {
            while (IsCurrent(mine))
            {
                try { Capture(mine); }
                catch (Exception e)
                {
                    Log.Write("Audio: " + e.Message);
                    Publish(mine, new double[BandCount]);
                    Thread.Sleep(1500);
                }
            }
        }

        void Capture(object mine)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device;
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device);
            string deviceId;
            device.GetId(out deviceId);

            object unknown;
            Guid clientId = typeof(IAudioClient).GUID;
            device.Activate(ref clientId, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out unknown);
            var client = (IAudioClient)unknown;

            IntPtr formatPtr;
            client.GetMixFormat(out formatPtr);
            int channels, sampleRate, blockAlign, bits;
            bool isFloat;
            try
            {
                int tag = (ushort)Marshal.ReadInt16(formatPtr, 0);
                channels = (ushort)Marshal.ReadInt16(formatPtr, 2);
                sampleRate = Marshal.ReadInt32(formatPtr, 4);
                blockAlign = (ushort)Marshal.ReadInt16(formatPtr, 12);
                bits = (ushort)Marshal.ReadInt16(formatPtr, 14);
                if (tag == 0xFFFE) tag = Marshal.ReadInt32(formatPtr, 24);   // WAVEFORMATEXTENSIBLE.SubFormat
                isFloat = tag == 3;
                client.Initialize(0 /* shared */, StreamLoopback, BufferDuration, 0, formatPtr, IntPtr.Zero);
            }
            finally { Marshal.FreeCoTaskMem(formatPtr); }

            Guid captureId = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref captureId, out unknown);
            var capture = (IAudioCaptureClient)unknown;

            var analyzer = new SpectrumAnalyzer(FftSize, sampleRate, BandCount, MinHz, MaxHz);
            var mono = new float[FftSize];
            int monoPos = 0;
            byte[] raw = new byte[0];
            DateTime lastAnalysis = DateTime.MinValue, lastData = DateTime.UtcNow, lastDeviceCheck = DateTime.UtcNow;

            client.Start();
            try
            {
                while (IsCurrent(mine))
                {
                    uint packet;
                    capture.GetNextPacketSize(out packet);
                    bool received = false;
                    while (packet > 0)
                    {
                        IntPtr data;
                        uint frames, flags;
                        ulong devicePosition, qpc;
                        capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpc);
                        int bytes = (int)frames * blockAlign;
                        if (raw.Length < bytes) raw = new byte[bytes];
                        if ((flags & BufferSilent) == 0) Marshal.Copy(data, raw, 0, bytes);
                        else Array.Clear(raw, 0, bytes);
                        capture.ReleaseBuffer(frames);

                        for (int f = 0; f < frames; f++)
                        {
                            double sum = 0;
                            int offset = f * blockAlign;
                            for (int c = 0; c < channels; c++)
                                sum += ReadSample(raw, offset + c * (bits / 8), bits, isFloat);
                            mono[monoPos] = (float)(sum / channels);
                            monoPos = (monoPos + 1) % FftSize;
                        }
                        received = true;
                        capture.GetNextPacketSize(out packet);
                    }

                    DateTime now = DateTime.UtcNow;
                    if (received) lastData = now;
                    if (received && (now - lastAnalysis).TotalMilliseconds >= 16)
                    {
                        lastAnalysis = now;
                        Publish(mine, analyzer.Analyze(mono, monoPos, Volatile.Read(ref sensitivity)));
                        LastPeakDb = analyzer.PeakDb;
                    }
                    else if ((now - lastData).TotalMilliseconds > 150)
                    {
                        // The endpoint delivers nothing at all while it is silent.
                        Publish(mine, new double[BandCount]);
                    }

                    if ((now - lastDeviceCheck).TotalSeconds >= 2)
                    {
                        lastDeviceCheck = now;
                        IMMDevice currentDevice;
                        enumerator.GetDefaultAudioEndpoint(0, 1, out currentDevice);
                        string currentId;
                        currentDevice.GetId(out currentId);
                        Marshal.ReleaseComObject(currentDevice);
                        if (currentId != deviceId) return;   // Run() reopens the new default device
                    }

                    Thread.Sleep(8);
                }
            }
            finally
            {
                try { client.Stop(); } catch { }
                Marshal.ReleaseComObject(capture);
                Marshal.ReleaseComObject(client);
                Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        static double ReadSample(byte[] data, int offset, int bits, bool isFloat)
        {
            if (isFloat && bits == 32) return BitConverter.ToSingle(data, offset);
            if (isFloat && bits == 64) return BitConverter.ToDouble(data, offset);
            switch (bits)
            {
                case 16: return BitConverter.ToInt16(data, offset) / 32768.0;
                case 24: return ((data[offset + 2] << 24 | data[offset + 1] << 16 | data[offset] << 8) >> 8) / 8388608.0;
                case 32: return BitConverter.ToInt32(data, offset) / 2147483648.0;
                default: return 0;
            }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            void OpenPropertyStore(int access, out IntPtr properties);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            void GetBufferSize(out uint frames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr handle);
            void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            void ReleaseBuffer(uint frames);
            void GetNextPacketSize(out uint frames);
        }
    }

    // Hann-windowed FFT reduced to logarithmic bands, with an automatic gain so that quiet and
    // loud sources both use the full height of the bars.
    class SpectrumAnalyzer
    {
        const double SilenceDb = -72;
        const double RangeDb = 34;

        readonly int size;
        readonly double[] window, re, im;
        readonly int[] bandStart, bandEnd;
        readonly double[] tilt;
        readonly int[] reverse;
        double ceilingDb = -30;

        public double PeakDb { get; private set; }

        public SpectrumAnalyzer(int size, int sampleRate, int bands, double minHz, double maxHz)
        {
            this.size = size;
            window = new double[size];
            re = new double[size];
            im = new double[size];
            reverse = new int[size];
            for (int i = 0; i < size; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1));

            int bits = (int)Math.Round(Math.Log(size, 2));
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                reverse[i] = r;
            }

            bandStart = new int[bands];
            bandEnd = new int[bands];
            tilt = new double[bands];
            double hzPerBin = (double)sampleRate / size;
            maxHz = Math.Min(maxHz, sampleRate / 2.0 * 0.95);
            for (int b = 0; b < bands; b++)
            {
                double low = minHz * Math.Pow(maxHz / minHz, (double)b / bands);
                double high = minHz * Math.Pow(maxHz / minHz, (double)(b + 1) / bands);
                bandStart[b] = Math.Max(1, (int)Math.Floor(low / hzPerBin));
                bandEnd[b] = Math.Max(bandStart[b] + 1, (int)Math.Ceiling(high / hzPerBin));
                // Music loses energy as frequency rises; compensate so the treble bars move too.
                tilt[b] = Math.Sqrt(Math.Sqrt(low * high) / 120);
            }
        }

        public double[] Analyze(float[] ring, int position, double sensitivity)
        {
            for (int i = 0; i < size; i++)
            {
                int j = reverse[i];
                re[j] = ring[(position + i) % size] * window[i];
                im[j] = 0;
            }
            Fft();

            int bands = bandStart.Length;
            var result = new double[bands];
            double loudest = double.NegativeInfinity;
            var levels = new double[bands];
            for (int b = 0; b < bands; b++)
            {
                double power = 0;
                for (int k = bandStart[b]; k < bandEnd[b] && k < size / 2; k++) power += re[k] * re[k] + im[k] * im[k];
                power /= Math.Max(1, bandEnd[b] - bandStart[b]);
                double magnitude = Math.Sqrt(power) * 4 / size * tilt[b];
                double db = 20 * Math.Log10(magnitude + 1e-10);
                levels[b] = db;
                if (db > loudest) loudest = db;
            }
            PeakDb = loudest;
            if (loudest < SilenceDb) return result;

            // The ceiling jumps up to loud peaks and drifts back down, so bars keep their dynamics.
            ceilingDb = loudest > ceilingDb ? loudest : Math.Max(loudest, ceilingDb - 0.035);
            double range = RangeDb / Math.Max(0.4, sensitivity);
            for (int b = 0; b < bands; b++)
            {
                double value = (levels[b] - (ceilingDb - range)) / range;
                value = Math.Max(0, Math.Min(1, value));
                result[b] = Math.Pow(value, 1.5);
            }
            return result;
        }

        void Fft()
        {
            for (int length = 2; length <= size; length *= 2)
            {
                double angle = -2 * Math.PI / length;
                double stepRe = Math.Cos(angle), stepIm = Math.Sin(angle);
                for (int start = 0; start < size; start += length)
                {
                    double wr = 1, wi = 0;
                    for (int k = 0; k < length / 2; k++)
                    {
                        int a = start + k, b = a + length / 2;
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                        double next = wr * stepRe - wi * stepIm;
                        wi = wr * stepIm + wi * stepRe;
                        wr = next;
                    }
                }
            }
        }
    }
}
