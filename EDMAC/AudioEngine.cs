using System.Runtime.InteropServices;
using NAudio.Wave;

namespace EDMAC;

public sealed class AudioEngine : IWaveProvider, IDisposable
{
    public const int DefaultSampleRate = 44100;
    private const int Channels = 2;
    // Keep the shared render clock granular without scheduling events here.
    private const int RenderBlockFrames = 64;

    private readonly SoundFontInstrument[] instruments;
    private readonly float[] renderLeft = new float[RenderBlockFrames];
    private readonly float[] renderRight = new float[RenderBlockFrames];
    private readonly WaveOutEvent output;
    private long samplePosition;
    private bool disposed;

    public AudioEngine(SoundFontInstrument[] instruments, int sampleRate)
    {
        this.instruments = instruments;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels);
        output = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };
        output.Init(this);
    }

    public WaveFormat WaveFormat { get; }

    public long SamplePosition => Interlocked.Read(ref samplePosition);

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

            foreach (SoundFontInstrument instrument in instruments)
            {
                instrument.Render(left, right);

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

        foreach (SoundFontInstrument instrument in instruments)
        {
            instrument.StopAll();
        }
    }
}
