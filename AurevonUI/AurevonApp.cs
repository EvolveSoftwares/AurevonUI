namespace AurevonUI;

public abstract class AurevonApp
{

    public string Title { get; set; } = "AurevonUI";

    private int _width = 1000;
    private int _height = 700;

    public int Width
    {
        get => (Host is not null && Host.IsWindowInitialized) ? Host.WindowWidth : _width;
        set
        {
            _width = value;
            if (Host is not null && Host.IsWindowInitialized) Host.WindowWidth = value;
        }
    }

    public int Height
    {
        get => (Host is not null && Host.IsWindowInitialized) ? Host.WindowHeight : _height;
        set
        {
            _height = value;
            if (Host is not null && Host.IsWindowInitialized) Host.WindowHeight = value;
        }
    }

    public WindowStyle WindowStyle { get; set; } = WindowStyle.Window;

    public WindowStartupLocation WindowStartupLocation { get; set; } = WindowStartupLocation.Default;

    public string? IconPath { get; set; }

    public int Fps => Host?.Fps ?? 0;

    internal Window? Host;

    public void Close() => Host?.CloseWindow();

    public void Minimize() => Host?.MinimizeWindow();

    public void ToggleMaximize() => Host?.ToggleMaximizeWindow();

    public void DragWindow() => Host?.BeginWindowDrag();

    public virtual void OnLoad() { }

    public abstract void OnUpdate(FrameContext ctx);

    public virtual void OnClose() { }

    internal virtual void HandleMouseMove(System.Numerics.Vector2 pos) { }
    internal virtual void HandleMouseDown(System.Numerics.Vector2 pos) { }
    internal virtual void HandleMouseUp(System.Numerics.Vector2 pos) { }
    internal virtual void HandleScroll(System.Numerics.Vector2 pos, float delta) { }
    internal virtual void HandleKeyChar(char c) { }
    internal virtual void HandleKeyDown(Silk.NET.Input.Key key) { }
    internal virtual void HandleKeyUp(Silk.NET.Input.Key key) { }

    internal virtual Cursor DesiredCursor => Cursor.Default;

    public void Run() => Window.Run(this);
}
