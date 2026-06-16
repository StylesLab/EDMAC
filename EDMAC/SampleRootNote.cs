using System.Text.RegularExpressions;

namespace EDMAC;

public sealed partial class SampleRootNote
{
    private static readonly IReadOnlyDictionary<char, int> NaturalPitchClasses =
        new Dictionary<char, int>
        {
            ['C'] = 0,
            ['D'] = 2,
            ['E'] = 4,
            ['F'] = 5,
            ['G'] = 7,
            ['A'] = 9,
            ['B'] = 11
        };

    private SampleRootNote(int pitchClass, int? midiNote)
    {
        PitchClass = pitchClass;
        MidiNote = midiNote;
    }

    public int PitchClass { get; }

    public int? MidiNote { get; }

    public int GetReferenceMidiNote(int targetMidiNote)
    {
        if (MidiNote is int midiNote)
        {
            return midiNote;
        }

        int targetOctave = (targetMidiNote / 12) - 1;
        int reference = (targetOctave + 1) * 12 + PitchClass;

        while (reference - targetMidiNote > 6)
        {
            reference -= 12;
        }

        while (targetMidiNote - reference > 6)
        {
            reference += 12;
        }

        return reference;
    }

    public static SampleRootNote Parse(string text)
    {
        Match match = SampleRootNoteRegex().Match(text.Trim());
        if (!match.Success)
        {
            throw new InvalidDataException(
                $"Sample note '{text}' is invalid. Use names such as c, f#, bb, or c4.");
        }

        char noteName = char.ToUpperInvariant(match.Groups["note"].Value[0]);
        int pitchClass = NaturalPitchClasses[noteName];
        string accidental = match.Groups["accidental"].Value;

        if (accidental == "#")
        {
            pitchClass++;
        }
        else if (accidental.Equals("b", StringComparison.OrdinalIgnoreCase))
        {
            pitchClass--;
        }

        pitchClass = (pitchClass + 12) % 12;

        if (!match.Groups["octave"].Success)
        {
            return new SampleRootNote(pitchClass, null);
        }

        int octave = int.Parse(match.Groups["octave"].Value);
        int midiNote = checked((octave + 1) * 12 + pitchClass);
        if (midiNote is < 0 or > 127)
        {
            throw new InvalidDataException(
                $"Sample note '{text}' resolves outside the MIDI note range.");
        }

        return new SampleRootNote(pitchClass, midiNote);
    }

    [GeneratedRegex(
        "^(?<note>[A-Ga-g])(?<accidental>[#b]?)(?<octave>-?\\d+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SampleRootNoteRegex();
}
