#pragma warning disable CS0618
namespace AurevonUI;

public class RenderOptions
{

    public bool Antialiasing { get; set; } = true;

    public int MsaaSamples { get; set; } = 4;

    public SkiaSharp.SKSamplingOptions FilterQuality { get; set; } =
        new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell);

    public double RenderScale { get; set; } = 1.0;
}
#pragma warning restore CS0618
