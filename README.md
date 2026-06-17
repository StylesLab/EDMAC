# EDMAC

![EDMAC logo](EDMaC.png)

EDMAC is a real-time, YAML-driven tracker-style music engine for .NET 8. It
uses SoundFonts for instruments, loops patterns indefinitely, supports
multiple simultaneous pattern lanes, and lets you mute tracks or select chord
progressions with function keys.

EDMAC uses:

- YamlDotNet for song files
- MeltySynth for SoundFont playback
- NAudio for audio output

## Requirements

- Windows
- .NET 8 SDK or runtime
- One or more `.sf2` SoundFont files

Build the application from the repository root:

```powershell
dotnet build EDMAC\EDMAC.csproj
```

Play a song:

```powershell
dotnet run --project EDMAC\EDMAC.csproj -- EDMAC\song.yml
```

Validate a song without starting audio:

```powershell
dotnet run --project EDMAC\EDMAC.csproj -- validate EDMAC\song.yml
```

When running the built executable directly:

```powershell
EDMAC\bin\Debug\net8.0\edmac.exe song.yml
EDMAC\bin\Debug\net8.0\edmac.exe validate song.yml
```

Playback loads the YAML and waits for you to press `Space` before starting.

Playback keys:

| Key | Action |
| --- | --- |
| `Space` | Play / pause |
| `H` | Restart playback from the beginning |
| `R` | Start / stop recording the mixed output to WAV |
| `Q` | Quit |
| `Escape` | Quit |

Recordings are written under a `recordings` folder beside the running app and
are date/time stamped, for example `recordings/edmac-20260616-213045-123.wav`.

Playback mode watches the YAML file. When the file is edited and saved, EDMAC
reloads it and restarts playback. If the edited YAML is invalid, the current
song keeps playing and the reload error is printed.

## Complete Example

```yaml
bpm: 120

progressions:
  - name: verse
    chords:
      - Bm
      - G
      - D
      - A:3
      - G:1
    control: f9

  - name: chorus
    chords:
      - G
      - A
      - G
      - A
    control: f10

tracks:
  - name: kick
    soundfont: 'C:\SoundFonts\Drums.sf2'
    bank: 0
    program: 0
    channel: 0
    velocity: 127
    amp: 1.0
    map:
      - x: 36
    patterns:
      - x|x|x|x
    beats: 4
    control: f1

  - name: snare
    soundfont: 'C:\SoundFonts\Drums.sf2'
    map:
      - x: 38
    patterns:
      - ....x.......x...|....x.......x.xx
    beats: 16
    amp: 0.8
    control: f2

  - name: chords
    soundfont: 'C:\SoundFonts\Sawtooth Piano.sf2'
    map:
      - z: [0]
      - x: [1]
      - c: [2]
    patterns:
      - z
      - x
      - c
    beats: 1
    amp: 0.5
    control: f3
```

This example:

- Plays kick notes on quarter-note steps.
- Plays the snare on sixteenth-note steps.
- Plays the root, third, and fifth of the current chord for a full bar.
- Starts with the `verse` progression.
- Selects `verse` with `F9` and `chorus` with `F10`.
- Toggles kick, snare, and chords with `F1`, `F2`, and `F3`.

## Top-Level Fields

### `bpm`

Required. The tempo in quarter-note beats per minute.

```yaml
bpm: 120
```

The value must be finite and greater than zero.

### `progressions`

Optional unless a track uses chord-relative mappings such as `[0]`.

```yaml
progressions:
  - name: verse
    chords: [Bm, G, D, A]
    control: f9
```

By default, each chord lasts one bar. Add `:N` after a chord to set its
duration in quarters of a bar. For example, `A:3` lasts three quarters of a
bar and `G:1` lasts one quarter of a bar. A progression loops after its final
chord.

The first progression in the file is active when playback starts.

### `tracks`

Required. A song must contain at least one track.

Each track owns a separate MeltySynth synthesizer and SoundFont instance.
Sample tracks own a preloaded sample playback engine. Tracks may share the
same SoundFont or sample file while retaining independent playback, program,
volume, and mute state.

## Chord Progressions

Each progression has these fields:

| Field | Required | Meaning |
| --- | --- | --- |
| `name` | Yes | Unique progression name |
| `chords` | Yes | One or more chord names, optionally with `:duration` |
| `control` | Yes | Function key used to select or cycle the progression |

Example:

```yaml
progressions:
  - name: verse
    chords:
      - Bm
      - G
      - D
      - A:3
      - G:1
    control: f9
```

In that example, `Bm`, `G`, and `D` each last one bar. `A:3` lasts three
quarters of a bar, then `G:1` lasts the final quarter of the bar.

If progressions have different controls, pressing a control selects the
matching progression. If several progressions share the same control,
pressing that key cycles through those progressions.

Controls must be function keys from `f1` through `f24`.

### Chord Names

Supported chord names use this form:

```text
NOTE + optional accidental + optional m
```

Examples:

```text
C
Bm
C#
Bb
```

- No quality suffix means a major triad.
- `m` means a minor triad.
- Sharps and flats are supported.
- Chord roots use octave 2 internally: `C` is MIDI note 36 and `B` is MIDI note 47.

Extended names such as `Cmaj7`, `G7`, `Dsus4`, and slash chords are not
currently supported.

### Chord Durations

Chord durations are written after a colon:

```yaml
chords:
  - Bm
  - G
  - D
  - A:3
  - G:1
```

The duration unit is one quarter of a bar:

| Suffix | Duration |
| --- | --- |
| omitted | One full bar, equal to `:4` |
| `:1` | One quarter of a bar |
| `:2` | Half a bar |
| `:3` | Three quarters of a bar |
| `:4` | One full bar |
| `:8` | Two full bars |

Durations must be positive integers. They are progression timing values, not
MIDI note lengths. Track note lengths are still controlled by each track's
`beats` value.

## Track Fields

| Field | Required | Default | Meaning |
| --- | --- | --- | --- |
| `name` | Yes | - | Diagnostic name for the track |
| `soundfont` | Yes, unless `sample` is used | - | Path to a `.sf2` file |
| `sample` | Yes, unless `soundfont` is used | - | Path to an audio sample file |
| `sampleNote` | No | inferred from filename | Root note of a sample track |
| `bank` | No | `0` | MIDI bank, from 0 through 16383 |
| `program` | No | `0` | SoundFont preset program, from 0 through 127 |
| `channel` | No | `0` | MIDI channel, from 0 through 15 |
| `velocity` | No | `127` | Note velocity, from 1 through 127 |
| `amp` | No | `1.0` | Linear output gain for this track |
| `enabled` | No | `true` | Whether the track starts enabled |
| `status` | No | `enabled` | Alternative startup state: `enabled` or `disabled` |
| `effects` | No | none | Per-track effect chain |
| `map` | Yes | - | Pattern-symbol mappings |
| `patterns` | Yes | - | One or more simultaneous pattern lanes |
| `beats` | Yes | - | Number of steps per four-beat bar |
| `control` | Yes | - | Function key that toggles the track |

The older singular `pattern:` field is also accepted for songs containing one
pattern:

```yaml
pattern: x|x|x|x
```

Do not specify both forms. If `patterns:` is present and non-empty, it takes
precedence over `pattern:`.

Each track must specify exactly one audio source: either `soundfont` or
`sample`.

Startup state can be controlled with either `enabled`:

```yaml
enabled: false
```

or `status`:

```yaml
status: disabled
```

If neither is present, the track starts enabled. If both are present,
`enabled` takes precedence.

### SoundFont Paths

Paths may be absolute:

```yaml
soundfont: 'C:\SoundFonts\Drums.sf2'
```

Or relative to the YAML song file:

```yaml
soundfont: ./soundfonts/drums.sf2
```

Single quotes are recommended for Windows paths so backslashes are treated
literally.

This also works with Windows-style relative paths:

```yaml
soundfont: '.\soundfonts\drums.sf2'
```

EDMAC does not assume General MIDI or channel 10 drums. `bank`, `program`, and
`channel` are applied exactly as authored.

### Sample Tracks

As an alternative to a SoundFont, a track can play a single audio sample:

```yaml
- name: sampled-synth
  sample: '.\samples\mysynth c.wav'
  map:
    - x: [0, 1, 2]
  patterns:
    - x
  beats: 1
  amp: 0.5
  control: f4
```

The sample is loaded before playback and then pitched when notes are
triggered by the pattern. The audio callback only renders already loaded
sample data.

Relative sample paths are resolved relative to the YAML song file, not the
current terminal directory. For example, if `song.yml` is in `C:\Songs\909`,
this loads `C:\Songs\909\TR 909 Kick 01.wav`:

```yaml
sample: '.\TR 909 Kick 01.wav'
```

`sampleNote` tells EDMAC what note the original sample is playing:

```yaml
sample: 'd:\samples\mysynth.wav'
sampleNote: c
```

If `sampleNote` is omitted, EDMAC tries to infer it from the final token in
the filename. For example, this infers `c`:

```yaml
sample: 'd:\samples\mysynth c.wav'
```

Supported sample note names:

```text
c
f#
bb
c4
f#3
```

If the sample note has no octave, such as `c`, EDMAC treats it as a pitch
class and chooses the nearest matching C to the target MIDI note before
pitch-shifting. If an octave is supplied, such as `c4`, that exact MIDI note is
used as the pitch reference.

Samples are one-shot voices. They stop naturally at the end of the file, and
EDMAC also stops currently sounding sample voices at each track step boundary
and when the track is muted.

### `amp`

`amp` is a linear gain multiplier applied while mixing the track:

```yaml
amp: 0.5
```

Common values:

| Value | Result |
| --- | --- |
| `0` | Silent output |
| `0.5` | Half amplitude |
| `1.0` | Original amplitude |
| `2.0` | Double amplitude |

The value must be finite and non-negative. Values above `1.0` are allowed but
can clip when loud tracks are mixed together.

### `effects`

Each track can have an optional effect chain. Effects are processed in the
order they appear, after the instrument renders and before `amp` is applied:

```yaml
effects:
  - type: reverb
    mix: 0.25
    room: 0.8

  - type: delay
    time: dotted-eighth
    feedback: 0.35
    mix: 0.25

  - type: stereoWidth
    width: 1.4
```

Supported effect types:

| Type | Purpose |
| --- | --- |
| `reverb` | Room, hall, and atmosphere |
| `delay` or `echo` | Rhythmic repeats |
| `lowpass` | Removes highs |
| `highpass` | Removes lows |
| `bandpass` | Narrows the signal to a frequency band |
| `chorus` | Thickens synths and pads |
| `flanger` | Short modulated delay with feedback |
| `phaser` | Swirling all-pass modulation |
| `stereoWidth` | Mid/side stereo widening |

Common parameters:

| Parameter | Used by | Meaning |
| --- | --- | --- |
| `mix` | reverb, delay, chorus, flanger, phaser | Wet/dry amount from `0` to `1` |
| `feedback` | delay, chorus, flanger, phaser | Repeat or resonance amount |
| `room` | reverb | Reverb size from small to large |
| `damp` | reverb | High-frequency damping |
| `time` | delay | `quarter`, `eighth`, `dotted-eighth`, `sixteenth`, or milliseconds |
| `cutoff` | filters | Frequency in Hz |
| `q` | filters | Resonance / bandwidth |
| `from` / `to` | filters | Sweep start and end cutoff in Hz |
| `durationBeats` | filters | Sweep duration in quarter-note beats |
| `rateHz` | chorus, flanger, phaser | Modulation speed |
| `delayMs` | chorus, flanger | Base delay time |
| `depthMs` | chorus, flanger | Modulation depth |
| `depth` | phaser | Modulation depth |
| `width` | stereoWidth | `1` is unchanged, above `1` is wider, `0` is mono |

Filter sweep example:

```yaml
effects:
  - type: lowpass
    from: 300
    to: 8000
    durationBeats: 16
    q: 0.8
```

Delay values are tempo-synced unless you provide a number, which is treated as
milliseconds:

```yaml
effects:
  - type: delay
    time: quarter
  - type: delay
    time: 375
```

Effects are lightweight built-in DSP modules. They are not VST plugins.

## Patterns

A pattern is a string of tracker steps:

```yaml
patterns:
  - x|x|x|x
```

Pattern rules:

- `|` is visual formatting and is removed before playback.
- `.` is a rest.
- Any other character must exist in the track's `map`.
- Patterns loop indefinitely.
- Mapping symbols must be exactly one character.
- `.` and `|` cannot be mapping symbols.

For example:

```text
x|x|x|x
```

is compiled as:

```text
xxxx
```

### Step Timing With `beats`

`beats` defines how many pattern steps fit into one four-quarter-note bar:

| `beats` | Step duration |
| --- | --- |
| `1` | Whole bar / four beats |
| `2` | Half note / two beats |
| `4` | Quarter note / one beat |
| `8` | Eighth note |
| `16` | Sixteenth note |

At 120 BPM:

```yaml
patterns:
  - x|x|x|x
beats: 4
```

triggers every 0.5 seconds.

This track triggers once per bar and holds until the next bar boundary:

```yaml
patterns:
  - x
beats: 1
```

At every track step boundary, EDMAC releases that track's currently sounding
notes and then starts the notes mapped at the new step. A note therefore lasts
for at most one track step, although a SoundFont envelope may decay sooner.

### Multiple Pattern Lanes

Entries in `patterns` are simultaneous lanes, not sequential variations:

```yaml
map:
  - z: [0]
  - x: [1]
  - c: [2]
patterns:
  - z
  - x
  - c
beats: 1
```

All three lanes trigger at the same step, producing a chord for the whole
bar.

Lanes may have different lengths. Each lane loops independently using the
track's shared `beats` timing.

## Note Mappings

Mappings connect pattern symbols to notes.

### Absolute MIDI Notes

A scalar value is an absolute MIDI note:

```yaml
map:
  - x: 36
  - s: 38
```

Absolute notes must be between 0 and 127. They do not depend on the active
chord progression.

This form is useful for drums, samples, bass notes with fixed pitches, and
custom SoundFonts.

### Chord-Relative Notes

A YAML list maps a symbol to chord-tone indices:

```yaml
map:
  - z: [0]
  - x: [1]
  - c: [2]
```

Indices are zero-based positions in the current major or minor triad:

| Index | Chord tone |
| --- | --- |
| `0` | Root |
| `1` | Third |
| `2` | Fifth |
| `3` | Root, one octave higher |
| `4` | Third, one octave higher |
| `-1` | Fifth below the root |
| `-2` | Third below the root |
| `-3` | Root, one octave lower |

For `Bm`:

| Mapping | MIDI note | Result |
| --- | --- | --- |
| `[0]` | 47 | B2 |
| `[1]` | 50 | D3 |
| `[2]` | 54 | F#3 |
| `[3]` | 59 | B3 |
| `[-1]` | 42 | F#2 |

These are chord-tone indices, not semitone offsets or full scale degrees.

A single symbol can trigger several chord tones:

```yaml
map:
  - z: [0, 1, 2]
patterns:
  - z
beats: 1
```

This plays the root, third, and fifth together for one bar.

Chord-relative mappings require at least one top-level progression. EDMAC
validates every relative mapping against every chord and rejects notes that
would fall outside MIDI range 0 through 127.

## Live Controls

Global transport controls:

| Key | Action |
| --- | --- |
| `Space` | Toggle play/pause |
| `H` | Restart the current song from the beginning |
| `R` | Start / stop recording the mixed output to WAV |
| `Q` | Quit playback |
| `Escape` | Quit playback |

Recordings are saved under `recordings` beside the running app executable. Each
recording uses a date/time-stamped file name so previous takes are not
overwritten.

Track controls toggle mute state:

```yaml
control: f1
```

- Press once to mute the track.
- Press again to enable it.
- Muting immediately stops its current notes.
- The pattern timing continues while muted.
- Re-enabling rejoins the pattern at its current position.
- Tracks that start with `enabled: false` or `status: disabled` are enabled
  by pressing their function key.

Progression controls select or cycle progressions:

```yaml
progressions:
  - name: verse
    chords: [Bm, G, D, A:3, G:1]
    control: f9
```

A progression change takes effect when subsequent notes are generated. It
does not restart the song clock or progression position.

Avoid assigning the same key to a track and a progression unless you
intentionally want one key press to perform both actions.

## Validation

Use the validation command before playback:

```powershell
edmac validate song.yml
```

Validation prints:

- BPM
- Parsed progressions and controls
- Track names and controls
- Pattern count, lengths, and compiled steps
- Samples per step
- Track amplitude
- Note mappings
- The first 16 resolved events
- Sample positions, active chords, channels, notes, and velocities

Validation parses and checks the song without opening SoundFont files or
starting an audio device. SoundFont existence and readability are checked
when playback starts.

Example validation lines:

```text
Track=chords
Patterns=1
Pattern[0] Length=1 Steps=z
SamplesPerStep=88200
Amp=0.5
```

## Minimal Drum Song

Chord progressions are optional when all mappings use absolute MIDI notes:

```yaml
bpm: 100

tracks:
  - name: kick
    soundfont: ./drums.sf2
    map:
      - x: 36
    patterns:
      - x...x...
    beats: 8
    control: f1

  - name: snare
    soundfont: ./drums.sf2
    map:
      - x: 38
    patterns:
      - ..x...x.
    beats: 8
    control: f2
```

## Troubleshooting

### A pattern symbol is rejected

Every non-rest symbol must have a mapping:

```yaml
map:
  - x: 36
patterns:
  - x.x.
```

### Chord-relative mappings fail validation

Add at least one progression and ensure every resolved note is within MIDI
range:

```yaml
progressions:
  - name: main
    chords: [C]
    control: f9
```

### The wrong SoundFont preset plays

Set the exact custom SoundFont bank and program:

```yaml
bank: 0
program: 0
channel: 0
```

EDMAC does not force drum-channel conventions.

### A note stops too early

Notes are released at the next track step. Use a lower `beats` value or a
single-step pattern to increase their duration:

```yaml
patterns:
  - z
beats: 1
```

### A note rings or overlaps unexpectedly

Check the SoundFont envelope and sample loop settings. EDMAC sends a channel
release at every track step and immediately when a track is muted.

### The mix distorts

Reduce `amp` on one or more tracks. Multiple loud tracks are summed, so even
tracks at `amp: 1.0` can clip when combined.

### Function keys do not behave as expected

Check for duplicated controls. Shared progression controls cycle matching
progressions, while a key shared by a track and progression performs both
operations.

## Authoring Checklist

Before playback, confirm:

- `bpm` is greater than zero.
- Every track has a name, SoundFont, map, pattern, beats value, and control.
- Tracks that should start muted use `enabled: false` or `status: disabled`.
- Every pattern symbol is mapped.
- Absolute notes are in MIDI range 0 through 127.
- Relative mappings have at least one progression.
- Chord names use supported major/minor triad syntax.
- Chord duration suffixes, when present, are positive integers such as `A:3`.
- Function controls are between `f1` and `f24`.
- Relative SoundFont and sample paths are relative to the YAML file.
- `amp` values are non-negative.
- `edmac validate song.yml` reports the intended first events.

During playback, save the YAML file to hot-reload the song. Valid changes
restart playback from the beginning; invalid changes are ignored until the
next successful save.

## Playback Architecture

EDMAC keeps timing and audio rendering separate:

1. YAML is parsed before playback.
2. Patterns and chord mappings are compiled into runtime models.
3. A dedicated high-priority sequencer follows an absolute sample clock.
4. MIDI events are sent to MeltySynth through a producer/consumer queue.
5. The NAudio callback only renders and mixes preallocated audio buffers.

The audio callback does not parse YAML, calculate pattern timing, allocate
temporary arrays, or schedule notes.
