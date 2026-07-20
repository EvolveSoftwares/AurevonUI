#pragma warning disable CS0618
using AurevonUI;

namespace AurevonUITest;

internal class Program
{
    static void Main()
    {
        FrameContext.LoadEmbeddedFonts();
        var OptionsConfig = new RenderOptions
        {
            Antialiasing = true,
            MsaaSamples = 8,
            RenderScale = 2.0,
            FilterQuality = new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell)
        };

        Window.Initialize(new MainWindow(), OptionsConfig);
        Window.Run();
    }
}
