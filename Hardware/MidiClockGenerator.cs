using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace US16x08_AbletonPanel.Hardware;

/// <summary>
/// Internal 24 PPQN MIDI clock master. Runs on its own high-priority thread using Stopwatch timing with a
/// sleep-then-spin wait, so pulse spacing does not depend on the 15.6 ms default Windows timer tick.
/// </summary>
public sealed class MidiClockGenerator : IDisposable
{
    public const double MinBpm = 20.0;
    public const double MaxBpm = 300.0;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    // (status byte, isBeat) -> send to the clock destination ports. isBeat is true on every 24th pulse.
    private readonly Action<byte, bool> _send;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);

    private long _bpmBits = BitConverter.DoubleToInt64Bits(120.0);
    private long _rateBits = BitConverter.DoubleToInt64Bits(1.0);
    private long _pulseCount;
    private volatile bool _transportRunning;
    private volatile bool _clockWhileStopped;
    private volatile bool _resync;
    private volatile bool _disposed;

    public MidiClockGenerator(Action<byte, bool> send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _thread = new Thread(ClockLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "TascamMidiClockGenerator"
        };
        _thread.Start();
    }

    public double Bpm
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _bpmBits));
        set => Interlocked.Exchange(ref _bpmBits, BitConverter.DoubleToInt64Bits(Math.Clamp(value, MinBpm, MaxBpm)));
    }

    /// <summary>Multiplier applied to the pulse rate (0.5 = half time, 2.0 = double time).</summary>
    public double RateMultiplier
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _rateBits));
        set => Interlocked.Exchange(ref _rateBits, BitConverter.DoubleToInt64Bits(Math.Clamp(value, 0.25, 4.0)));
    }

    public bool IsTransportRunning => _transportRunning;

    /// <summary>When true, 0xF8 pulses keep flowing while the transport is stopped (so arpeggiators stay in tempo).</summary>
    public bool ClockWhileStopped
    {
        get => _clockWhileStopped;
        set
        {
            _clockWhileStopped = value;
            _wake.Set();
        }
    }

    public bool IsEmitting => _transportRunning || _clockWhileStopped;

    /// <summary>Pulses emitted since the last Start.</summary>
    public long PulseCount => Interlocked.Read(ref _pulseCount);

    /// <summary>Sends MIDI Start (0xFA) and begins ticking from the top of the bar.</summary>
    public void Start()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pulseCount, 0);
        _send(0xFA, false);
        _resync = true;
        _transportRunning = true;
        _wake.Set();
    }

    /// <summary>Sends MIDI Continue (0xFB) and resumes ticking without resetting the position.</summary>
    public void Continue()
    {
        if (_disposed || _transportRunning) return;
        _send(0xFB, false);
        _resync = true;
        _transportRunning = true;
        _wake.Set();
    }

    /// <summary>Sends MIDI Stop (0xFC). Pulses stop too unless <see cref="ClockWhileStopped"/> is set.</summary>
    public void Stop()
    {
        if (_disposed) return;
        bool wasRunning = _transportRunning;
        _transportRunning = false;
        if (wasRunning)
            _send(0xFC, false);
    }

    private void ClockLoop()
    {
        bool highResTimer = false;
        long nextPulse = 0;
        long freq = Stopwatch.Frequency;

        try
        {
            while (!_disposed)
            {
                if (!IsEmitting)
                {
                    if (highResTimer)
                    {
                        timeEndPeriod(1);
                        highResTimer = false;
                    }
                    nextPulse = 0;
                    _wake.WaitOne(250);
                    continue;
                }

                if (!highResTimer)
                {
                    timeBeginPeriod(1);
                    highResTimer = true;
                }

                double pulsesPerSecond = Bpm * RateMultiplier * 24.0 / 60.0;
                long interval = Math.Max(1, (long)(freq / pulsesPerSecond));
                long now = Stopwatch.GetTimestamp();

                // First pulse, an explicit restart, or we fell more than 200 ms behind (machine was suspended / debugger): re-anchor.
                if (nextPulse == 0 || _resync || now - nextPulse > freq / 5)
                {
                    _resync = false;
                    nextPulse = now + interval;
                }

                bool aborted = false;
                while (true)
                {
                    now = Stopwatch.GetTimestamp();
                    long remaining = nextPulse - now;
                    if (remaining <= 0) break;
                    if (_disposed || !IsEmitting || _resync)
                    {
                        aborted = true;
                        break;
                    }

                    double remainingMs = remaining * 1000.0 / freq;
                    if (remainingMs > 3.0)
                        Thread.Sleep(1);
                    else
                        Thread.SpinWait(40);
                }

                if (aborted) continue;

                long pulse = Interlocked.Increment(ref _pulseCount);
                _send(0xF8, (pulse - 1) % 24 == 0);
                nextPulse += interval;
            }
        }
        finally
        {
            if (highResTimer)
                timeEndPeriod(1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transportRunning = false;
        _clockWhileStopped = false;
        _wake.Set();
        if (Thread.CurrentThread != _thread)
            _thread.Join(300);
    }
}
