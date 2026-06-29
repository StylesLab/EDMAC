using System.Diagnostics;
using System.Runtime.InteropServices;
using EDMAC.Effects;
using NAudio.Wave;

namespace EDMAC;

public sealed class AudioEngine : IWaveProvider, IDisposable
{
    public const int DefaultSampleRate = 44100;
    private const int Channels = 2;
    // Larger chunks reduce callback overhead under heavy instrument/effect load.
    private const int RenderBlockFrames = 256;

    private readonly IReadOnlyList<Track> tracks;
    private readonly IInstrument[] instruments;
    private readonly IAudioEffect[][] effects;
    private readonly float[] renderLeft = new float[RenderBlockFrames];
    private readonly float[] renderRight = new float[RenderBlockFrames];
    private readonly WaveOutEvent output;
    private readonly AudioRecorder recorder;
    private int effectBypassReads;
    private long samplePosition;
    private bool stopRequested;
    private bool disposed;

    public AudioEngine(
        IReadOnlyList<Track> tracks,
        IInstrument[] instruments,
        IAudioEffect[][] effects,
        int sampleRate)
    {
        this.tracks = tracks;
        this.instruments = instruments;
        this.effects = effects;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels);
        output = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };
        recorder = new AudioRecorder(WaveFormat);
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(this);
    }

    public WaveFormat WaveFormat { get; }

    public long SamplePosition => Interlocked.Read(ref samplePosition);

    public bool IsRecording => recorder.IsRecording;

    public event EventHandler<AudioEngineStoppedEventArgs>? StoppedUnexpectedly;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        stopRequested = false;
        output.Play();
    }

    public void Stop()
    {
        if (!disposed)
        {
            stopRequested = true;
            output.Stop();
        }
    }

    public string StartRecording(string recordingsDirectory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return recorder.Start(recordingsDirectory);
    }

    public string? StopRecording()
    {
        return recorder.Stop();
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        long renderStartTimestamp = Stopwatch.GetTimestamp();
        Span<byte> bytes = buffer.AsSpan(offset, count);
        Span<float> outputSamples = MemoryMarshal.Cast<byte, float>(bytes);
        outputSamples.Clear();

        int totalFrames = outputSamples.Length / Channels;
        int frameOffset = 0;
        bool bypassEffects = Volatile.Read(ref effectBypassReads) > 0;

        while (frameOffset < totalFrames)
        {
            int frames = Math.Min(RenderBlockFrames, totalFrames - frameOffset);
            Span<float> left = renderLeft.AsSpan(0, frames);
            Span<float> right = renderRight.AsSpan(0, frames);
            Span<float> destination = outputSamples.Slice(frameOffset * Channels, frames * Channels);

            for (var instrumentIndex = 0; instrumentIndex < instruments.Length; instrumentIndex++)
            {
                if (!tracks[instrumentIndex].Enabled)
                {
                    continue;
                }

                IInstrument instrument = instruments[instrumentIndex];
                left.Clear();
                right.Clear();
                instrument.Render(left, right);

                if (!bypassEffects)
                {
                    foreach (IAudioEffect effect in effects[instrumentIndex])
                    {
                        effect.Process(left, right);
                    }
                }

                for (var frame = 0; frame < frames; frame++)
                {
                    int destinationIndex = frame * Channels;
                    destination[destinationIndex] += left[frame] * instrument.Amp;
                    destination[destinationIndex + 1] += right[frame] * instrument.Amp;
                }
            }

            frameOffset += frames;
            Interlocked.Add(ref samplePosition, frames);
        }

        recorder.Capture(buffer, offset, count);
        UpdatePerformanceGuard(renderStartTimestamp, totalFrames);
        return count;
    }

    private void UpdatePerformanceGuard(long renderStartTimestamp, int totalFrames)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - renderStartTimestamp;
        long bufferTicks = totalFrames * Stopwatch.Frequency / WaveFormat.SampleRate;
        long budgetTicks = bufferTicks * 7 / 10;

        if (elapsedTicks > budgetTicks)
        {
            Volatile.Write(ref effectBypassReads, 20);
            return;
        }

        if (Volatile.Read(ref effectBypassReads) > 0)
        {
            Interlocked.Decrement(ref effectBypassReads);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (disposed || stopRequested)
        {
            return;
        }

        StoppedUnexpectedly?.Invoke(
            this,
            new AudioEngineStoppedEventArgs(args.Exception));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stopRequested = true;
        output.PlaybackStopped -= OnPlaybackStopped;
        output.Stop();
        output.Dispose();
        recorder.Dispose();

        foreach (IInstrument instrument in instruments)
        {
            instrument.StopAll();
        }
    }
}

public sealed class AudioEngineStoppedEventArgs : EventArgs
{
    public AudioEngineStoppedEventArgs(Exception? exception)
    {
        Exception = exception;
    }

    public Exception? Exception { get; }
}
