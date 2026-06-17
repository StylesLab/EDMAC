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

    private readonly IInstrument[] instruments;
    private readonly IAudioEffect[][] effects;
    private readonly float[] renderLeft = new float[RenderBlockFrames];
    private readonly float[] renderRight = new float[RenderBlockFrames];
    private readonly WaveOutEvent output;
    private readonly AudioRecorder recorder;
    private long samplePosition;
    private bool disposed;

    public AudioEngine(
        IInstrument[] instruments,
        IAudioEffect[][] effects,
        int sampleRate)
    {
        this.instruments = instruments;
        this.effects = effects;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels);
        output = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };
        recorder = new AudioRecorder(WaveFormat);
        output.Init(this);
    }

    public WaveFormat WaveFormat { get; }

    public long SamplePosition => Interlocked.Read(ref samplePosition);

    public bool IsRecording => recorder.IsRecording;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        output.Play();
    }

    public void Stop()
    {
        if (!disposed)
        {
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
        Span<byte> bytes = buffer.AsSpan(offset, count);
        Span<float> outputSamples = MemoryMarshal.Cast<byte, float>(bytes);
        outputSamples.Clear();

        int totalFrames = outputSamples.Length / Channels;
        int frameOffset = 0;

        while (frameOffset < totalFrames)
        {
            int frames = Math.Min(RenderBlockFrames, totalFrames - frameOffset);
            Span<float> left = renderLeft.AsSpan(0, frames);
            Span<float> right = renderRight.AsSpan(0, frames);
            Span<float> destination = outputSamples.Slice(frameOffset * Channels, frames * Channels);

            for (var instrumentIndex = 0; instrumentIndex < instruments.Length; instrumentIndex++)
            {
                IInstrument instrument = instruments[instrumentIndex];
                left.Clear();
                right.Clear();
                instrument.Render(left, right);

                foreach (IAudioEffect effect in effects[instrumentIndex])
                {
                    effect.Process(left, right);
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
        return count;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        output.Stop();
        output.Dispose();
        recorder.Dispose();

        foreach (IInstrument instrument in instruments)
        {
            instrument.StopAll();
        }
    }
}
