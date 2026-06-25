using System.Collections;
using EDMAC.Effects;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EDMAC;

public static class SongLoader
{
    public static Song Load(string yamlPath, int sampleRate)
    {
        string fullPath = Path.GetFullPath(yamlPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Song YAML file was not found.", fullPath);
        }

        string yaml = File.ReadAllText(fullPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        SongDefinition definition = deserializer.Deserialize<SongDefinition>(yaml)
            ?? throw new InvalidDataException("The YAML document is empty.");

        ValidateSongDefinition(definition);
        string songDirectory = Path.GetDirectoryName(fullPath)!;
        IReadOnlyList<ChordProgression> progressions = ParseProgressions(definition.Progressions);

        var tracks = new List<Track>(definition.Tracks.Count);
        foreach (TrackDefinition trackDefinition in definition.Tracks)
        {
            tracks.Add(CreateTrack(
                trackDefinition,
                songDirectory,
                sampleRate,
                definition.Bpm,
                progressions.Count > 0));
        }

        ValidateResolvedMappings(tracks, progressions);
        ValidateEffects(tracks, sampleRate, definition.Bpm);
        ConsoleKey? startOnControl = ResolveStartOnControl(definition.StartOn, tracks);

        return new Song
        {
            Bpm = definition.Bpm,
            SampleRate = sampleRate,
            StartOnControl = startOnControl,
            Progressions = progressions,
            Tracks = tracks
        };
    }

    private static void ValidateEffects(
        IReadOnlyList<Track> tracks,
        int sampleRate,
        double bpm)
    {
        foreach (Track track in tracks)
        {
            try
            {
                EffectFactory.CreateEffects(track.Effects, sampleRate, bpm);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    $"Track '{track.Name}' effect configuration is invalid: {exception.Message}",
                    exception);
            }
        }
    }

    private static void ValidateResolvedMappings(
        IReadOnlyList<Track> tracks,
        IReadOnlyList<ChordProgression> progressions)
    {
        foreach (Track track in tracks)
        {
            foreach ((char symbol, NoteMapping mapping) in track.NoteMappings)
            {
                if (!mapping.IsChordRelative)
                {
                    continue;
                }

                foreach (ChordProgression progression in progressions)
                {
                    foreach (Chord chord in progression.Steps.Select(step => step.Chord))
                    {
                        for (var valueIndex = 0; valueIndex < mapping.Count; valueIndex++)
                        {
                            int note = mapping.Resolve(valueIndex, chord);
                            if (note is < 0 or > 127)
                            {
                                throw new InvalidDataException(
                                    $"Track '{track.Name}' mapping '{symbol}' resolves to MIDI " +
                                    $"note {note} for chord '{chord.Name}', outside the 0-127 range.");
                            }
                        }
                    }
                }
            }
        }
    }

    private static IReadOnlyList<ChordProgression> ParseProgressions(
        IReadOnlyList<ProgressionDefinition> definitions)
    {
        var progressions = new List<ChordProgression>(definitions.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProgressionDefinition definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new InvalidDataException("Every chord progression requires a name.");
            }

            if (!names.Add(definition.Name))
            {
                throw new InvalidDataException(
                    $"Chord progression name '{definition.Name}' is used more than once.");
            }

            if (definition.Chords.Count == 0)
            {
                throw new InvalidDataException(
                    $"Chord progression '{definition.Name}' requires at least one chord.");
            }

            progressions.Add(new ChordProgression
            {
                Name = definition.Name,
                Steps = ParseProgressionSteps(definition.Name, definition.Chords),
                Control = ParseControl(
                    definition.Control,
                    $"Chord progression '{definition.Name}'")
            });
        }

        return progressions;
    }

    private static IReadOnlyList<ChordProgressionStep> ParseProgressionSteps(
        string progressionName,
        IReadOnlyList<string> chordTexts)
    {
        var steps = new List<ChordProgressionStep>(chordTexts.Count);
        int startQuarter = 0;

        foreach (string chordText in chordTexts)
        {
            (Chord chord, int durationQuarters) = ParseProgressionStep(
                progressionName,
                chordText);

            steps.Add(new ChordProgressionStep
            {
                Chord = chord,
                DurationQuarters = durationQuarters,
                StartQuarter = startQuarter
            });

            startQuarter = checked(startQuarter + durationQuarters);
        }

        return steps;
    }

    private static (Chord Chord, int DurationQuarters) ParseProgressionStep(
        string progressionName,
        string text)
    {
        string trimmed = text.Trim();
        int separator = trimmed.LastIndexOf(':');

        if (separator < 0)
        {
            return (Chord.Parse(trimmed), ChordProgression.DefaultChordDurationQuarters);
        }

        string chordText = trimmed[..separator].Trim();
        string durationText = trimmed[(separator + 1)..].Trim();

        if (chordText.Length == 0 ||
            !int.TryParse(durationText, out int durationQuarters) ||
            durationQuarters <= 0)
        {
            throw new InvalidDataException(
                $"Chord '{text}' in progression '{progressionName}' must use a positive " +
                "duration such as A:3 or G:1.");
        }

        return (Chord.Parse(chordText), durationQuarters);
    }

    private static Track CreateTrack(
        TrackDefinition definition,
        string songDirectory,
        int sampleRate,
        double bpm,
        bool hasProgressions)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new InvalidDataException("Every track requires a name.");
        }

        bool hasSoundFont = !string.IsNullOrWhiteSpace(definition.Soundfont);
        bool hasSample = !string.IsNullOrWhiteSpace(definition.Sample);

        if (hasSoundFont == hasSample)
        {
            throw new InvalidDataException(
                $"Track '{definition.Name}' must specify exactly one of soundfont or sample.");
        }

        List<string> patternSources = definition.Patterns.Count > 0
            ? definition.Patterns
            : string.IsNullOrWhiteSpace(definition.Pattern)
                ? []
                : [definition.Pattern];

        if (patternSources.Count == 0)
        {
            throw new InvalidDataException(
                $"Track '{definition.Name}' requires at least one pattern.");
        }

        ValidateMidiValue(definition.Channel, 0, 15, definition.Name, "channel");
        ValidateMidiValue(definition.Bank, 0, 16383, definition.Name, "bank");
        ValidateMidiValue(definition.Program, 0, 127, definition.Name, "program");
        ValidateMidiValue(definition.Velocity, 1, 127, definition.Name, "velocity");
        ValidateAmp(definition.Amp, definition.Name);

        Dictionary<char, NoteMapping> mappings = ParseMappings(definition, hasProgressions);
        IReadOnlySet<ConsoleKey> controls = ParseOptionalControls(
            definition.Control,
            $"Track '{definition.Name}'");
        string? soundFontPath = hasSoundFont
            ? ResolveAssetPath(definition.Soundfont, songDirectory)
            : null;
        string? samplePath = hasSample
            ? ResolveAssetPath(definition.Sample, songDirectory)
            : null;
        SampleRootNote? sampleRootNote = hasSample
            ? ParseSampleRootNote(definition, samplePath!)
            : null;

        Pattern[] patterns = patternSources
            .Select(source => new Pattern(
                source,
                definition.Beats,
                sampleRate,
                bpm))
            .ToArray();

        if (!hasSample && patterns.Any(pattern => pattern.HasPlayToCompletionSteps))
        {
            throw new InvalidDataException(
                $"Track '{definition.Name}' uses '+', which is only supported for sample tracks.");
        }

        ValidatePatternSymbols(definition.Name, patterns, mappings);

        return new Track
        {
            Name = definition.Name,
            InstrumentKind = hasSoundFont
                ? TrackInstrumentKind.SoundFont
                : TrackInstrumentKind.Sample,
            SoundFontPath = soundFontPath,
            SamplePath = samplePath,
            SampleRootNote = sampleRootNote,
            Bank = definition.Bank,
            Program = definition.Program,
            Channel = definition.Channel,
            Velocity = definition.Velocity,
            Amp = definition.Amp,
            Effects = ParseEffects(definition.Name, definition.Effects),
            Controls = controls,
            NoteMappings = mappings,
            Patterns = patterns
        };
    }

    private static string ResolveAssetPath(string path, string songDirectory)
    {
        return Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, songDirectory);
    }

    private static IReadOnlyList<TrackEffectSettings> ParseEffects(
        string trackName,
        IReadOnlyList<Dictionary<string, object>> definitions)
    {
        var effects = new List<TrackEffectSettings>(definitions.Count);

        foreach (Dictionary<string, object> definition in definitions)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach ((string key, object value) in definition)
            {
                parameters[key] = value?.ToString() ?? string.Empty;
            }

            if (!parameters.TryGetValue("type", out string? type) ||
                string.IsNullOrWhiteSpace(type))
            {
                throw new InvalidDataException(
                    $"Track '{trackName}' has an effect without a type.");
            }

            parameters.Remove("type");

            effects.Add(new TrackEffectSettings
            {
                Type = type,
                Parameters = parameters
            });
        }

        return effects;
    }

    private static SampleRootNote ParseSampleRootNote(
        TrackDefinition definition,
        string samplePath)
    {
        string? noteText = string.IsNullOrWhiteSpace(definition.SampleNote)
            ? TryInferSampleNote(samplePath)
            : definition.SampleNote;

        if (string.IsNullOrWhiteSpace(noteText))
        {
            throw new InvalidDataException(
                $"Track '{definition.Name}' uses a sample but does not specify sampleNote, " +
                "and no trailing note name could be inferred from the file name.");
        }

        return SampleRootNote.Parse(noteText);
    }

    private static string? TryInferSampleNote(string samplePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(samplePath);
        string[] parts = fileName.Split(
            [' ', '_', '-'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length == 0 ? null : parts[^1];
    }

    private static Dictionary<char, NoteMapping> ParseMappings(
        TrackDefinition definition,
        bool hasProgressions)
    {
        var mappings = new Dictionary<char, NoteMapping>();

        foreach (Dictionary<string, object> mapEntry in definition.Map)
        {
            foreach ((string symbolText, object rawValue) in mapEntry)
            {
                if (symbolText.Length != 1 ||
                    symbolText[0] == Pattern.RestSymbol ||
                    symbolText[0] == Pattern.TieSymbol ||
                    symbolText[0] == Pattern.PlayToCompletionSymbol ||
                    symbolText[0] == '|')
                {
                    throw new InvalidDataException(
                        $"Track '{definition.Name}' mapping keys must be one character and cannot be '.', '>', '+', or '|'.");
                }

                NoteMapping mapping = ParseMappingValue(
                    rawValue,
                    definition.Name,
                    symbolText);

                if (mapping.IsChordRelative && !hasProgressions)
                {
                    throw new InvalidDataException(
                        $"Track '{definition.Name}' mapping '{symbolText}' is chord-relative, " +
                        "but the song has no chord progressions.");
                }

                if (!mappings.TryAdd(symbolText[0], mapping))
                {
                    throw new InvalidDataException(
                        $"Track '{definition.Name}' maps symbol '{symbolText}' more than once.");
                }
            }
        }

        if (mappings.Count == 0)
        {
            throw new InvalidDataException($"Track '{definition.Name}' requires at least one note mapping.");
        }

        return mappings;
    }

    private static NoteMapping ParseMappingValue(
        object rawValue,
        string trackName,
        string symbol)
    {
        if (rawValue is IEnumerable sequence and not string)
        {
            var indices = new List<int>();
            foreach (object? item in sequence)
            {
                indices.Add(ParseInteger(item, trackName, symbol));
            }

            return NoteMapping.ChordRelative(indices);
        }

        int note = ParseInteger(rawValue, trackName, symbol);
        ValidateMidiValue(note, 0, 127, trackName, $"note mapping '{symbol}'");
        return NoteMapping.Absolute(note);
    }

    private static int ParseInteger(object? value, string trackName, string symbol)
    {
        if (value is null ||
            !int.TryParse(value.ToString(), out int parsed))
        {
            throw new InvalidDataException(
                $"Track '{trackName}' mapping '{symbol}' must contain integer values.");
        }

        return parsed;
    }

    private static void ValidatePatternSymbols(
        string trackName,
        IReadOnlyList<Pattern> patterns,
        IReadOnlyDictionary<char, NoteMapping> mappings)
    {
        foreach (Pattern pattern in patterns)
        {
            foreach (char symbol in pattern.Steps)
            {
                if (symbol != Pattern.RestSymbol &&
                    symbol != Pattern.TieSymbol &&
                    !mappings.ContainsKey(symbol))
                {
                    throw new InvalidDataException(
                        $"Track '{trackName}' pattern uses unmapped symbol '{symbol}'.");
                }
            }
        }
    }

    private static ConsoleKey ParseControl(string control, string owner)
    {
        if (!Enum.TryParse(control, true, out ConsoleKey key) ||
            key < ConsoleKey.F1 ||
            key > ConsoleKey.F24)
        {
            throw new InvalidDataException(
                $"{owner} control must be a function key such as f1 or f2.");
        }

        return key;
    }

    private static IReadOnlySet<ConsoleKey> ParseOptionalControls(string controls, string owner)
    {
        var parsed = new HashSet<ConsoleKey>();

        if (string.IsNullOrWhiteSpace(controls))
        {
            return parsed;
        }

        foreach (string control in controls.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            parsed.Add(ParseControl(control, owner));
        }

        return parsed;
    }

    private static ConsoleKey? ResolveStartOnControl(string startOn, IReadOnlyList<Track> tracks)
    {
        ConsoleKey[] availableControls = tracks
            .SelectMany(track => track.Controls)
            .Distinct()
            .Order()
            .ToArray();

        if (availableControls.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(startOn))
            {
                throw new InvalidDataException("startOn was specified, but no track controls are defined.");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(startOn))
        {
            return availableControls[0];
        }

        ConsoleKey startOnControl = ParseControl(startOn, "Song startOn");
        if (!availableControls.Contains(startOnControl))
        {
            throw new InvalidDataException(
                $"Song startOn '{startOn}' does not match any track control.");
        }

        return startOnControl;
    }

    private static void ValidateSongDefinition(SongDefinition definition)
    {
        if (definition.Bpm <= 0 || double.IsNaN(definition.Bpm) || double.IsInfinity(definition.Bpm))
        {
            throw new InvalidDataException("BPM must be a finite value greater than zero.");
        }

        if (definition.Tracks.Count == 0)
        {
            throw new InvalidDataException("The song must contain at least one track.");
        }
    }

    private static void ValidateMidiValue(
        int value,
        int minimum,
        int maximum,
        string trackName,
        string field)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"Track '{trackName}' {field} must be between {minimum} and {maximum}.");
        }
    }

    private static void ValidateAmp(float amp, string trackName)
    {
        if (amp < 0 || float.IsNaN(amp) || float.IsInfinity(amp))
        {
            throw new InvalidDataException(
                $"Track '{trackName}' amp must be a finite value greater than or equal to zero.");
        }
    }

    private sealed class SongDefinition
    {
        public double Bpm { get; set; }

        [YamlMember(Alias = "startOn", ApplyNamingConventions = false)]
        public string StartOn { get; set; } = string.Empty;

        public List<ProgressionDefinition> Progressions { get; set; } = [];

        public List<TrackDefinition> Tracks { get; set; } = [];
    }

    private sealed class ProgressionDefinition
    {
        public string Name { get; set; } = string.Empty;

        public List<string> Chords { get; set; } = [];

        public string Control { get; set; } = string.Empty;
    }

    private sealed class TrackDefinition
    {
        public string Name { get; set; } = string.Empty;

        public string Soundfont { get; set; } = string.Empty;

        public string Sample { get; set; } = string.Empty;

        [YamlMember(Alias = "sampleNote", ApplyNamingConventions = false)]
        public string SampleNote { get; set; } = string.Empty;

        public int Bank { get; set; }

        public int Program { get; set; }

        public int Channel { get; set; }

        public int Velocity { get; set; } = 127;

        public float Amp { get; set; } = 1.0f;

        public List<Dictionary<string, object>> Effects { get; set; } = [];

        public List<Dictionary<string, object>> Map { get; set; } = [];

        public string Pattern { get; set; } = string.Empty;

        public List<string> Patterns { get; set; } = [];

        public int Beats { get; set; }

        public string Control { get; set; } = string.Empty;
    }
}
