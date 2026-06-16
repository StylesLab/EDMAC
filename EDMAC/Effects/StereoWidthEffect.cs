namespace EDMAC.Effects;

public sealed class StereoWidthEffect : IAudioEffect
{
    private readonly float width;

    public StereoWidthEffect(float width)
    {
        this.width = Math.Clamp(width, 0, 3);
    }

    public void Process(Span<float> left, Span<float> right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            float mid = (left[index] + right[index]) * 0.5f;
            float side = (left[index] - right[index]) * 0.5f * width;
            left[index] = mid + side;
            right[index] = mid - side;
        }
    }
}
