using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using US16x08_AbletonPanel.Models;

namespace US16x08_AbletonPanel.Hardware;

public sealed class TascamHardwareService : IDisposable
{
    // KSCATEGORY_RENDER = {65E8773E-8F56-11D0-A3B9-00A0C9223196}
    private static readonly Guid KsCategoryRender = new("65E8773E-8F56-11D0-A3B9-00A0C9223196");

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint SPDRP_SERVICE = 0x00000004;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    // US-16x08 Driver IOCTL codes (from 0x464e20, 0x464fb0, 0x465050)
    private const uint IOCTL_TASCAM_VENDOR_CTL = 0x00222000;
    private const uint IOCTL_TASCAM_GET_SAMPLERATE = 0x0022606C;

    private const byte USB_REQ_WRITE_TLV = 0x1D;
    private const byte USB_REQ_READ_TLV = 0x1E;

    // Group IDs (TLV tag 0x61)
    public const byte GROUP_LINE_OUT = 1;
    public const byte GROUP_MASTER = 2;
    public const byte GROUP_COMPUTER = 3;
    public const byte GROUP_INPUT_TRACK = 4;

    private IntPtr _deviceHandle = IntPtr.Zero;
    private readonly object _ioLock = new();
    private readonly object _queueLock = new();
    private readonly Dictionary<string, (byte[] Packet, string Description)> _pendingSlots = new();
    private readonly Queue<string> _pendingOrder = new();
    private readonly Dictionary<string, byte[]> _lastSentBySlot = new();
    private readonly AutoResetEvent _txSignal = new(false);
    private readonly Thread _workerThread;
    private volatile bool _disposed;

    // Background-polled hardware meter cache (never blocks UI thread or parameter writes)
    private readonly double[] _hwTrackPeaks = new double[16];
    private double _hwMasterL;
    private double _hwMasterR;
    private double _hwSelectedGrDb;
    private volatile int _selectedCompPollChannel; // 1..16 when selected track has compressor enabled, else 0
    private long _lastParamWriteTick;
    private long _lastMeterPollTick;

    public bool IsHardwareConnected => _deviceHandle != IntPtr.Zero && _deviceHandle != new IntPtr(-1);
    public string DevicePath { get; private set; } = "US-16x08 Disconnected";
    public string ProductName { get; private set; } = "TASCAM US-16x08";
    public string FirmwareVersion { get; private set; } = "2.00 (Build 0022)";
    public string DriverVersion { get; private set; } = "4.0.0";
    public int SampleRate { get; private set; } = 48000;

    public event Action<string>? PacketLogged;

    public TascamHardwareService()
    {
        ReadInstalledDriverVersion();
        TryConnectHardware();

        _workerThread = new Thread(HardwareWorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "US16x08_UsbControlWorker"
        };
        _workerThread.Start();
    }

    private bool HasPendingCommands
    {
        get
        {
            lock (_queueLock)
                return _pendingSlots.Count > 0;
        }
    }

    private void HardwareWorkerLoop()
    {
        while (!_disposed)
        {
            _txSignal.WaitOne(15);
            if (_disposed) return;

            // Priority 1: Immediately flush all coalesced parameter changes
            bool sentAny = false;
            while (!_disposed)
            {
                byte[]? pkt = null;
                string? desc = null;

                lock (_queueLock)
                {
                    while (_pendingOrder.Count > 0)
                    {
                        string key = _pendingOrder.Dequeue();
                        if (_pendingSlots.Remove(key, out var item))
                        {
                            pkt = item.Packet;
                            desc = item.Description;
                            break;
                        }
                    }
                }

                if (pkt == null)
                    break;

                if (TransmitTlvPacketHardware(pkt))
                {
                    sentAny = true;
                    Interlocked.Exchange(ref _lastParamWriteTick, Environment.TickCount64);
                    if (desc != null)
                        LogPacket(desc, pkt);
                }
            }

            if (sentAny || _disposed || !IsHardwareConnected)
                continue;

            // Priority 2: Poll meters in background only when user is not actively dragging a control
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastParamWriteTick) >= 35 &&
                now - _lastMeterPollTick >= 45)
            {
                _lastMeterPollTick = now;
                PollHardwareMetersBackground();
                int grCh = _selectedCompPollChannel;
                if (grCh >= 1 && grCh <= 16 && !HasPendingCommands)
                {
                    PollHardwareGrMeterBackground(grCh);
                }
            }
        }
    }

    public bool TryConnectHardware()
    {
        lock (_ioLock)
        {
            CloseHandleInternal();

            string? foundPath = FindUs16x08DevicePath();
            if (string.IsNullOrEmpty(foundPath))
            {
                DevicePath = "Simulation Mode (Hardware Unplugged)";
                return false;
            }

            IntPtr hDev = CreateFileW(
                foundPath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hDev == IntPtr.Zero || hDev == new IntPtr(-1))
            {
                DevicePath = $"Simulation Mode (Driver Busy: {foundPath})";
                return false;
            }

            _deviceHandle = hDev;
            DevicePath = foundPath;

            QuerySampleRateFromHardware();
            QueryDeviceInfoFromHardware();
            return true;
        }
    }

    private static unsafe string? FindUs16x08DevicePath()
    {
        Guid classGuid = KsCategoryRender;
        IntPtr hDevInfo = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1))
            return null;

        try
        {
            uint memberIndex = 0;
            SP_DEVINFO_DATA devInfoData = new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };

            while (SetupDiEnumDeviceInfo(hDevInfo, memberIndex, ref devInfoData))
            {
                memberIndex++;
                byte[] propBuf = new byte[256];
                if (SetupDiGetDeviceRegistryPropertyW(
                        hDevInfo,
                        ref devInfoData,
                        SPDRP_SERVICE,
                        out _,
                        propBuf,
                        (uint)propBuf.Length,
                        out _))
                {
                    string serviceName = Encoding.Unicode.GetString(propBuf).TrimEnd('\0');
                    int nullIdx = serviceName.IndexOf('\0');
                    if (nullIdx >= 0) serviceName = serviceName[..nullIdx];

                    if (string.Equals(serviceName, "us-16x08", StringComparison.OrdinalIgnoreCase))
                    {
                        SP_DEVICE_INTERFACE_DATA ifaceData = new() { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                        if (SetupDiEnumDeviceInterfaces(hDevInfo, ref devInfoData, ref classGuid, 0, ref ifaceData))
                        {
                            SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref ifaceData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                            if (reqSize > 0 && reqSize < 2048)
                            {
                                byte[] detailBuf = new byte[reqSize];
                                uint detailCbSize = (uint)(IntPtr.Size == 8 ? 8 : 6);
                                BitConverter.TryWriteBytes(detailBuf.AsSpan(0, 4), detailCbSize);

                                fixed (byte* pDetail = detailBuf)
                                {
                                    if (SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref ifaceData, (IntPtr)pDetail, reqSize, out _, IntPtr.Zero))
                                    {
                                        string rawPath = Marshal.PtrToStringUni((IntPtr)(pDetail + 4)) ?? "";
                                        int lastSlash = rawPath.LastIndexOf('\\');
                                        if (lastSlash > 0)
                                        {
                                            return rawPath[..lastSlash] + "\\us-16x08";
                                        }
                                        return rawPath;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }

        return null;
    }

    private void ReadInstalledDriverVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\us-16x08");
            if (key != null)
            {
                string? imagePath = key.GetValue("ImagePath")?.ToString();
                if (!string.IsNullOrEmpty(imagePath))
                    DriverVersion = "4.00 (us-16x08.sys Installed)";
            }
        }
        catch
        {
            // Ignore registry permission issues
        }
    }

    public int GetAsioBufferSize()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\TASCAM\US-16x08\ASIODRIVER");
            if (key?.GetValue("blockFrames") is int frames && frames >= 4 && frames <= 4096)
                return frames;
        }
        catch
        {
            // Fallback default
        }
        return 256;
    }

    public bool SetAsioBufferSize(int blockFrames)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\TASCAM\US-16x08\ASIODRIVER");
            key?.SetValue("blockFrames", blockFrames, RegistryValueKind.DWord);
            LogPacket("ASIO REG", $"HKCU\\Software\\TASCAM\\US-16x08\\ASIODRIVER\\blockFrames = {blockFrames} samples");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public unsafe int QuerySampleRateFromHardware()
    {
        if (!IsHardwareConnected)
            return SampleRate;

        lock (_ioLock)
        {
            uint rate = 0;
            if (DeviceIoControl(_deviceHandle, IOCTL_TASCAM_GET_SAMPLERATE, null, 0, &rate, 4, out uint returned, IntPtr.Zero)
                && returned == 4 && rate >= 44100 && rate <= 192000)
            {
                SampleRate = (int)rate;
            }
            return SampleRate;
        }
    }

    public void SetSimulatedSampleRate(int rate)
    {
        if (!IsHardwareConnected)
            SampleRate = rate;
    }

    private void QueryDeviceInfoFromHardware()
    {
        if (!IsHardwareConnected) return;

        byte[] cmd = new byte[64];
        cmd[0] = 0x31;
        cmd[1] = 0x01;
        if (TransmitTlvPacketHardware(cmd))
        {
            byte[] reply = new byte[64];
            if (VendorControlRead(USB_REQ_READ_TLV, 0, 0, reply))
            {
                int idx = 0;
                ushort ver = 200;
                ushort build = 22;
                while (idx + 1 < reply.Length)
                {
                    byte tag = reply[idx];
                    byte type = reply[idx + 1];
                    if (tag == 0 && type == 0) break;
                    if (type == 1) idx += 2;
                    else if (type == 2) idx += 3;
                    else if (type == 3 && idx + 3 < reply.Length)
                    {
                        ushort val = (ushort)(reply[idx + 2] | (reply[idx + 3] << 8));
                        if (tag == 0x32) ver = val;
                        if (tag == 0x33) build = val;
                        idx += 4;
                    }
                    else if (type == 4) idx += 6;
                    else if (type == 5 && idx + 2 < reply.Length)
                    {
                        int len = reply[idx + 2];
                        if (tag == 0x34 && idx + 3 + len <= reply.Length)
                        {
                            ProductName = Encoding.ASCII.GetString(reply, idx + 3, len).TrimEnd('\0');
                        }
                        idx += 3 + len;
                    }
                    else break;
                }
                if (ver >= 100)
                    FirmwareVersion = $"{ver / 100}.{ver % 100:00} (Build {build:0000})";
                else
                    FirmwareVersion = $"{ver >> 8}.{ver & 0xFF:00} (Build {build:0000})";
            }
        }
    }

    private unsafe bool VendorControlWrite(byte bRequest, ushort wValue, ushort wIndex, ReadOnlySpan<byte> payload)
    {
        if (!IsHardwareConnected) return false;

        int totalLen = 0x10 + payload.Length;
        byte* buf = stackalloc byte[totalLen];
        *(uint*)(buf + 0x00) = 0; // Direction 0 = OUT
        *(uint*)(buf + 0x04) = (uint)payload.Length;
        *(ushort*)(buf + 0x08) = 0x0017;
        *(ushort*)(buf + 0x0A) = wValue;
        *(ushort*)(buf + 0x0C) = wIndex;
        *(buf + 0x0E) = 0;
        *(buf + 0x0F) = bRequest;

        for (int i = 0; i < payload.Length; i++)
            buf[0x10 + i] = payload[i];

        return DeviceIoControl(_deviceHandle, IOCTL_TASCAM_VENDOR_CTL, buf, (uint)totalLen, null, 0, out _, IntPtr.Zero);
    }

    private unsafe bool VendorControlRead(byte bRequest, ushort wValue, ushort wIndex, Span<byte> outData)
    {
        if (!IsHardwareConnected) return false;

        byte* hdr = stackalloc byte[0x14];
        *(uint*)(hdr + 0x00) = 1; // Direction 1 = IN
        *(uint*)(hdr + 0x04) = 0;
        *(ushort*)(hdr + 0x08) = 0x0017;
        *(ushort*)(hdr + 0x0A) = wValue;
        *(ushort*)(hdr + 0x0C) = wIndex;
        *(hdr + 0x0E) = 0;
        *(hdr + 0x0F) = bRequest;
        *(uint*)(hdr + 0x10) = 0;

        fixed (byte* pOut = outData)
        {
            return DeviceIoControl(_deviceHandle, IOCTL_TASCAM_VENDOR_CTL, hdr, 0x14, pOut, (uint)outData.Length, out uint returned, IntPtr.Zero)
                   && returned > 0;
        }
    }

    private static int GetExactTlvLength(ReadOnlySpan<byte> tlv)
    {
        int idx = 0;
        while (idx + 1 < tlv.Length)
        {
            byte tag = tlv[idx];
            byte type = tlv[idx + 1];
            if (tag == 0 && type == 0)
                return idx + 2;
            if (type == 1) idx += 2;
            else if (type == 2) idx += 3;
            else if (type == 3) idx += 4;
            else if (type == 4) idx += 6;
            else if (type == 5 && idx + 2 < tlv.Length) idx += 3 + tlv[idx + 2];
            else break;
        }
        return Math.Min(tlv.Length, 64);
    }

    public void ResetStateCache()
    {
        lock (_queueLock)
        {
            _lastSentBySlot.Clear();
        }
    }

    private bool SendTlvCommand(string slotKey, ReadOnlySpan<byte> tlv, string description)
    {
        int exactLen = GetExactTlvLength(tlv);
        ReadOnlySpan<byte> trimmed = tlv[..exactLen];

        if (!IsHardwareConnected)
        {
            LogPacket(description, trimmed);
            return true;
        }

        lock (_queueLock)
        {
            // Skip duplicate packets when encoded DSP bytes are unchanged (matches 0x45A180..0x45AB90)
            if (_lastSentBySlot.TryGetValue(slotKey, out byte[]? prev) &&
                prev.AsSpan().SequenceEqual(trimmed))
            {
                return true;
            }

            byte[] copy = trimmed.ToArray();
            _lastSentBySlot[slotKey] = copy;

            // Latest-value coalescing: overwrite any unsent packet for the same slot in-place
            if (!_pendingSlots.ContainsKey(slotKey))
            {
                _pendingOrder.Enqueue(slotKey);
            }
            _pendingSlots[slotKey] = (copy, description);
        }

        _txSignal.Set();
        return true;
    }

    private bool TransmitTlvPacketHardware(ReadOnlySpan<byte> tlv)
    {
        if (!IsHardwareConnected) return false;

        int exactLen = GetExactTlvLength(tlv);
        ReadOnlySpan<byte> payload = tlv[..exactLen];

        lock (_ioLock)
        {
            Span<byte> statusBuf = stackalloc byte[64];
            for (int retry = 0; retry < 8; retry++)
            {
                if (VendorControlRead(USB_REQ_READ_TLV, 0, 0, statusBuf))
                {
                    if (statusBuf[0] == 0xA1 && statusBuf[1] == 0x02 && statusBuf[2] == 0x00)
                        break;
                }
            }

            return VendorControlWrite(USB_REQ_WRITE_TLV, 0, 0, payload);
        }
    }

    private void LogPacket(string label, ReadOnlySpan<byte> tlv)
    {
        int len = GetExactTlvLength(tlv);
        StringBuilder sb = new();
        sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] {label,-24} -> ");
        for (int i = 0; i < Math.Min(len, 29); i++)
            sb.Append($"{tlv[i]:X2} ");
        PacketLogged?.Invoke(sb.ToString().TrimEnd());
    }

    private void LogPacket(string label, string details)
    {
        PacketLogged?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {label,-24} -> {details}");
    }

    // =========================================================================
    // HIGH-LEVEL US-16x08 DSP COMMANDS (Matching 0x4544e0 - 0x4577c0)
    // =========================================================================

    public bool SendChannelParam(byte group, byte chOneBased, byte paramTag, byte val, string label)
    {
        Span<byte> pkt = stackalloc byte[11];
        pkt[0] = 0x61; pkt[1] = 0x02; pkt[2] = group;
        pkt[3] = 0x62; pkt[4] = 0x02; pkt[5] = chOneBased;
        pkt[6] = paramTag; pkt[7] = 0x02; pkt[8] = val;
        pkt[9] = 0x00; pkt[10] = 0x00;
        return SendTlvCommand($"CH:{group}:{chOneBased}:{paramTag:X2}", pkt, label);
    }

    public void SetTrackFader(int chOneBased, double faderDb)
    {
        byte code = TascamDspTables.EncodeFaderDb(faderDb);
        SendChannelParam(GROUP_INPUT_TRACK, (byte)chOneBased, 0x81, code, $"CH{chOneBased} FADER ({faderDb:0.0}dB)");
    }

    public void SetMasterFader(double faderDb)
    {
        byte code = TascamDspTables.EncodeFaderDb(faderDb);
        SendChannelParam(GROUP_MASTER, 1, 0x81, code, $"MASTER L FADER ({faderDb:0.0}dB)");
        SendChannelParam(GROUP_MASTER, 2, 0x81, code, $"MASTER R FADER ({faderDb:0.0}dB)");
    }

    public void SetTrackPan(int chOneBased, double pan)
    {
        byte code = TascamDspTables.EncodePan(pan);
        SendChannelParam(GROUP_INPUT_TRACK, (byte)chOneBased, 0x82, code, $"CH{chOneBased} PAN ({pan:0})");
    }

    public void SetTrackMute(int chOneBased, bool muted)
    {
        SendChannelParam(GROUP_INPUT_TRACK, (byte)chOneBased, 0x83, (byte)(muted ? 1 : 0), $"CH{chOneBased} MUTE ({muted})");
    }

    public void SetMasterMute(bool muted)
    {
        SendChannelParam(GROUP_MASTER, 1, 0x83, (byte)(muted ? 1 : 0), $"MASTER L MUTE ({muted})");
        SendChannelParam(GROUP_MASTER, 2, 0x83, (byte)(muted ? 1 : 0), $"MASTER R MUTE ({muted})");
    }

    public void SetTrackSolo(int chOneBased, bool solo)
    {
        SendChannelParam(GROUP_INPUT_TRACK, (byte)chOneBased, 0x84, (byte)(solo ? 1 : 0), $"CH{chOneBased} SOLO ({solo})");
    }

    public void SetTrackPhase(int chOneBased, bool inverted)
    {
        SendChannelParam(GROUP_INPUT_TRACK, (byte)chOneBased, 0x85, (byte)(inverted ? 1 : 0), $"CH{chOneBased} PHASE ({inverted})");
    }

    public void SetTrackEqBand(TrackChannelModel track, EqBandModel band)
    {
        Span<byte> pkt = stackalloc byte[23];
        byte ch = (byte)track.ChannelNumber;
        byte bandNum = (byte)band.BandNumber;
        bool isLowCut = bandNum == 1 && track.IsLowCutEnabled;
        byte enable = (byte)((track.IsEqEnabled || isLowCut) ? 1 : 0);
        byte gainCode = TascamDspTables.EncodeEqGain(bandNum, track.IsEqEnabled ? band.GainDb : 0.0, isLowCut);
        byte freqCode = band.FrequencyCode;
        byte qCode = band.QCode;

        // Exact TLV tag layout from 0x453B50 -> 0x456DF0:
        // 0x61 = group (4), 0x62 = ch (1..16), 0x51 = band (1..4),
        // 0x52 = gain (0..24, 0dB=0x0C, or 0xFF for Band 1 LowCut),
        // 0x53 = freq (0..31 / 0..63 / 32..63), 0x54 = Q (0..6 or 0xFF), 0x55 = enable (0..1)
        pkt[0] = 0x61; pkt[1] = 0x02; pkt[2] = GROUP_INPUT_TRACK;
        pkt[3] = 0x62; pkt[4] = 0x02; pkt[5] = ch;
        pkt[6] = 0x51; pkt[7] = 0x02; pkt[8] = bandNum;
        pkt[9] = 0x52; pkt[10] = 0x02; pkt[11] = gainCode;
        pkt[12] = 0x53; pkt[13] = 0x02; pkt[14] = freqCode;
        pkt[15] = 0x54; pkt[16] = 0x02; pkt[17] = qCode;
        pkt[18] = 0x55; pkt[19] = 0x02; pkt[20] = enable;
        pkt[21] = 0x00; pkt[22] = 0x00;

        SendTlvCommand($"EQ:{ch}:{bandNum}", pkt, $"CH{ch} EQ B{bandNum} ({band.FrequencyDisplay}, {band.GainDisplay})");
    }

    public void SetTrackCompressor(TrackChannelModel track)
    {
        Span<byte> pkt = stackalloc byte[29];
        var comp = track.Compressor;
        byte ch = (byte)track.ChannelNumber;
        byte thrCode = unchecked((byte)(sbyte)Math.Clamp((int)Math.Round(comp.ThresholdDb), -32, 0));
        byte ratioCode = TascamDspTables.CompRatioDspCodes[Math.Clamp(comp.RatioIndex, 0, TascamDspTables.CompRatioDspCodes.Length - 1)];
        byte atkCode = (byte)Math.Clamp((int)Math.Round(comp.AttackMs), 2, 200);
        byte relCode = (byte)Math.Clamp((int)Math.Round(comp.ReleaseMs / 10.0), 1, 100);
        byte gainCode = (byte)Math.Clamp((int)Math.Round(comp.MakeupGainDb), 0, 20);
        byte linkCode = !track.IsStereoLinked ? (byte)1 : (track.IsOddChannel ? (byte)2 : (byte)3);
        byte enableCode = (byte)(comp.IsEnabled ? 1 : 0);

        // Exact TLV tag layout from 0x4330D0 -> 0x453CB0 -> 0x457200:
        // 0x91 = threshold (-32..0 dB), 0x92 = ratio, 0x93 = attack (2..200 ms),
        // 0x94 = release (1..100 = 10..1000 ms), 0x95 = makeup gain (0..20 dB),
        // 0x96 = stereo link mode (1=mono, 2=odd, 3=even), 0x97 = enable (0..1)
        pkt[0] = 0x61; pkt[1] = 0x02; pkt[2] = GROUP_INPUT_TRACK;
        pkt[3] = 0x62; pkt[4] = 0x02; pkt[5] = ch;
        pkt[6] = 0x91; pkt[7] = 0x02; pkt[8] = thrCode;
        pkt[9] = 0x92; pkt[10] = 0x02; pkt[11] = ratioCode;
        pkt[12] = 0x93; pkt[13] = 0x02; pkt[14] = atkCode;
        pkt[15] = 0x94; pkt[16] = 0x02; pkt[17] = relCode;
        pkt[18] = 0x95; pkt[19] = 0x02; pkt[20] = gainCode;
        pkt[21] = 0x96; pkt[22] = 0x02; pkt[23] = linkCode;
        pkt[24] = 0x97; pkt[25] = 0x02; pkt[26] = enableCode;
        pkt[27] = 0x00; pkt[28] = 0x00;

        SendTlvCommand($"COMP:{ch}", pkt, $"CH{ch} COMP (T:{comp.ThresholdDb:0} R:{comp.RatioDisplay})");
    }

    public void SetLineOutRoute(int lineOutOneBased, int sourceIndex)
    {
        byte srcGroup = sourceIndex <= 1 ? GROUP_MASTER : GROUP_COMPUTER;
        byte srcCh = sourceIndex <= 1 ? (byte)(sourceIndex + 1) : (byte)(sourceIndex - 1);

        Span<byte> pkt = stackalloc byte[20];
        pkt[0] = 0x61; pkt[1] = 0x02; pkt[2] = srcGroup;
        pkt[3] = 0x62; pkt[4] = 0x02; pkt[5] = srcCh;
        pkt[6] = 0x41; pkt[7] = 0x01;
        pkt[8] = 0x61; pkt[9] = 0x02; pkt[10] = GROUP_LINE_OUT;
        pkt[11] = 0x62; pkt[12] = 0x02; pkt[13] = (byte)lineOutOneBased;
        pkt[14] = 0x42; pkt[15] = 0x01;
        pkt[16] = 0x43; pkt[17] = 0x01;
        pkt[18] = 0x00; pkt[19] = 0x00;

        SendTlvCommand($"ROUTE:{lineOutOneBased}", pkt, $"OUT {lineOutOneBased} <- {TascamDspTables.LineOutSourceNames[Math.Clamp(sourceIndex, 0, 9)]}");
    }

    public void SetComputerToStereoBus(bool enabled)
    {
        Span<byte> pkt = stackalloc byte[5];
        pkt[0] = 0x44; pkt[1] = 0x02; pkt[2] = (byte)(enabled ? 1 : 0);
        pkt[3] = 0x00; pkt[4] = 0x00;
        SendTlvCommand("SYS:44", pkt, $"COMP->STEREO BUS ({enabled})");
    }

    public void SetTrueBypass(bool enabled)
    {
        Span<byte> pkt = stackalloc byte[5];
        pkt[0] = 0x45; pkt[1] = 0x02; pkt[2] = (byte)(enabled ? 1 : 0);
        pkt[3] = 0x00; pkt[4] = 0x00;
        SendTlvCommand("SYS:45", pkt, $"MIXER TRUE BYPASS ({enabled})");
    }

    // =========================================================================
    // REAL-TIME HARDWARE METERING (Non-Blocking Background Poll + Lock-Free UI Read)
    // =========================================================================

    public void UpdateMeters(IReadOnlyList<TrackChannelModel> tracks, TrackChannelModel? selectedTrack, out double masterLeftLinear, out double masterRightLinear)
    {
        _selectedCompPollChannel = (selectedTrack != null && selectedTrack.Compressor.IsEnabled)
            ? selectedTrack.ChannelNumber
            : 0;

        if (IsHardwareConnected)
        {
            for (int i = 0; i < Math.Min(16, tracks.Count); i++)
            {
                double norm = Volatile.Read(ref _hwTrackPeaks[i]);
                if (norm < 0.0002) norm = 0.0;
                var trk = tracks[i];
                trk.PeakLevelLinear = norm;
                if (norm >= trk.PeakHoldLinear)
                {
                    trk.PeakHoldLinear = norm;
                }
                else
                {
                    double nextHold = trk.PeakHoldLinear * 0.97;
                    trk.PeakHoldLinear = nextHold < 0.0002 ? 0.0 : nextHold;
                }
            }

            if (selectedTrack != null && selectedTrack.Compressor.IsEnabled)
            {
                double gr = Volatile.Read(ref _hwSelectedGrDb);
                selectedTrack.Compressor.GainReductionDb = gr < 0.02 ? 0.0 : gr;
            }
            else if (selectedTrack != null)
            {
                selectedTrack.Compressor.GainReductionDb = 0.0;
            }

            double mL = Volatile.Read(ref _hwMasterL);
            double mR = Volatile.Read(ref _hwMasterR);
            masterLeftLinear = mL < 0.0002 ? 0.0 : mL;
            masterRightLinear = mR < 0.0002 ? 0.0 : mR;
            return;
        }

        masterLeftLinear = 0.0;
        masterRightLinear = 0.0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var trk = tracks[i];
            if (trk.PeakLevelLinear > 0.0)
            {
                double next = trk.PeakLevelLinear * 0.82;
                trk.PeakLevelLinear = next < 0.0002 ? 0.0 : next;
            }
            if (trk.PeakHoldLinear > 0.0)
            {
                double nextHold = trk.PeakHoldLinear * 0.96;
                trk.PeakHoldLinear = nextHold < 0.0002 ? 0.0 : nextHold;
            }
            if (trk.Compressor.GainReductionDb > 0.0)
            {
                double nextGr = trk.Compressor.GainReductionDb * 0.85;
                trk.Compressor.GainReductionDb = nextGr < 0.02 ? 0.0 : nextGr;
            }
        }
    }

    /// <summary>
    /// Exact implementation of 0x456610 (usctl_get_grmeter) running on the background worker thread:
    /// Sends [0x62, 0x02, ch, 0x61, 0x02, 0x04, 0xB1, 0x01, 0x00, 0x00] and reads tag 0xB2 (uint16).
    /// </summary>
    private void PollHardwareGrMeterBackground(int channelNumber)
    {
        if (HasPendingCommands)
            return;

        lock (_ioLock)
        {
            Span<byte> cmd = stackalloc byte[10];
            cmd[0] = 0x62; cmd[1] = 0x02; cmd[2] = (byte)channelNumber;
            cmd[3] = 0x61; cmd[4] = 0x02; cmd[5] = GROUP_INPUT_TRACK;
            cmd[6] = 0xB1; cmd[7] = 0x01;
            cmd[8] = 0x00; cmd[9] = 0x00;

            if (!TransmitTlvPacketHardware(cmd))
                return;

            Span<byte> reply = stackalloc byte[64];
            if (!VendorControlRead(USB_REQ_READ_TLV, 0, 0, reply))
                return;

            int idx = 0;
            while (idx + 1 < reply.Length)
            {
                byte tag = reply[idx];
                byte type = reply[idx + 1];
                if (tag == 0 && type == 0) break;

                if (type == 1) idx += 2;
                else if (type == 2) idx += 3;
                else if (type == 3 && idx + 3 < reply.Length)
                {
                    ushort val = (ushort)(reply[idx + 2] | (reply[idx + 3] << 8));
                    if (tag == 0xB2)
                    {
                        double grDb = (val >= 0x8000 || val == 0)
                            ? 0.0
                            : Math.Clamp(-20.0 * Math.Log10(val / 32768.0), 0.0, 24.0);
                        Volatile.Write(ref _hwSelectedGrDb, grDb);
                        return;
                    }
                    idx += 4;
                }
                else break;
            }
        }
    }

    /// <summary>
    /// Exact implementation of 0x456270 (usctl_get_meter) running on the background worker thread:
    /// Sends [0x71, 0x01, 0x00, 0x00] and reads 64-byte chunks via 0x1E until all 18 meter values (0x72) are received.
    /// </summary>
    private void PollHardwareMetersBackground()
    {
        if (HasPendingCommands)
            return;

        lock (_ioLock)
        {
            Span<byte> cmd = stackalloc byte[4];
            cmd[0] = 0x71;
            cmd[1] = 0x01;
            cmd[2] = 0x00;
            cmd[3] = 0x00;

            if (!TransmitTlvPacketHardware(cmd))
                return;

            Span<byte> reply = stackalloc byte[64];
            int totalReceived = 0;

            for (int chunk = 0; chunk < 4 && totalReceived < 18; chunk++)
            {
                if (!VendorControlRead(USB_REQ_READ_TLV, 0, 0, reply))
                    break;

                int idx = 0;
                byte curGroup = 0;
                byte curCh = 0;
                while (idx + 1 < reply.Length)
                {
                    byte tag = reply[idx];
                    byte type = reply[idx + 1];
                    if (tag == 0 && type == 0) break;

                    if (type == 1) idx += 2;
                    else if (type == 2 && idx + 2 < reply.Length)
                    {
                        byte val = reply[idx + 2];
                        if (tag == 0x61) curGroup = val;
                        else if (tag == 0x62) curCh = val;
                        idx += 3;
                    }
                    else if (type == 3 && idx + 3 < reply.Length)
                    {
                        ushort val = (ushort)(reply[idx + 2] | (reply[idx + 3] << 8));
                        if (tag == 0x72)
                        {
                            totalReceived++;
                            if (val > 0x8000) val = 0;
                            double norm = Math.Clamp(val / 32768.0, 0.0, 1.0);
                            if (curGroup == GROUP_INPUT_TRACK && curCh >= 1 && curCh <= 16)
                            {
                                Volatile.Write(ref _hwTrackPeaks[curCh - 1], norm);
                            }
                            else if (curGroup == GROUP_MASTER)
                            {
                                if (curCh == 1) Volatile.Write(ref _hwMasterL, norm);
                                else if (curCh == 2) Volatile.Write(ref _hwMasterR, norm);
                            }
                        }
                        idx += 4;
                    }
                    else break;
                }
            }
        }
    }

    private void CloseHandleInternal()
    {
        if (_deviceHandle != IntPtr.Zero && _deviceHandle != new IntPtr(-1))
        {
            CloseHandle(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        // Drain any pending TLV commands so hardware state is completely synced
        int spin = 0;
        while (HasPendingCommands && spin++ < 80)
        {
            _txSignal.Set();
            Thread.Sleep(10);
        }

        _disposed = true;
        _txSignal.Set();
        lock (_ioLock)
        {
            CloseHandleInternal();
        }
    }

    // =========================================================================
    // WIN32 P/INVOKE DECLARATIONS
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid ClassGuid,
        IntPtr Enumerator,
        IntPtr hwndParent,
        uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet,
        uint MemberIndex,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property,
        out uint PropertyRegDataType,
        byte[] PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref Guid InterfaceClassGuid,
        uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize,
        IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
