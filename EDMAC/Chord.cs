using System.Text.RegularExpressions;

namespace EDMAC;

public sealed partial class Chord
{
    private const int RootOctave = 2;

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

    private readonly int[] intervals;

    private Chord(string name, int rootMidiNote, bool minor)
    {
        Name = name;
        RootMidiNote = rootMidiNote;
        IsMinor = minor;
        intervals = minor ? [0, 3, 7] : [0, 4, 7];
    }

    public string Name { get; }

    public int RootMidiNote { get; }

    public bool IsMinor { get; }

    public Chord Transpose(int semitones)
    {
        return semitones == 0
            ? this
            : new Chord(Name, checked(RootMidiNote + semitones), IsMinor);
    }

    public int GetTone(int index)
    {
        int octave = Math.DivRem(index, intervals.Length, out int toneIndex);
        if (toneIndex < 0)
        {
            toneIndex += intervals.Length;
            octave--;
        }

        return checked(RootMidiNote + intervals[toneIndex] + octave * 12);
    }

    public static Chord Parse(string text)
    {
        Match match = ChordNameRegex().Match(text.Trim());
        if (!match.Success)
        {
            throw new InvalidDataException(
                $"Chord '{text}' is invalid. Use names such as Bm, G, C#, or Bb.");
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
        int rootMidiNote = (RootOctave + 1) * 12 + pitchClass;

        bool minor = match.Groups["quality"].Value.Equals(
            "m",
            StringComparison.OrdinalIgnoreCase);

        return new Chord(text.Trim(), rootMidiNote, minor);
    }

    [GeneratedRegex(
        "^(?<note>[A-Ga-g])(?<accidental>[#b]?)(?<quality>m?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChordNameRegex();
}
