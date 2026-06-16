namespace EDMAC;

using EDMAC.Effects;

public sealed class Track
{
    private int enabled = 1;

    public required string Name { get; init; }

    public required TrackInstrumentKind InstrumentKind { get; init; }

    public string? SoundFontPath { get; init; }

    public string? SamplePath { get; init; }

    public SampleRootNote? SampleRootNote { get; init; }

    public required int Bank { get; init; }

    public required int Program { get; init; }

    public required int Channel { get; init; }

    public required int Velocity { get; init; }

    public required float Amp { get; init; }

    public required IReadOnlyList<TrackEffectSettings> Effects { get; init; }

    public required ConsoleKey Control { get; init; }

    public required IReadOnlyDictionary<char, NoteMapping> NoteMappings { get; init; }

    public required IReadOnlyList<Pattern> Patterns { get; init; }

    public Pattern TimingPattern => Patterns[0];

    public bool Enabled => Volatile.Read(ref enabled) != 0;

    public bool Toggle()
    {
        int next = Enabled ? 0 : 1;
        Interlocked.Exchange(ref enabled, next);
        return next != 0;
    }
}

public enum TrackInstrumentKind
{
    SoundFont,
    Sample
}
