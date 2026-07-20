#pragma warning disable CS0618
namespace AurevonUI;

public class RenderOptions
{

    public bool Antialiasing { get; set; } = true;

    public int MsaaSamples { get; set; } = 4;

    public SkiaSharp.SKSamplingOptions FilterQuality { get; set; } = SkiaSharp.SKSamplingOptions.Default;

    public double RenderScale { get; set; } = 1.0;
}
#pragma warning restore CS0618
