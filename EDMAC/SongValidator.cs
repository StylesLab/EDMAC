namespace EDMAC;

public static class SongValidator
{
    public static void Print(Song song)
    {
        Console.WriteLine($"BPM={song.Bpm}");
        Console.WriteLine($"Progressions={song.Progressions.Count}");

        foreach (ChordProgression progression in song.Progressions)
        {
            Console.WriteLine(
                $"Progression={progression.Name} Control={progression.Control} " +
                $"Chords={string.Join(",", progression.Steps.Select(step => step.DisplayName))} " +
                $"DurationQuarters={progression.TotalDurationQuarters}");
        }

        Console.WriteLine($"Tracks={song.Tracks.Count}");

        foreach (Track track in song.Tracks)
        {
            Console.WriteLine();
            Console.WriteLine($"Track={track.Name}");
            Console.WriteLine($"Instrument={track.InstrumentKind}");
            Console.WriteLine($"Patterns={track.Patterns.Count}");

            for (var patternIndex = 0; patternIndex < track.Patterns.Count; patternIndex++)
            {
                Pattern pattern = track.Patterns[patternIndex];
                Console.WriteLine(
                    $"Pattern[{patternIndex}] Length={pattern.Length} Steps={pattern.Steps}");
            }

            Console.WriteLine($"SamplesPerStep={track.TimingPattern.SamplesPerStep}");
            Console.WriteLine($"Amp={track.Amp}");
            Console.WriteLine($"InitiallyEnabled={track.InitiallyEnabled}");
            Console.WriteLine($"Effects={string.Join(",", track.Effects.Select(effect => effect.Type))}");
            Console.WriteLine($"Control={track.Control}");
            Console.WriteLine("NoteMappings:");

            foreach ((char symbol, NoteMapping mapping) in
                     track.NoteMappings.OrderBy(entry => entry.Key))
            {
                Console.WriteLine($"  {symbol}={mapping}");
            }

            Console.WriteLine("First16Events:");
            ScheduledNote[] events = GetFirstEvents(song, track, 16).ToArray();

            if (events.Length == 0)
            {
                Console.WriteLine("  (none)");
                continue;
            }

            foreach (ScheduledNote scheduledNote in events)
            {
                Chord? chord = song.GetChord(scheduledNote.SamplePosition);
                Console.WriteLine(
                    $"  SamplePosition={scheduledNote.SamplePosition} " +
                    $"Chord={chord?.Name ?? "-"} Channel={scheduledNote.Channel} " +
                    $"Note={scheduledNote.Note} Velocity={scheduledNote.Velocity}");
            }
        }
    }

    private static IEnumerable<ScheduledNote> GetFirstEvents(
        Song song,
        Track track,
        int count)
    {
        int emitted = 0;

        for (long absoluteStep = 0; emitted < count; absoluteStep++)
        {
            long samplePosition = track.TimingPattern.GetSamplePosition(absoluteStep);
            Chord? chord = song.GetChord(samplePosition);

            foreach (Pattern pattern in track.Patterns)
            {
                char symbol = pattern.GetSymbol(absoluteStep);
                if (symbol == '.' ||
                    !track.NoteMappings.TryGetValue(symbol, out NoteMapping? mapping))
                {
                    continue;
                }

                for (var valueIndex = 0; valueIndex < mapping.Count; valueIndex++)
                {
                    int note = mapping.Resolve(valueIndex, chord);
                    ValidateResolvedNote(track.Name, symbol, note);

                    yield return new ScheduledNote(
                        samplePosition,
                        track.Channel,
                        note,
                        track.Velocity);

                    emitted++;
                    if (emitted == count)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    private static void ValidateResolvedNote(string trackName, char symbol, int note)
    {
        if (note is < 0 or > 127)
        {
            throw new InvalidDataException(
                $"Track '{trackName}' mapping '{symbol}' resolves outside the MIDI note range.");
        }
    }
}
