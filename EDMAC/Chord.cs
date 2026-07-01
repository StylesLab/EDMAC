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

    private Chord(string name, int rootMidiNote, bool minor, int[] intervals)
    {
        Name = name;
        RootMidiNote = rootMidiNote;
        IsMinor = minor;
        this.intervals = intervals;
    }

    public string Name { get; }

    public int RootMidiNote { get; }

    public bool IsMinor { get; }

    public Chord Transpose(int semitones)
    {
        return semitones == 0
            ? this
            : new Chord(Name, checked(RootMidiNote + semitones), IsMinor, intervals);
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
                $"Chord '{text}' is invalid. Use names such as Bm, G, C#, Bb, Cm7, Ebmaj7, or F7.");
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

        string quality = match.Groups["quality"].Value;
        bool minor = quality.Equals("m", StringComparison.OrdinalIgnoreCase) ||
            quality.Equals("m7", StringComparison.OrdinalIgnoreCase);

        int[] intervals = quality.ToLowerInvariant() switch
        {
            "m" => [0, 3, 7],
            "m7" => [0, 3, 7, 10],
            "7" => [0, 4, 7, 10],
            "maj7" => [0, 4, 7, 11],
            _ => [0, 4, 7]
        };

        return new Chord(text.Trim(), rootMidiNote, minor, intervals);
    }

    [GeneratedRegex(
        "^(?<note>[A-Ga-g])(?<accidental>[#b]?)(?<quality>maj7|m7|7|m?)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ChordNameRegex();
}
