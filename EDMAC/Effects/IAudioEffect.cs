namespace EDMAC.Effects;

public interface IAudioEffect
{
    void Process(Span<float> left, Span<float> right);
}
