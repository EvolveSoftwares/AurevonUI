using System.Diagnostics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using SilkWindow = Silk.NET.Windowing.Window;

namespace AurevonUI;

public sealed class Window
{
    private static readonly List<Window> _active_windows = new();
    private static readonly Stopwatch _global_clock = Stopwatch.StartNew();
    private static double _last_global_tick_time = 0;

    private readonly Stopwatch _clock = new();
    private AurevonApp _app = null!;
    private readonly FrameContext _ctx = new();
    private IWindow _window = null!;
    private RenderOptions _render_options = null!;

    private IInputContext? _input;
    private IMouse? _mouse;
    private Cursor _applied_cursor = Cursor.Default;

    private GL? _gl;
    private GRGlInterface? _gl_interface;
    private GRContext? _gr_context;
    private GRBackendRenderTarget? _target;
    private SKSurface? _surface;
    private int _surf_w, _surf_h;

    private SKSurface? _ss_surface;
    private int _ss_w, _ss_h;

    private double _last_fps_time;
    private int _frames;
    private int _fps;

    public int Fps => _fps;

    private Window() { }

    public static void Initialize(AurevonApp App, RenderOptions? RenderOpts = null)
    {
        var Win = new Window();
        Win._app = App;
        App.Host = Win;
        Win._render_options = RenderOpts ?? new RenderOptions();

        int Width = App.Width;
        int Height = App.Height;

        bool Supersampling = Win._render_options.RenderScale > 1.001;
        int Samples = Win._render_options.Antialiasing && !Supersampling
            ? Math.Max(1, Win._render_options.MsaaSamples)
            : 1;

        var Options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(Width, Height),
            Title = App.Title,
            VSync = true,
            PreferredStencilBufferBits = 8,
            PreferredBitDepth = new Vector4D<int>(8, 8, 8, 8),
            Samples = Samples,
            IsVisible = false
        };

        if (App.WindowStartupLocation == WindowStartupLocation.CenterScreen)
        {
            var MainMonitor = Silk.NET.Windowing.Monitor.GetMainMonitor(null);
            if (MainMonitor is not null && MainMonitor.VideoMode.Resolution.HasValue)
            {
                var Res = MainMonitor.VideoMode.Resolution.Value;
                Options = Options with
                {
                    Position = new Vector2D<int>(
                        (Res.X - Width) / 2,
                        (Res.Y - Height) / 2)
                };
            }
        }

        if (App.WindowStyle == WindowStyle.None)
        {
            Options = Options with { WindowBorder = WindowBorder.Hidden, TransparentFramebuffer = true };
        }

        Win._window = SilkWindow.Create(Options);
        Win._window.Load += Win.OnLoad;
        Win._window.Render += Win.OnRender;
        Win._window.FramebufferResize += Win.OnResize;
        Win._window.Update += Win.OnUpdate;
        Win._window.Closing += Win.OnClosing;

        Win._window.Initialize();
        Win._window.IsVisible = true;

        _active_windows.Add(Win);
    }

    public static void Run()
    {
        while (_active_windows.Count > 0)
        {
            var Active = _active_windows[0];
            Active._window.Run();
        }
    }

    public static void Run(AurevonApp App)
    {
        Initialize(App);
        Run();
    }

    private static byte[]? LoadIconBytes(string? IconName)
    {
        if (string.IsNullOrEmpty(IconName))
            return null;

        var asm = System.Reflection.Assembly.GetEntryAssembly();
        if (asm is not null)
        {
            var NormalizedName = IconName.Replace('/', '.').Replace('\\', '.');
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (res.EndsWith(NormalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = asm.GetManifestResourceStream(res);
                    if (stream is not null)
                    {
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
        }

        var BasePath = Path.Combine(AppContext.BaseDirectory, IconName);
        if (File.Exists(BasePath))
            return File.ReadAllBytes(BasePath);
        if (File.Exists(IconName))
            return File.ReadAllBytes(IconName);

        return null;
    }

    private void OnLoad()
    {
        _window.GLContext!.MakeCurrent();

        _gl = GL.GetApi(_window);

        _gl.Enable(EnableCap.Multisample);

        _gl_interface = GRGlInterface.Create(Name =>
            _window.GLContext!.TryGetProcAddress(Name, out var Addr) ? Addr : IntPtr.Zero);
        _gr_context = GRContext.CreateGl(_gl_interface)
            ?? throw new Exception("Failed to create Skia GRContext (OpenGL).");

        _input = _window.CreateInput();
        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;
        if (_mouse is not null)
        {
            _mouse.MouseMove += (_, P) => _app.HandleMouseMove(P);
            _mouse.MouseDown += (M, B) => { if (B == MouseButton.Left) _app.HandleMouseDown(M.Position); };
            _mouse.MouseUp += (M, B) => { if (B == MouseButton.Left) _app.HandleMouseUp(M.Position); };
            _mouse.Scroll += (M, W) => _app.HandleScroll(M.Position, W.Y);
        }

        var Kb = _input.Keyboards.Count > 0 ? _input.Keyboards[0] : null;
        if (Kb is not null)
        {
            Kb.KeyChar += (_, C) => _app.HandleKeyChar(C);
            Kb.KeyDown += (_, K, _) => _app.HandleKeyDown(K);
            Kb.KeyUp += (_, K, _) => _app.HandleKeyUp(K);
        }

        var Fb = _window.FramebufferSize;
        CreateSurface(Fb.X, Fb.Y);

        var IconBytes = LoadIconBytes(_app.IconPath);
        if (IconBytes is not null)
        {
            using var IconBitmap = SKBitmap.Decode(IconBytes);
            if (IconBitmap is not null)
            {
                var Pixels = IconBitmap.Pixels;
                var RgbaBytes = new byte[Pixels.Length * 4];
                for (int I = 0; I < Pixels.Length; I++)
                {
                    RgbaBytes[I * 4 + 0] = Pixels[I].Red;
                    RgbaBytes[I * 4 + 1] = Pixels[I].Green;
                    RgbaBytes[I * 4 + 2] = Pixels[I].Blue;
                    RgbaBytes[I * 4 + 3] = Pixels[I].Alpha;
                }
                var RawImg = new Silk.NET.Core.RawImage(IconBitmap.Width, IconBitmap.Height, RgbaBytes.AsMemory());
                _window.SetWindowIcon(new ReadOnlySpan<Silk.NET.Core.RawImage>(ref RawImg));
            }
        }

        _app.OnLoad();
        _clock.Restart();
        Console.WriteLine($"Load — Skia GPU renderer ready for: {_app.Title}");
    }

    private void CreateSurface(int Width, int Height)
    {
        if (Width <= 0 || Height <= 0)
            return;

        _surface?.Dispose();
        _target?.Dispose();

        _gl!.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        int ActualSamples = _gl.GetInteger(GLEnum.Samples);
        if (ActualSamples < 1)
            ActualSamples = 1;

        int MaxSamples = _gr_context!.GetMaxSurfaceSampleCount(SKColorType.Rgba8888);
        if (MaxSamples < 1)
            MaxSamples = 1;
        if (ActualSamples > MaxSamples)
            ActualSamples = MaxSamples;

        var FbInfo = new GRGlFramebufferInfo(0, 0x8058);
        _target = new GRBackendRenderTarget(Width, Height, ActualSamples, 8, FbInfo);
        _surface = SKSurface.Create(_gr_context, _target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        _surf_w = Width;
        _surf_h = Height;
    }

    private void OnResize(Vector2D<int> Size)
    {
        _window.GLContext!.MakeCurrent();
        if (Size.X > 0 && Size.Y > 0)
        {
            CreateSurface(Size.X, Size.Y);

            RenderFrame(0, Size.X, Size.Y);
            _window.GLContext!.SwapBuffers();
        }
    }

    private void OnRender(double Delta)
    {
        _window.GLContext!.MakeCurrent();
        var Fb = _window.FramebufferSize;
        RenderFrame(Delta, Fb.X, Fb.Y);

    }

    private void RenderFrame(double Delta, int FbW, int FbH)
    {
        if (_surface is null || _gr_context is null)
            return;

        if (FbW == 0 || FbH == 0)
            return;
        if (FbW != _surf_w || FbH != _surf_h)
            CreateSurface(FbW, FbH);
        if (_surface is null)
            return;

        double Time = _clock.Elapsed.TotalSeconds;

        var BgColor = _app.WindowStyle == WindowStyle.None ? SKColors.Transparent : new SKColor(15, 18, 23);

        double CurrentFrameTime = _global_clock.Elapsed.TotalSeconds;
        if (Math.Abs(CurrentFrameTime - _last_global_tick_time) > 0.001)
        {
            Animator.Tick(Time, (float)Delta);
            _last_global_tick_time = CurrentFrameTime;
        }

        float Scale = (float)Math.Clamp(_render_options.RenderScale, 1.0, 4.0);
        if (Scale > 1.001f)
        {

            int Sw = Math.Max(1, (int)MathF.Ceiling(FbW * Scale));
            int Sh = Math.Max(1, (int)MathF.Ceiling(FbH * Scale));

            if (_ss_surface is null || _ss_w != Sw || _ss_h != Sh)
            {
                _ss_surface?.Dispose();
                _ss_surface = SKSurface.Create(_gr_context, true,
                    new SKImageInfo(Sw, Sh, SKColorType.Rgba8888, SKAlphaType.Premul));
                _ss_w = Sw;
                _ss_h = Sh;
            }

            var SsCanvas = _ss_surface!.Canvas;
            SsCanvas.Clear(BgColor);
            SsCanvas.Save();

            SsCanvas.Scale(Sw / (float)FbW, Sh / (float)FbH);
            _ctx.Begin((float)Time, (float)Delta, FbW, FbH, SsCanvas, _render_options);
            _app.OnUpdate(_ctx);
            SsCanvas.Restore();

            _ss_surface.Flush();
            using var Frame = _ss_surface.Snapshot();

            var WinCanvas = _surface.Canvas;
            WinCanvas.Clear(BgColor);
            WinCanvas.DrawImage(Frame, new SKRect(0, 0, FbW, FbH), _render_options.FilterQuality);
        }
        else
        {
            var Canvas = _surface.Canvas;
            Canvas.Clear(BgColor);
            _ctx.Begin((float)Time, (float)Delta, FbW, FbH, Canvas, _render_options);
            _app.OnUpdate(_ctx);
        }

        _gr_context.Flush();

        _frames++;
        if (Time - _last_fps_time >= 1.0)
        {
            _fps = (int)(_frames / (Time - _last_fps_time));
            _frames = 0;
            _last_fps_time = Time;
        }
    }

    private void OnUpdate(double Delta)
    {
        _window.Title = _app.Title;
        ApplyCursor(_app.DesiredCursor);
        UpdateWindowDrag();
    }

    private bool _dragging;
    private System.Numerics.Vector2 _drag_anchor;

    internal bool IsWindowInitialized => _window is not null;

    internal int WindowWidth
    {
        get => _window.Size.X;
        set => _window.Size = new Vector2D<int>(value, _window.Size.Y);
    }

    internal int WindowHeight
    {
        get => _window.Size.Y;
        set => _window.Size = new Vector2D<int>(_window.Size.X, value);
    }

    internal void CloseWindow() => _window.Close();

    internal void MinimizeWindow() => _window.WindowState = WindowState.Minimized;

    internal void ToggleMaximizeWindow() =>
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    internal void BeginWindowDrag()
    {
        if (_mouse is null) return;
        _dragging = true;
        _drag_anchor = _mouse.Position;
    }

    private void UpdateWindowDrag()
    {
        if (!_dragging) return;
        if (_mouse is null || !_mouse.IsButtonPressed(MouseButton.Left))
        {
            _dragging = false;
            return;
        }

        var Delta = _mouse.Position - _drag_anchor;
        if (MathF.Abs(Delta.X) >= 1f || MathF.Abs(Delta.Y) >= 1f)
            _window.Position += new Vector2D<int>((int)Delta.X, (int)Delta.Y);
    }

    private void OnClosing()
    {
        _app.OnClose();
        _ss_surface?.Dispose();
        _surface?.Dispose();
        _target?.Dispose();
        _gr_context?.Dispose();
        _gl_interface?.Dispose();
        _gl?.Dispose();
        _input?.Dispose();
        _active_windows.Remove(this);
        Console.WriteLine($"Closing window: {_app.Title}");
    }

    private void ApplyCursor(Cursor Cursor)
    {
        if (_mouse is null || Cursor == _applied_cursor)
            return;

        _mouse.Cursor.StandardCursor = Cursor switch
        {
            Cursor.Hand => StandardCursor.Hand,
            Cursor.Text => StandardCursor.IBeam,
            Cursor.Wait => StandardCursor.Wait,
            Cursor.Crosshair => StandardCursor.Crosshair,
            Cursor.No => StandardCursor.NotAllowed,
            Cursor.SizeAll => StandardCursor.ResizeAll,
            Cursor.SizeNS => StandardCursor.VResize,
            Cursor.SizeWE => StandardCursor.HResize,
            Cursor.Arrow => StandardCursor.Arrow,
            _ => StandardCursor.Default,
        };
        _applied_cursor = Cursor;
    }
}
