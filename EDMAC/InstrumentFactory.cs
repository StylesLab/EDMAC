namespace EDMAC;

public static class InstrumentFactory
{
    public static IInstrument Create(Track track, int sampleRate)
    {
        return track.InstrumentKind switch
        {
            TrackInstrumentKind.SoundFont => new SoundFontInstrument(track, sampleRate),
            TrackInstrumentKind.Sample => new SampleInstrument(track, sampleRate),
            _ => throw new InvalidOperationException(
                $"Track '{track.Name}' has an unsupported instrument type.")
        };
    }
}
