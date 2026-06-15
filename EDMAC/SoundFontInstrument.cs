using MeltySynth;

namespace EDMAC;

public sealed class SoundFontInstrument
{
    private const int ControlChange = 0xB0;
    private const int ProgramChangeCommand = 0xC0;
    private const int BankSelectMsb = 0;
    private const int BankSelectLsb = 32;

    private readonly object synthLock = new();
    private readonly Synthesizer synthesizer;

    public SoundFontInstrument(Track track, int sampleRate)
    {
        if (!File.Exists(track.SoundFontPath))
        {
            throw new FileNotFoundException(
                $"SoundFont for track '{track.Name}' was not found.",
                track.SoundFontPath);
        }

        var soundFont = new SoundFont(track.SoundFontPath);
        synthesizer = new Synthesizer(soundFont, sampleRate);
        Amp = track.Amp;
        SelectBank(track.Channel, track.Bank);
        ProgramChange(track.Channel, track.Program);
    }

    public float Amp { get; }

    public void NoteOn(int channel, int note, int velocity)
    {
        lock (synthLock)
        {
            synthesizer.NoteOn(channel, note, velocity);
        }
    }

    public void Render(Span<float> left, Span<float> right)
    {
        lock (synthLock)
        {
            synthesizer.Render(left, right);
        }
    }

    public void StopAll()
    {
        lock (synthLock)
        {
            synthesizer.NoteOffAll(true);
        }
    }

    public void StopChannel(int channel)
    {
        lock (synthLock)
        {
            synthesizer.NoteOffAll(channel, true);
        }
    }

    private void SelectBank(int channel, int bank)
    {
        int msb = (bank >> 7) & 0x7F;
        int lsb = bank & 0x7F;
        synthesizer.ProcessMidiMessage(channel, ControlChange, BankSelectMsb, msb);
        synthesizer.ProcessMidiMessage(channel, ControlChange, BankSelectLsb, lsb);
    }

    private void ProgramChange(int channel, int program)
    {
        synthesizer.ProcessMidiMessage(channel, ProgramChangeCommand, program, 0);
    }
}
