using NAudio.Wave;

namespace EDMAC;

public sealed class SampleInstrument : IInstrument
{
    private const int MaxVoices = 24;
    private readonly object voiceLock = new();
    private readonly float[] left;
    private readonly float[] right;
    private readonly Voice[] voices = Enumerable
        .Range(0, MaxVoices)
        .Select(_ => new Voice())
        .ToArray();
    private readonly int sourceSampleRate;
    private readonly int outputSampleRate;
    private readonly SampleRootNote rootNote;

    public SampleInstrument(Track track, int outputSampleRate)
    {
        if (track.SamplePath is null)
        {
            throw new InvalidOperationException("Sample track requires a sample path.");
        }

        if (!File.Exists(track.SamplePath))
        {
            throw new FileNotFoundException(
                $"Sample for track '{track.Name}' was not found.",
                track.SamplePath);
        }

        this.outputSampleRate = outputSampleRate;
        rootNote = track.SampleRootNote ?? SampleRootNote.Parse("c");
        Amp = track.Amp;

        (left, right, sourceSampleRate) = LoadSample(track.SamplePath);
    }

    public float Amp { get; }

    public void NoteOn(int channel, int note, int velocity, bool playToCompletion = false)
    {
        double pitchRatio = Math.Pow(
            2.0,
            (note - rootNote.GetReferenceMidiNote(note)) / 12.0);
        double increment = pitchRatio * sourceSampleRate / outputSampleRate;
        float gain = velocity / 127.0f;

        lock (voiceLock)
        {
            Voice voice = voices.FirstOrDefault(candidate => !candidate.Active) ?? voices[0];
            voice.Active = true;
            voice.Channel = channel;
            voice.Position = 0;
            voice.Increment = increment;
            voice.Gain = gain;
            voice.PlayToCompletion = playToCompletion;
        }
    }

    public void Render(Span<float> outputLeft, Span<float> outputRight)
    {
        lock (voiceLock)
        {
            foreach (Voice voice in voices)
            {
                if (!voice.Active)
                {
                    continue;
                }

                for (var frame = 0; frame < outputLeft.Length; frame++)
                {
                    int index = (int)voice.Position;
                    if (index >= left.Length - 1)
                    {
                        voice.Active = false;
                        break;
                    }

                    float fraction = (float)(voice.Position - index);
                    outputLeft[frame] += Lerp(left[index], left[index + 1], fraction) * voice.Gain;
                    outputRight[frame] += Lerp(right[index], right[index + 1], fraction) * voice.Gain;
                    voice.Position += voice.Increment;
                }
            }
        }
    }

    public void StopAll()
    {
        lock (voiceLock)
        {
            foreach (Voice voice in voices)
            {
                voice.Active = false;
            }
        }
    }

    public void StopChannel(int channel, bool includePlayToCompletion = false)
    {
        lock (voiceLock)
        {
            foreach (Voice voice in voices)
            {
                if (voice.Channel == channel &&
                    (includePlayToCompletion || !voice.PlayToCompletion))
                {
                    voice.Active = false;
                }
            }
        }
    }

    private static (float[] Left, float[] Right, int SampleRate) LoadSample(string path)
    {
        using var reader = new AudioFileReader(path);
        int channels = reader.WaveFormat.Channels;
        var interleaved = new List<float>();
        var buffer = new float[reader.WaveFormat.SampleRate * channels];

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                interleaved.Add(buffer[index]);
            }
        }

        int frames = interleaved.Count / channels;
        if (frames == 0)
        {
            throw new InvalidDataException($"Sample '{path}' contains no audio frames.");
        }

        var left = new float[frames + 1];
        var right = new float[frames + 1];

        for (var frame = 0; frame < frames; frame++)
        {
            int sourceIndex = frame * channels;
            left[frame] = interleaved[sourceIndex];
            right[frame] = channels > 1 ? interleaved[sourceIndex + 1] : left[frame];
        }

        left[^1] = left[^2];
        right[^1] = right[^2];
        return (left, right, reader.WaveFormat.SampleRate);
    }

    private static float Lerp(float a, float b, float amount)
    {
        return a + (b - a) * amount;
    }

    private sealed class Voice
    {
        public bool Active;

        public int Channel;

        public double Position;

        public double Increment;

        public float Gain;

        public bool PlayToCompletion;
    }
}
