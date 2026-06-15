namespace EDMAC;

public sealed class NoteMapping
{
    private NoteMapping(bool isChordRelative, int[] values)
    {
        IsChordRelative = isChordRelative;
        Values = values;
    }

    public bool IsChordRelative { get; }

    public IReadOnlyList<int> Values { get; }

    public int Count => Values.Count;

    public static NoteMapping Absolute(int note)
    {
        return new NoteMapping(false, [note]);
    }

    public static NoteMapping ChordRelative(IEnumerable<int> indices)
    {
        int[] values = indices.ToArray();
        if (values.Length == 0)
        {
            throw new InvalidDataException("A chord-relative mapping cannot be empty.");
        }

        return new NoteMapping(true, values);
    }

    public int Resolve(int valueIndex, Chord? chord)
    {
        int value = Values[valueIndex];
        return IsChordRelative
            ? chord?.GetTone(value)
                ?? throw new InvalidOperationException(
                    "A chord-relative mapping requires at least one chord progression.")
            : value;
    }

    public override string ToString()
    {
        return IsChordRelative
            ? $"[{string.Join(", ", Values)}]"
            : Values[0].ToString();
    }
}
