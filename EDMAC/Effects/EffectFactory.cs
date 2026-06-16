namespace EDMAC.Effects;

public static class EffectFactory
{
    public static IAudioEffect[] CreateEffects(
        IReadOnlyList<TrackEffectSettings> settings,
        int sampleRate,
        double bpm)
    {
        return settings
            .Select(setting => CreateEffect(setting, sampleRate, bpm))
            .ToArray();
    }

    private static IAudioEffect CreateEffect(
        TrackEffectSettings settings,
        int sampleRate,
        double bpm)
    {
        string type = Normalize(settings.Type);

        return type switch
        {
            "reverb" => new ReverbEffect(
                sampleRate,
                GetFloat(settings, "mix", 0.25f),
                GetFloat(settings, "room", 0.75f),
                GetFloat(settings, "damp", 0.35f)),
            "delay" or "echo" => new DelayEffect(
                sampleRate,
                GetDelaySamples(settings, sampleRate, bpm, "eighth"),
                GetFloat(settings, "feedback", 0.35f),
                GetFloat(settings, "mix", 0.3f)),
            "lowpass" or "lowpassfilter" or "low-pass" => new BiquadFilterEffect(
                FilterMode.LowPass,
                sampleRate,
                GetFloat(settings, "cutoff", 1000),
                GetFloat(settings, "q", 0.707f),
                GetNullableFloat(settings, "from"),
                GetNullableFloat(settings, "to"),
                GetFloat(settings, "durationBeats", 0),
                bpm),
            "highpass" or "highpassfilter" or "high-pass" => new BiquadFilterEffect(
                FilterMode.HighPass,
                sampleRate,
                GetFloat(settings, "cutoff", 300),
                GetFloat(settings, "q", 0.707f),
                GetNullableFloat(settings, "from"),
                GetNullableFloat(settings, "to"),
                GetFloat(settings, "durationBeats", 0),
                bpm),
            "bandpass" or "bandpassfilter" or "band-pass" => new BiquadFilterEffect(
                FilterMode.BandPass,
                sampleRate,
                GetFloat(settings, "cutoff", 1000),
                GetFloat(settings, "q", 1.0f),
                GetNullableFloat(settings, "from"),
                GetNullableFloat(settings, "to"),
                GetFloat(settings, "durationBeats", 0),
                bpm),
            "chorus" => new ModulatedDelayEffect(
                sampleRate,
                GetFloat(settings, "delayMs", 18),
                GetFloat(settings, "depthMs", 8),
                GetFloat(settings, "rateHz", 0.35f),
                GetFloat(settings, "feedback", 0.05f),
                GetFloat(settings, "mix", 0.35f)),
            "flanger" => new ModulatedDelayEffect(
                sampleRate,
                GetFloat(settings, "delayMs", 3),
                GetFloat(settings, "depthMs", 2),
                GetFloat(settings, "rateHz", 0.25f),
                GetFloat(settings, "feedback", 0.35f),
                GetFloat(settings, "mix", 0.35f)),
            "phaser" => new PhaserEffect(
                sampleRate,
                GetFloat(settings, "rateHz", 0.35f),
                GetFloat(settings, "depth", 0.75f),
                GetFloat(settings, "feedback", 0.15f),
                GetFloat(settings, "mix", 0.45f)),
            "stereowidth" or "stereo" or "width" or "widen" or "widening" => new StereoWidthEffect(
                GetFloat(settings, "width", 1.5f)),
            _ => throw new InvalidDataException($"Unsupported effect type '{settings.Type}'.")
        };
    }

    private static int GetDelaySamples(
        TrackEffectSettings settings,
        int sampleRate,
        double bpm,
        string defaultTime)
    {
        string time = GetString(settings, "time", defaultTime);
        double quarterSeconds = 60.0 / bpm;
        double seconds = Normalize(time) switch
        {
            "whole" or "bar" => quarterSeconds * 4,
            "half" => quarterSeconds * 2,
            "quarter" or "quarter-note" => quarterSeconds,
            "eighth" or "eighth-note" => quarterSeconds / 2,
            "sixteenth" or "sixteenth-note" => quarterSeconds / 4,
            "dottedeighth" or "dotted-eighth" => quarterSeconds * 0.75,
            "dottedquarter" or "dotted-quarter" => quarterSeconds * 1.5,
            _ when double.TryParse(time, out double milliseconds) => milliseconds / 1000.0,
            _ => throw new InvalidDataException(
                $"Delay time '{time}' is invalid. Use quarter, eighth, dotted-eighth, or milliseconds.")
        };

        return Math.Max(1, (int)Math.Round(seconds * sampleRate));
    }

    private static string GetString(
        TrackEffectSettings settings,
        string name,
        string defaultValue)
    {
        return TryGet(settings, name, out string? value) ? value ?? defaultValue : defaultValue;
    }

    private static float GetFloat(
        TrackEffectSettings settings,
        string name,
        float defaultValue)
    {
        if (!TryGet(settings, name, out string? value))
        {
            return defaultValue;
        }

        if (!float.TryParse(value, out float parsed) ||
            float.IsNaN(parsed) ||
            float.IsInfinity(parsed))
        {
            throw new InvalidDataException(
                $"Effect '{settings.Type}' parameter '{name}' must be a finite number.");
        }

        return parsed;
    }

    private static float? GetNullableFloat(TrackEffectSettings settings, string name)
    {
        return TryGet(settings, name, out _)
            ? GetFloat(settings, name, 0)
            : null;
    }

    private static bool TryGet(
        TrackEffectSettings settings,
        string name,
        out string? value)
    {
        return settings.Parameters.TryGetValue(name, out value) ||
            settings.Parameters.TryGetValue(ToCamelCase(name), out value) ||
            settings.Parameters.TryGetValue(name.ToLowerInvariant(), out value);
    }

    private static string Normalize(string value)
    {
        return value
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string ToCamelCase(string value)
    {
        return value.Length == 0
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];
    }
}
