namespace EDMAC;

public interface IInstrument
{
    float Amp { get; }

    void NoteOn(int channel, int note, int velocity, bool playToCompletion = false);

    void Render(Span<float> left, Span<float> right);

    void StopAll();

    void StopChannel(int channel, bool includePlayToCompletion = false);
}
