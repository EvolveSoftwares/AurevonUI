using System;
using System.Collections.Generic;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace AurevonUI;

public class Control
{

    public string Id { get; internal set; } = "";

    public Control? Parent { get; internal set; }

    public List<Control> Children { get; } = new();

    public bool IsHittable { get; set; } = true;

    public bool IsInteractive
        => IsHittable && (WantsPointerInput || OnClick is not null || OnPress is not null || OnHoverEnter is not null || OnHoverLeave is not null || Cursor != Cursor.Default || Command is not null);

    protected internal virtual bool WantsPointerInput => false;

    public bool StretchToWindow { get; set; }

    public Thickness Margin { get; set; } = new Thickness(0);

    public Thickness MarginPercent { get; set; } = new Thickness(0);

    internal bool HasMargin;
    internal bool HasMarginPercent;

    public Cursor Cursor { get; set; } = Cursor.Default;

    public ICommand? Command { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool Visible { get; set; } = true;

    public bool IsVisible
    {
        get => Visible;
        set => Visible = value;
    }

    public float Opacity { get; set; } = 1f;

    public float OffsetX { get; set; }

    public float OffsetY { get; set; }

    public float Scale { get; set; } = 1f;

    public HAlign? HorizontalAlignment { get; set; }

    public VAlign? VerticalAlignment { get; set; }

    public bool IsHovered { get; internal set; }

    public bool IsPressed { get; internal set; }

    public event Action? OnClick;

    public event Action? OnPress;

    public event Action? OnHoverEnter;

    public event Action? OnHoverLeave;

    internal float ContentScale = 1f;

    public float ScreenX { get; internal set; }
    public float ScreenY { get; internal set; }
    public float ScreenWidth { get; internal set; }
    public float ScreenHeight { get; internal set; }

    internal SKRect BoundsSvg;

    internal SKRect FrameBoundsSvg;

    internal float CornerInset;

    internal SKPicture? Picture;

    internal XDocument? Skeleton;

    internal XElement? SkeletonTarget;

    internal SKSvg? SvgOwner;

    internal bool StyleDirty;

    internal int DocOrder;

    internal bool HitTest(float px, float py)
        => Visible && IsEnabled && IsHittable &&
           px >= ScreenX && px <= ScreenX + ScreenWidth &&
           py >= ScreenY && py <= ScreenY + ScreenHeight;

    internal void RaiseClick()
    {
        if (!IsEnabled)
            return;
        OnClick?.Invoke();
        Command?.Execute();
    }

    internal void RaisePress()
    {
        if (!IsEnabled)
            return;
        OnPress?.Invoke();
    }

    internal void SetHovered(bool hovered)
    {
        if (IsHovered == hovered)
            return;
        IsHovered = hovered;
        if (hovered) OnHoverEnter?.Invoke();
        else OnHoverLeave?.Invoke();
    }

    internal XElement? XmlElement;
    internal XElement? AuiElement;

    private string? _fill;
    public string? Fill
    {
        get => _fill;
        set
        {
            if (_fill == value) return;
            _fill = value;
            UpdateXmlAttribute("fill", value);
        }
    }

    private string? _stroke;
    public string? Stroke
    {
        get => _stroke;
        set
        {
            if (_stroke == value) return;
            _stroke = value;
            UpdateXmlAttribute("stroke", value);
        }
    }

    private float? _stroke_width;
    public float? StrokeWidth
    {
        get => _stroke_width;
        set
        {
            if (_stroke_width == value) return;
            _stroke_width = value;
            UpdateXmlAttribute("stroke-width", value?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public Color? FillColor
    {
        get => Color.TryParse(Fill, out var C) ? C : (Color?)null;
        set
        {
            if (value is null)
            {
                Fill = null;
                UpdateXmlAttribute("fill-opacity", null);
            }
            else
            {
                var V = value.Value;
                Fill = $"#{V.R:X2}{V.G:X2}{V.B:X2}";
                UpdateXmlAttribute("fill-opacity", V.A < 255
                    ? (V.A / 255f).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null);
            }
        }
    }

    public Color? StrokeColor
    {
        get => Color.TryParse(Stroke, out var C) ? C : (Color?)null;
        set
        {
            if (value is null)
            {
                Stroke = null;
                UpdateXmlAttribute("stroke-opacity", null);
            }
            else
            {
                var V = value.Value;
                Stroke = $"#{V.R:X2}{V.G:X2}{V.B:X2}";
                UpdateXmlAttribute("stroke-opacity", V.A < 255
                    ? (V.A / 255f).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null);
            }
        }
    }

    internal void ReapplyStyleOverrides()
    {
        if (_fill is not null)
            UpdateXmlAttribute("fill", _fill);
        if (_stroke is not null)
            UpdateXmlAttribute("stroke", _stroke);
        if (_stroke_width is not null)
            UpdateXmlAttribute("stroke-width", _stroke_width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal void SetGeometryAttribute(string Name, string? Value)
    {
        var Target = SkeletonTarget ?? XmlElement;
        if (Target is null) return;
        if (Value is null) Target.Attribute(Name)?.Remove();
        else Target.SetAttributeValue(Name, Value);
        StyleDirty = true;
    }

    internal string? GetGeometryAttribute(string Name)
        => (string?)(SkeletonTarget ?? XmlElement)?.Attribute(Name);

    private void UpdateXmlAttribute(string Name, string? Value)
    {

        var Target = SkeletonTarget;
        if (Target is null) return;

        UpdateElementAttribute(Target, Target, Name, Value);
        foreach (var Descendant in Target.Descendants())
        {
            var Ln = Descendant.Name.LocalName;
            if (Ln is "path" or "rect" or "circle" or "ellipse" or "polygon" or "polyline" or "text" or "g")
            {
                UpdateElementAttribute(Target, Descendant, Name, Value);
            }
        }

        StyleDirty = true;
    }

    private static void UpdateElementAttribute(XElement Root, XElement El, string Name, string? Value)
    {

        if (El.Attribute(Name) is not null || (El == Root && Value is not null))
        {
            if (Value is null)
            {
                El.Attribute(Name)?.Remove();
            }
            else
            {
                El.SetAttributeValue(Name, Value);
            }
        }

        var Style = (string?)El.Attribute("style");
        if (Style is not null)
        {
            var Parts = Style.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var UpdatedParts = new List<string>();
            bool Found = false;

            foreach (var Part in Parts)
            {
                var Kv = Part.Split(new[] { ':' }, 2);
                if (Kv.Length == 2 && Kv[0].Trim().Equals(Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (Value is not null)
                    {

                        var OrigUnit = "";
                        var OrigValue = Kv[1].Trim();
                        if (OrigValue.EndsWith("px", StringComparison.OrdinalIgnoreCase)) OrigUnit = "px";
                        else if (OrigValue.EndsWith("em", StringComparison.OrdinalIgnoreCase)) OrigUnit = "em";
                        else if (OrigValue.EndsWith("%")) OrigUnit = "%";

                        UpdatedParts.Add($"{Kv[0].Trim()}:{Value}{OrigUnit}");
                    }
                    Found = true;
                }
                else
                {
                    UpdatedParts.Add(Part.Trim());
                }
            }

            if (!Found && Value is not null && El == Root)
            {
                UpdatedParts.Add($"{Name}:{Value}");
            }

            if (UpdatedParts.Count > 0)
            {
                El.SetAttributeValue("style", string.Join(";", UpdatedParts) + ";");
            }
            else
            {
                El.Attribute("style")?.Remove();
            }
        }
        else if (El == Root && Value is not null)
        {
            El.SetAttributeValue(Name, Value);
        }
    }
}

public struct Thickness
{
    public float Left { get; set; }
    public float Top { get; set; }
    public float Right { get; set; }
    public float Bottom { get; set; }

    public Thickness(float uniform)
    {
        Left = Top = Right = Bottom = uniform;
    }

    public Thickness(float horizontal, float vertical)
    {
        Left = Right = horizontal;
        Top = Bottom = vertical;
    }

    public Thickness(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}
