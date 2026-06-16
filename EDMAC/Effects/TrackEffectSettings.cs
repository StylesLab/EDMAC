namespace EDMAC.Effects;

public sealed class TrackEffectSettings
{
    public required string Type { get; init; }

    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
}
