using AurevonUI;

namespace AurevonUIApp;

internal class Program
{
    private static void Main()
    {
        FrameContext.LoadEmbeddedFonts();

        var Options = new RenderOptions
        {
            Antialiasing = true,
            //MsaaSamples = 8,
            RenderScale = 2.0,
            FilterQuality = new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell)
        };

        Window.Initialize(new MainWindow(), Options);
        Window.Run();
    }
}
