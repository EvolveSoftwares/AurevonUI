using System;

namespace AurevonUI.Elements;

public class Group : Control { }

public sealed class Path : Control
{
    private string? _data;

    public string? PathData
    {
        get => _data ?? GetGeometryAttribute("d");
        set
        {
            if (_data == value) return;
            _data = value;
            SetGeometryAttribute("d", value);
        }
    }
}

public sealed class Rect : Control { }

public sealed class Circle : Control { }

public sealed class Ellipse : Control { }

public sealed class Line : Control { }

public sealed class Polyline : Control { }

public sealed class Polygon : Control { }

public sealed class Image : Control
{

    public Bitmap? Source { get; set; }
}

public class TextControl : Control
{
    private string _text = "";

    public string Text
    {
        get => _text;
        set { var V = value ?? ""; if (_text == V) return; _text = V; OnTextChanged?.Invoke(_text); }
    }

    public string Placeholder { get; set; } = "";

    public Color TextColor { get; set; } = Color.White;

    public Color PlaceholderColor { get; set; } = new Color(255, 255, 255, 110);

    public event Action<string>? OnTextChanged;

    internal float TextSvgX, TextSvgY, TextSvgSize = 16f;
    internal string TextFontFamily = "Segoe UI";
    internal bool TextBold;
    internal string InitialText = "";
    internal bool HasCapturedText;
    internal bool Activated;
}

public sealed class TextBox : TextControl
{

    public bool IsFocused { get; internal set; }

    public event Action? OnSubmit;

    internal void RaiseSubmit() => OnSubmit?.Invoke();

    protected internal override bool WantsPointerInput => true;
}

public sealed class ScrollViewer : Group
{
    internal float ScrollOffsetY, ScrollMaxY;

    public float ScrollY
    {
        get => ScrollOffsetY;
        set => ScrollOffsetY = value < 0f ? 0f : (value > ScrollMaxY ? ScrollMaxY : value);
    }

    public float ScrollMax => ScrollMaxY;

    public float ScrollPaddingTop { get; set; }

    public float ScrollPaddingBottom { get; set; }

    public bool ScrollbarVisible { get; set; } = true;

    public float ScrollbarWidth { get; set; } = 6f;

    public float ScrollbarPadding { get; set; } = 4f;

    public Color ScrollbarColor { get; set; } = new Color(255, 255, 255, 180);

    public Color ScrollbarTrackColor { get; set; } = new Color(255, 255, 255, 40);
}
