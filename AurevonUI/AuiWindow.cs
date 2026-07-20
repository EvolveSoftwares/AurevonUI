#pragma warning disable CS0618
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AurevonUI.Svg;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Svg.Skia;

namespace AurevonUI;

public enum HAlign { Left, Center, Right, Stretch }
public enum VAlign { Top, Center, Bottom, Stretch }

public enum AuiStretch
{

    None,

    Uniform,

    Fill,
}

public abstract class AuiWindow : AurevonApp
{
    private static readonly HashSet<string> NonVisual = new()
    {
        "defs", "clipPath", "mask", "filter", "linearGradient", "radialGradient",
        "pattern", "symbol", "style", "metadata", "title", "desc",
    };

    private static readonly HashSet<string> VisualTags = new()
    {
        "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "use", "image", "text", "svg", "a",
    };

    private static readonly HashSet<string> AuiIgnoreTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "defs", "style", "resources", "window", "aui", "aurevonui"
    };

    private static string? LogicalAuiId(XElement El)
    {
        var Id = (string?)El.Attribute("id") ?? (string?)El.Attribute("Id") ?? (string?)El.Attribute("Name");
        if (Id is not null)
            return Id;
        var Tag = El.Name.LocalName;
        if (AuiIgnoreTags.Contains(Tag) || Tag.Equals("Control", StringComparison.OrdinalIgnoreCase))
            return null;
        return Tag;
    }

    private readonly Dictionary<string, Control> _controls_dict = new();
    private readonly List<Control> _control_list = new();
    private readonly List<SKSvg> _svg_keep_alive = new();

    private readonly Dictionary<string, XElement> _templates = new();
    private XElement _root = null!;
    private Dictionary<string, SKRect> _svg_bounds = new();
    private HashSet<string> _control_ids = new();
    private int _gen_seq;

    private static bool IsTemplateId(string? id) =>
        id is not null && id.IndexOf("template", StringComparison.OrdinalIgnoreCase) >= 0;

    private sealed class InputInfo
    {
        public float X, Y, Size = 16f;
        public string Family = "Segoe UI";
        public bool Bold;
        public Color Fill = Color.White;
        public string InitialText = "";
        public SKTextAlign Alignment = SKTextAlign.Left;
    }

    private readonly Dictionary<string, InputInfo> _input_info = new();
    private Elements.TextBox? _focused;
    private readonly Dictionary<string, XElement> _aui_elements_dict = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _text_module_ids = new();

    private SKPicture? _base_picture;
    private float _view_min_x, _view_min_y, _view_w = 100, _view_h = 100;

    public HAlign HorizontalAlignment { get; set; } = HAlign.Center;

    public VAlign VerticalAlignment { get; set; } = VAlign.Center;

    public AuiStretch Stretch { get; set; } = AuiStretch.Uniform;

    private Vector2 _mouse_pos;
    private Control? _pressed;
    private volatile Control? _hovered;

    private readonly SKPaint _draw_paint = new();

    private readonly string _aui_name;
    private string? _svg_source_path;
    private string? _aui_source_path;
    private readonly List<ItemsControl> _items_controls = new();

#if DEBUG

    private FileSystemWatcher? _hot_watcher;
    private readonly object _hot_lock = new();
    private bool _hot_pending;
    private bool _hot_aui_changed;
    private int _hot_retries;
    private long _hot_request_ticks;
#endif

    protected AuiWindow(string AuiName)
    {
        _aui_name = AuiName;
        LoadUi(ApplyAuiProps: true);
#if DEBUG
        StartHotReloadWatcher();
#endif
    }

    protected virtual void OnHotReload() { }

    private void LoadUi(bool ApplyAuiProps)
    {
        string AuiName = _aui_name;
        string? AuiXmlText = null;
        string SvgXmlText = string.Empty;

        if (AuiName.EndsWith(".aui", StringComparison.OrdinalIgnoreCase))
        {
            AuiXmlText = LoadUiText(AuiName, out _aui_source_path);
            var AuiDocTemp = ParseXmlLenient(AuiXmlText);
            var SvgAttr = (string?)AuiDocTemp.Root?.Attribute("Svg") ?? (string?)AuiDocTemp.Root?.Attribute("svg");
            string SvgName = SvgAttr ?? Path.ChangeExtension(AuiName, ".svg");
            try
            {
                SvgXmlText = LoadUiText(SvgName, out _svg_source_path);
            }
            catch (Exception Ex)
            {
                throw new FileNotFoundException($"Failed to load the associated SVG file {SvgName} for {AuiName}.", Ex);
            }
        }
        else if (AuiName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            SvgXmlText = LoadUiText(AuiName, out _svg_source_path);
            string PossibleAuiName = Path.ChangeExtension(AuiName, ".aui");
            try
            {
                AuiXmlText = LoadUiText(PossibleAuiName, out _aui_source_path);
            }
            catch
            {

            }
        }
        else
        {
            try
            {
                AuiXmlText = LoadUiText(AuiName + ".aui", out _aui_source_path);
                SvgXmlText = LoadUiText(AuiName + ".svg", out _svg_source_path);
            }
            catch
            {
                SvgXmlText = LoadUiText(AuiName, out _svg_source_path);
            }
        }

#if DEBUG
        if (AuiXmlText is not null)
        {
            SyncAuiWithSvg(SvgXmlText, AuiXmlText);

            if (_aui_source_path is not null && File.Exists(_aui_source_path))
            {
                try { AuiXmlText = ReadFileShared(_aui_source_path); } catch { }
            }
        }
#endif

        var doc = ParseXmlLenient(SvgXmlText);
        var root = doc.Root ?? throw new InvalidDataException($"{AuiName}: empty SVG document");

        var Recycled = new Dictionary<string, Control>(_controls_dict);
        foreach (var Old in Recycled.Values)
        {
            Old.Children.Clear();
            Old.Parent = null;
            if (Old is Elements.Image OldImg) { OldImg.Source?.Dispose(); OldImg.Source = null; }
        }
        foreach (var KeepAlive in _svg_keep_alive)
            KeepAlive.Dispose();
        _svg_keep_alive.Clear();
        _controls_dict.Clear();
        _control_list.Clear();
        _templates.Clear();
        _input_info.Clear();
        _text_module_ids.Clear();
        _aui_elements_dict.Clear();
        _hovered = null;
        _pressed = null;
        _root = root;

        XElement? AuiRoot = null;
        if (AuiXmlText is not null)
        {
            var AuiDoc = ParseXmlLenient(AuiXmlText);
            AuiRoot = AuiDoc.Root;
            if (AuiRoot is not null)
            {
                foreach (var El in AuiRoot.Descendants())
                {

                    var IdAttr = LogicalAuiId(El);
                    if (IdAttr is not null)
                    {
                        _aui_elements_dict[IdAttr] = El;
                    }
                }
            }
        }

        ParseViewBox(root);
        ParseRootParams(root, AuiRoot);

        foreach (var el in root.Descendants())
        {
            var tid = (string?)el.Attribute("id");
            if (IsTemplateId(tid)) _templates[tid!] = el;
        }

        var EarlyBounds = SvgBounds.Compute(root);
        foreach (var Txt in root.Descendants().Where(t => t.Name.LocalName == "text").ToList())
        {
            float Tx = ParseLen(Attr(Txt, "x"), float.NaN);
            float Ty = ParseLen(Attr(Txt, "y"), float.NaN);
            if (float.IsNaN(Tx) || float.IsNaN(Ty)) continue;

            string? Owner = null;
            float Best = float.MaxValue;
            foreach (var kv in EarlyBounds)
            {
                var B = kv.Value;
                if (Tx >= B.Left - 1f && Tx <= B.Right + 1f && Ty >= B.Top - 1f && Ty <= B.Bottom + 1f)
                {
                    float A = B.Width * B.Height;
                    if (A < Best) { Best = A; Owner = kv.Key; }
                }
            }
            if (Owner is not null && !_input_info.ContainsKey(Owner))
                _input_info[Owner] = ExtractInputInfo(Txt);
        }

        foreach (var el in root.Descendants())
        {
            var idv = (string?)el.Attribute("id");
            if (idv is null || !VisualTags.Contains(el.Name.LocalName)) continue;
            _aui_elements_dict.TryGetValue(idv, out var AuiEl0);
            string? TypeAttr0 = AuiEl0 is not null ? (Attr(AuiEl0, "Type") ?? Attr(AuiEl0, "type")) : null;
            string Tn = Generator.AuiSyncCore.ResolveControlTypeName(TypeAttr0, el.Name.LocalName);
            if (Tn is "TextControl" or "TextBox")
                _text_module_ids.Add(idv);
        }

        ConvertTextToPaths(root, _text_module_ids);

        var ExistingIds = new HashSet<string>();
        foreach (var el in root.Descendants())
        {
            var idv = (string?)el.Attribute("id");
            if (idv is not null) ExistingIds.Add(idv);
        }
        int AutoIdSeq = 0;
        string NextAutoId()
        {
            string Gen;
            do { Gen = $"__auto{AutoIdSeq++}"; } while (ExistingIds.Contains(Gen));
            ExistingIds.Add(Gen);
            return Gen;
        }

        foreach (var el in root.Elements())
        {
            if (!VisualTags.Contains(el.Name.LocalName)) continue;
            if ((string?)el.Attribute("id") is not null) continue;
            el.SetAttributeValue("id", NextAutoId());
        }

        var ClipTarget = new Dictionary<string, string>();
        foreach (var cp in root.Descendants().Where(e => e.Name.LocalName == "clipPath"))
        {
            var cid = (string?)cp.Attribute("id");
            if (cid is null) continue;
            var inner = cp.Descendants().FirstOrDefault(e => VisualTags.Contains(e.Name.LocalName));
            var logical = inner is null ? null : SerifOrId(inner);
            if (logical is not null) ClipTarget[cid] = logical;
        }

        var ForcedParentId = new Dictionary<XElement, string>();
        foreach (var el in root.Descendants())
        {
            if (!VisualTags.Contains(el.Name.LocalName)) continue;
            if (el.Ancestors().Any(a => NonVisual.Contains(a.Name.LocalName))) continue;
            var Clip = ClipRef(el);
            if (Clip is null || !ClipTarget.TryGetValue(Clip, out var TargetId)) continue;
            if (SerifOrId(el) == TargetId) continue;

            if ((string?)el.Attribute("id") is null)
                el.SetAttributeValue("id", NextAutoId());
            ForcedParentId[el] = TargetId;
        }

        var controlElements = new List<XElement>();
        foreach (var el in root.Descendants())
        {
            var id = (string?)el.Attribute("id");
            if (id is null || _controls_dict.ContainsKey(id))
                continue;
            if (!VisualTags.Contains(el.Name.LocalName))
                continue;
            if (el.Ancestors().Any(a => NonVisual.Contains(a.Name.LocalName)))
                continue;

            if (el.AncestorsAndSelf().Any(a => IsTemplateId((string?)a.Attribute("id"))))
                continue;

            _aui_elements_dict.TryGetValue(id, out var AuiEl);
            Control? ParentControl = null;

            if (AuiEl is not null)
            {
                var ParentAuiEl = AuiEl.Parent;
                while (ParentAuiEl is not null)
                {
                    var Paid = LogicalAuiId(ParentAuiEl);
                    if (Paid is not null && _controls_dict.TryGetValue(Paid, out ParentControl))
                    {
                        break;
                    }
                    ParentAuiEl = ParentAuiEl.Parent;
                }
            }

            if (ParentControl is null)
            {
                if (ForcedParentId.TryGetValue(el, out var ForcedId))
                    _controls_dict.TryGetValue(ForcedId, out ParentControl);
            }

            if (ParentControl is null)
            {
                var ParentEl = el.Parent;
                while (ParentEl is not null && ParentEl != root)
                {
                    var Pid = (string?)ParentEl.Attribute("id");
                    if (Pid is not null && _controls_dict.TryGetValue(Pid, out ParentControl))
                    {
                        break;
                    }
                    ParentEl = ParentEl.Parent;
                }
            }

            string TypeAttr0 = (AuiEl is not null ? (Attr(AuiEl, "Type") ?? Attr(AuiEl, "type")) : null) ?? "";
            string TypeName = Generator.AuiSyncCore.ResolveControlTypeName(
                TypeAttr0.Length > 0 ? TypeAttr0 : null, el.Name.LocalName);

            bool IsNew = !Recycled.TryGetValue(id, out var ctl) || ctl!.GetType().Name != TypeName;
            if (IsNew)
                ctl = CreateControlInstance(TypeName);
            ctl!.Id = id;
            ctl.DocOrder = controlElements.Count;
            ctl.XmlElement = el;
            ctl.AuiElement = AuiEl;
            if (ParentControl is not null)
            {
                ctl.Parent = ParentControl;
                ParentControl.Children.Add(ctl);
            }

            XElement TargetEl = AuiEl ?? el;

            if (IsNew || ApplyAuiProps)
            {
                ctl.HorizontalAlignment = ParseH(Attr(TargetEl, "halign") ?? Attr(TargetEl, "HorizontalAlignment"));
                ctl.VerticalAlignment = ParseV(Attr(TargetEl, "valign") ?? Attr(TargetEl, "VerticalAlignment"));
                ctl.OffsetX = ParseLen(Attr(TargetEl, "offsetx") ?? Attr(TargetEl, "OffsetX"), 0f);
                ctl.OffsetY = ParseLen(Attr(TargetEl, "offsety") ?? Attr(TargetEl, "OffsetY"), 0f);

                var MarginVal = Attr(TargetEl, "margin") ?? Attr(TargetEl, "Margin");
                ctl.Margin = ParseThickness(MarginVal);
                ctl.HasMargin = MarginVal is not null;
                ctl.Cursor = ParseCursor(Attr(TargetEl, "cursor") ?? Attr(TargetEl, "Cursor"));
                ctl.Opacity = ParseFloat(Attr(TargetEl, "opacity") ?? Attr(TargetEl, "Opacity"), 1f);
                ctl.Scale = ParseFloat(Attr(TargetEl, "scale") ?? Attr(TargetEl, "Scale"), 1f);
                ctl.StretchToWindow = ParseBool(Attr(TargetEl, "stretchtowindow") ?? Attr(TargetEl, "StretchToWindow"), false);
                ctl.IsHittable = ParseBool(Attr(TargetEl, "ishittable") ?? Attr(TargetEl, "IsHittable"), true);
                ctl.IsEnabled = ParseBool(Attr(TargetEl, "isenabled") ?? Attr(TargetEl, "IsEnabled"), true);
                ctl.Visible = ParseBool(Attr(TargetEl, "visible") ?? Attr(TargetEl, "Visible"), true);

                var FillVal = Attr(TargetEl, "fill") ?? Attr(TargetEl, "Fill");
                if (FillVal is not null) ctl.Fill = FillVal;

                var StrokeVal = Attr(TargetEl, "stroke") ?? Attr(TargetEl, "Stroke");
                if (StrokeVal is not null) ctl.Stroke = StrokeVal;

                var StrokeWidthVal = Attr(TargetEl, "stroke-width") ?? Attr(TargetEl, "StrokeWidth");
                if (StrokeWidthVal is not null) ctl.StrokeWidth = ParseFloatNullable(StrokeWidthVal);

                var MarginPercentVal = Attr(TargetEl, "marginpercent") ?? Attr(TargetEl, "MarginPercent");
                if (MarginPercentVal is not null)
                {
                    ctl.MarginPercent = ParseThickness(MarginPercentVal);
                    ctl.HasMarginPercent = true;
                }

                if (ctl is Elements.TextControl TextCtl)
                {
                    var PlaceholderVal = Attr(TargetEl, "placeholder") ?? Attr(TargetEl, "Placeholder");
                    if (PlaceholderVal is not null) TextCtl.Placeholder = PlaceholderVal;

                    var TextVal = Attr(TargetEl, "text") ?? Attr(TargetEl, "Text");
                    if (TextVal is not null) TextCtl.Text = TextVal;
                }

                if (ctl is Elements.ScrollViewer Sv)
                {
                    var ScrollPadTopVal = Attr(TargetEl, "scrollpaddingtop") ?? Attr(TargetEl, "ScrollPaddingTop");
                    if (ScrollPadTopVal is not null) Sv.ScrollPaddingTop = ParseLen(ScrollPadTopVal, 0f);

                    var ScrollPadBottomVal = Attr(TargetEl, "scrollpaddingbottom") ?? Attr(TargetEl, "ScrollPaddingBottom");
                    if (ScrollPadBottomVal is not null) Sv.ScrollPaddingBottom = ParseLen(ScrollPadBottomVal, 0f);

                    var ScrollbarVisibleVal = Attr(TargetEl, "scrollbarvisible") ?? Attr(TargetEl, "ScrollbarVisible");
                    if (ScrollbarVisibleVal is not null) Sv.ScrollbarVisible = ParseBool(ScrollbarVisibleVal, true);

                    var ScrollbarWidthVal = Attr(TargetEl, "scrollbarwidth") ?? Attr(TargetEl, "ScrollbarWidth");
                    if (ScrollbarWidthVal is not null) Sv.ScrollbarWidth = ParseLen(ScrollbarWidthVal, 6f);

                    var ScrollbarPaddingVal = Attr(TargetEl, "scrollbarpadding") ?? Attr(TargetEl, "ScrollbarPadding");
                    if (ScrollbarPaddingVal is not null) Sv.ScrollbarPadding = ParseLen(ScrollbarPaddingVal, 4f);

                    var ScrollbarColorVal = Attr(TargetEl, "scrollbarcolor") ?? Attr(TargetEl, "ScrollbarColor");
                    if (ScrollbarColorVal is not null && Color.TryParse(ScrollbarColorVal, out var SbColor))
                        Sv.ScrollbarColor = SbColor;

                    var ScrollbarTrackColorVal = Attr(TargetEl, "scrollbartrackcolor") ?? Attr(TargetEl, "ScrollbarTrackColor");
                    if (ScrollbarTrackColorVal is not null && Color.TryParse(ScrollbarTrackColorVal, out var SbTrackColor))
                        Sv.ScrollbarTrackColor = SbTrackColor;
                }
            }

            if (el.Name.LocalName == "image" && ctl is Elements.Image ImgCtl)
            {
                var Href = (string?)el.Attribute("{http://www.w3.org/1999/xlink}href")
                           ?? (string?)el.Attribute("href");
                if (Href is not null && Href.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var CommaIdx = Href.IndexOf(',');
                        if (CommaIdx >= 0)
                        {
                            var Base64 = Href[(CommaIdx + 1)..];
                            var Sb = new StringBuilder(Base64.Length);
                            foreach (char Ch in Base64)
                            {
                                if (!char.IsWhiteSpace(Ch))
                                    Sb.Append(Ch);
                            }
                            var CleanedBase64 = Sb.ToString();
                            var Bytes = Convert.FromBase64String(CleanedBase64);
                            ImgCtl.Source = Bitmap.Decode(Bytes);
                        }
                    }
                    catch (Exception Ex)
                    {
                        Console.WriteLine($"[DEBUG] Svg image load failed for {id}: {Ex.Message}");
                    }
                }
            }

            _controls_dict[id] = ctl;
            _control_list.Add(ctl);
            controlElements.Add(el);
            if (IsNew)
                BindEvents(ctl, TargetEl);
        }

        var Bounds = SvgBounds.Compute(root);

        foreach (var id in _text_module_ids)
        {
            if (EarlyBounds.TryGetValue(id, out var originalRect))
            {
                Bounds[id] = originalRect;
            }
        }
        _svg_bounds = Bounds;
        foreach (var Ctl in _control_list)
        {
            if (Bounds.TryGetValue(Ctl.Id, out var R))
            {
                Ctl.BoundsSvg = R;
                Ctl.FrameBoundsSvg = R;
            }
        }

        var baseDoc = new XDocument(doc);
        foreach (var el in controlElements)
            FindById(baseDoc.Root!, el)?.Remove();
        foreach (var tpl in _templates.Values)
            FindById(baseDoc.Root!, tpl)?.Remove();
        _base_picture = LoadPicture(baseDoc);

        var controlIds = new HashSet<string>(_controls_dict.Keys);
        _control_ids = controlIds;
        for (int i = 0; i < controlElements.Count; i++)
        {
            var element = controlElements[i];
            var control = _control_list[i];

            var skeleton = BuildSkeleton(root, element, controlIds, control is Elements.TextControl);
            control.Skeleton = skeleton;
            control.SkeletonTarget = FindById(skeleton.Root!, element);

            var FrameBounds = SvgBounds.Compute(skeleton.Root!);
            control.FrameBoundsSvg = FrameBounds.TryGetValue(control.Id, out var Fb) ? Fb : control.BoundsSvg;
            control.CornerInset = control.SkeletonTarget is null
                ? 0f
                : DetectCornerInset(control.SkeletonTarget, control.FrameBoundsSvg);

            control.ReapplyStyleOverrides();

            var svg = LoadSvg(skeleton);
            control.SvgOwner?.Dispose();
            control.SvgOwner = svg;
            control.Picture = svg?.Picture;
            control.StyleDirty = false;
        }

        foreach (var kv in _input_info)
        {
            if (!_controls_dict.TryGetValue(kv.Key, out var c) || c is not Elements.TextControl Tc) continue;
            var Ii = kv.Value;
            Tc.TextSvgX = Ii.X;
            Tc.TextSvgY = Ii.Y;
            Tc.TextSvgSize = Ii.Size;
            Tc.TextFontFamily = Ii.Family;
            Tc.TextBold = Ii.Bold;
            Tc.TextColor = Ii.Fill;
            Tc.InitialText = Ii.InitialText;
            Tc.TextAlignment = Ii.Alignment;
            Tc.HasCapturedText = true;
        }

        foreach (var Kvp in Recycled)
        {
            if (_controls_dict.ContainsKey(Kvp.Key))
                continue;
            Kvp.Value.SvgOwner?.Dispose();
            Kvp.Value.SvgOwner = null;
            Kvp.Value.Picture = null;
        }

        if (_focused is not null &&
            (!_controls_dict.TryGetValue(_focused.Id, out var StillFocused) || !ReferenceEquals(StillFocused, _focused)))
        {
            _focused.IsFocused = false;
            _focused = null;
        }

        foreach (var Ic in _items_controls)
        {
            if (_templates.ContainsKey(Ic.TemplateId) && _controls_dict.ContainsKey(Ic.Container.Id))
                GenerateItems(Ic, Ic.ItemsSource);
        }


        var auiOrder = new Dictionary<XElement, int>();
        int orderSeq = 0;
        void WalkAui(XElement parent)
        {
            auiOrder[parent] = orderSeq++;
            foreach (var child in parent.Elements())
            {
                WalkAui(child);
            }
        }
        if (AuiRoot is not null)
        {
            WalkAui(AuiRoot);
        }

        var comparer = new Comparison<Control>((a, b) =>
        {
            int orderA = a.AuiElement is not null && auiOrder.TryGetValue(a.AuiElement, out int idxA) ? idxA : -1;
            int orderB = b.AuiElement is not null && auiOrder.TryGetValue(b.AuiElement, out int idxB) ? idxB : -1;
            if (orderA != orderB)
                return orderA.CompareTo(orderB);
            return a.DocOrder.CompareTo(b.DocOrder);
        });

        _control_list.Sort(comparer);
        foreach (var c in _control_list)
        {
            if (c.Children.Count > 1)
            {
                c.Children.Sort(comparer);
            }
        }
    }

    private void ActivateInputs()
    {
        foreach (var C in _control_list)
        {
            if (C is not Elements.TextControl Tc || Tc.Activated)
                continue;
            Tc.Activated = true;
            if (Tc is Elements.TextBox)
                Tc.Cursor = Cursor.Text;

            if (Tc.HasCapturedText)
            {

                if (Tc.Text.Length == 0)
                    Tc.Text = Tc.InitialText;
            }
            else if (Tc is Elements.TextBox)
            {

                var B = Tc.BoundsSvg;
                Tc.TextSvgX = B.Left + B.Width * 0.06f;
                Tc.TextSvgY = B.Top + B.Height * 0.68f;
                Tc.TextSvgSize = B.Height * 0.5f;
            }
        }
    }

    private void FlushDirtyStyles()
    {
        foreach (var C in _control_list)
        {
            if (C.StyleDirty)
            {
                RebuildControlPicture(C);
                C.StyleDirty = false;
            }
        }
    }

    private void UpdateItemsLayout()
    {
        foreach (var Ic in _items_controls)
        {
            if (Ic._generated.Count == 0)
                continue;
            _svg_bounds.TryGetValue(Ic.TemplateId, out var TplBounds);
            float CS = Ic.Container.ContentScale <= 0f ? 1f : Ic.Container.ContentScale;
            float Step = (Ic.Orientation == Orientation.Vertical ? TplBounds.Height : TplBounds.Width) * CS + Ic.Spacing;
            for (int I = 0; I < Ic._generated.Count; I++)
            {
                var Ctl = Ic._generated[I];
                if (Ic.Orientation == Orientation.Vertical)
                    Ctl.OffsetY = I * Step;
                else
                    Ctl.OffsetX = I * Step;
            }
        }
    }

    private static XDocument ParseXmlLenient(string Xml)
    {
        var Settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = true,
        };
        using var Reader = XmlReader.Create(new StringReader(Xml), Settings);
        return XDocument.Load(Reader);
    }

    private static string LoadUiText(string Name, out string? SourcePath)
    {
        SourcePath = null;
#if DEBUG
        var ProjectPath = FindProjectFile(Name);
        if (ProjectPath is not null && File.Exists(ProjectPath))
        {
            SourcePath = ProjectPath;
            return ReadFileShared(ProjectPath);
        }
#endif
        return LoadAuiText(Name);
    }

    private static string? FindProjectFile(string Name)
    {
        try
        {
            var Dir = AppContext.BaseDirectory;
            while (Dir is not null)
            {
                if (Directory.GetFiles(Dir, "*.csproj").Length > 0)
                    return Path.Combine(Dir, Name);
                Dir = Path.GetDirectoryName(Dir);
            }
        }
        catch { }
        return null;
    }

    private static string ReadFileShared(string FilePath)
    {
        using var Fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var Sr = new StreamReader(Fs);
        return Sr.ReadToEnd();
    }

    private static string LoadAuiText(string auiName)
    {

        var asm = Assembly.GetEntryAssembly();
        if (asm is not null)
        {
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (res.EndsWith(auiName, StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = asm.GetManifestResourceStream(res)!;
                    using var sr = new StreamReader(stream);
                    return sr.ReadToEnd();
                }
            }
        }

        string path = Path.IsPathRooted(auiName)
            ? auiName
            : Path.Combine(AppContext.BaseDirectory, auiName);
        if (File.Exists(path))
            return File.ReadAllText(path);
        if (File.Exists(auiName))
            return File.ReadAllText(auiName);

        throw new FileNotFoundException(
            $"AUI '{auiName}' not found – add it to the project as <EmbeddedResource Include=\"{auiName}\" />.");
    }

    private SKPicture? LoadPicture(XDocument doc)
    {
        var svg = LoadSvg(doc);
        if (svg is null)
            return null;
        _svg_keep_alive.Add(svg);
        return svg.Picture;
    }

    private static SKSvg? LoadSvg(XDocument doc)
    {
        var svg = new SKSvg();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting)));
        svg.Load(ms);
        return svg.Picture is null ? null : svg;
    }

    private static void RebuildControlPicture(Control c)
    {
        if (c.Skeleton is null)
            return;
        var svg = LoadSvg(c.Skeleton);
        if (svg is null)
            return;
        c.SvgOwner?.Dispose();
        c.SvgOwner = svg;
        c.Picture = svg.Picture;
    }

    private static XElement? FindById(XElement root, XElement original)
    {
        var id = (string?)original.Attribute("id");
        return id is null ? null : root.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == id);
    }

    private static XDocument BuildSkeleton(XElement root, XElement target, HashSet<string> controlIds, bool isInput)
    {
        var newRoot = new XElement(root.Name, root.Attributes());

        foreach (var d in root.Descendants())
        {
            var n = d.Name.LocalName;
            if (n is "defs" or "clipPath" or "linearGradient" or "radialGradient" or "pattern" or "mask" or "filter")
            {
                if (!d.Ancestors().Any(a => a == target) && d != target)
                {
                    if (n == "defs")
                    {
                        var CleanDefs = new XElement(d);
                        var Images = new List<XElement>();
                        foreach (var Img in CleanDefs.Descendants())
                        {
                            if (Img.Name.LocalName == "image")
                                Images.Add(Img);
                        }
                        foreach (var Img in Images)
                        {
                            Img.Remove();
                        }
                        newRoot.Add(CleanDefs);
                    }
                    else if (n != "image")
                    {
                        newRoot.Add(new XElement(d));
                    }
                }
            }
        }

        var chain = target.Ancestors().TakeWhile(a => a != root).Reverse().ToList();
        XElement parent = newRoot;
        foreach (var anc in chain)
        {
            var shell = new XElement(anc.Name, anc.Attributes());
            parent.Add(shell);
            parent = shell;
        }

        var copy = new XElement(target);
        var mine = (string?)target.Attribute("id");
        foreach (var nested in copy.Descendants().ToList())
        {
            var nid = (string?)nested.Attribute("id");
            if (nid is not null && nid != mine && (controlIds.Contains(nid) || IsTemplateId(nid)))
            {
                nested.Remove();
                continue;
            }
            if (isInput && (nested.Name.LocalName == "text" || nested.Attribute("AuiWasText") is not null))
            {
                nested.Remove();
            }
        }
        parent.Add(copy);

        return new XDocument(newRoot);
    }

    private float GetAccumulatedOpacity(Control C)
    {
        float Opacity = C.Opacity;
        var Parent = C.Parent;
        while (Parent is not null)
        {
            Opacity *= Parent.Opacity;
            Parent = Parent.Parent;
        }
        return Opacity;
    }

    private static SKRect EffBounds(Control C) => C is Elements.ScrollViewer ? C.FrameBoundsSvg : C.BoundsSvg;

    private static bool PxActive(bool Has, float V) => Has && V != -1f;

    private static bool PctActive(bool Has, float V) => Has && V != -1f;

    private static bool SideIgnored(bool HasPx, float Px, bool HasPct, float Pct)
        => !PxActive(HasPx, Px) && !PctActive(HasPct, Pct);

    private static float SideMargin(bool HasPx, float Px, bool HasPct, float Pct, float ContDim)
        => (PxActive(HasPx, Px) ? Px : 0f) + (PctActive(HasPct, Pct) ? Pct / 100f * ContDim : 0f);

    private void UpdateControlLayout(Control C, FrameContext Ctx, float Sx, float Sy, float Ox, float Oy, float ScrollY = 0f)
    {

        bool IsBackground = C.Parent is null &&
                            MathF.Abs(C.BoundsSvg.Width - _view_w) < 1f &&
                            MathF.Abs(C.BoundsSvg.Height - _view_h) < 1f;

        float ParentContentScale = C.Parent?.ContentScale ?? Sx;
        C.ContentScale = ParentContentScale * C.Scale;

        if (C.StretchToWindow || IsBackground)
        {
            C.ScreenX = 0;
            C.ScreenY = 0;
            C.ScreenWidth = Ctx.Width;
            C.ScreenHeight = Ctx.Height;
        }
        else
        {
            bool HasParent = C.Parent is not null;

            var Eb = EffBounds(C);
            float W = Eb.Width * C.ContentScale;
            float H = Eb.Height * C.ContentScale;
            C.ScreenWidth = W;
            C.ScreenHeight = H;

            float ContX = HasParent ? C.Parent!.ScreenX : 0f;
            float ContY = HasParent ? C.Parent!.ScreenY : 0f;
            float ContW = HasParent ? C.Parent!.ScreenWidth : Ctx.Width;
            float ContH = HasParent ? C.Parent!.ScreenHeight : Ctx.Height;

            bool Hm = C.HasMargin, Hp = C.HasMarginPercent;
            bool LeftIgn = SideIgnored(Hm, C.Margin.Left, Hp, C.MarginPercent.Left);
            bool RightIgn = SideIgnored(Hm, C.Margin.Right, Hp, C.MarginPercent.Right);
            bool TopIgn = SideIgnored(Hm, C.Margin.Top, Hp, C.MarginPercent.Top);
            bool BottomIgn = SideIgnored(Hm, C.Margin.Bottom, Hp, C.MarginPercent.Bottom);

            float MLeft = SideMargin(Hm, C.Margin.Left, Hp, C.MarginPercent.Left, ContW);
            float MRight = SideMargin(Hm, C.Margin.Right, Hp, C.MarginPercent.Right, ContW);
            float MTop = SideMargin(Hm, C.Margin.Top, Hp, C.MarginPercent.Top, ContH);
            float MBottom = SideMargin(Hm, C.Margin.Bottom, Hp, C.MarginPercent.Bottom, ContH);

            bool AnchorAttr = C.HasMargin || C.HasMarginPercent;
            bool HAnchored = AnchorAttr && !(LeftIgn && RightIgn);
            bool VAnchored = AnchorAttr && !(TopIgn && BottomIgn);

            if (C.HorizontalAlignment == HAlign.Stretch)
            {
                C.ScreenWidth = MathF.Max(0f, ContW - MLeft - MRight);
                C.ScreenX = ContX + MLeft + C.OffsetX;
            }
            else if (C.HorizontalAlignment is null && HAnchored)
            {
                if (!LeftIgn && !RightIgn)
                {
                    C.ScreenWidth = MathF.Max(0f, ContW - MLeft - MRight);
                    C.ScreenX = ContX + MLeft + C.OffsetX;
                }
                else if (!LeftIgn)
                {
                    C.ScreenX = ContX + MLeft + C.OffsetX;
                }
                else
                {
                    C.ScreenX = ContX + ContW - MRight - W + C.OffsetX;
                }
            }
            else if (C.HorizontalAlignment == HAlign.Left)
            {
                C.ScreenX = ContX + MLeft + C.OffsetX;
            }
            else if (C.HorizontalAlignment == HAlign.Center)
            {
                C.ScreenX = ContX + (ContW - W) / 2f + C.OffsetX;
            }
            else if (C.HorizontalAlignment == HAlign.Right)
            {
                C.ScreenX = ContX + ContW - W - MRight + C.OffsetX;
            }
            else if (HasParent)
            {

                float Cx = C.Parent!.ScreenX + (Eb.MidX - EffBounds(C.Parent).Left) * ParentContentScale + C.OffsetX;
                C.ScreenX = Cx - W / 2f;
            }
            else
            {

                float Cx = (C.BoundsSvg.MidX - _view_min_x) * Sx + Ox + C.OffsetX;
                C.ScreenX = Cx - W / 2f;
            }

            if (C.VerticalAlignment == VAlign.Stretch)
            {
                C.ScreenHeight = MathF.Max(0f, ContH - MTop - MBottom);
                C.ScreenY = ContY + MTop + C.OffsetY;
            }
            else if (C.VerticalAlignment is null && VAnchored)
            {
                if (!TopIgn && !BottomIgn)
                {
                    C.ScreenHeight = MathF.Max(0f, ContH - MTop - MBottom);
                    C.ScreenY = ContY + MTop + C.OffsetY;
                }
                else if (!TopIgn)
                {
                    C.ScreenY = ContY + MTop + C.OffsetY;
                }
                else
                {
                    C.ScreenY = ContY + ContH - MBottom - H + C.OffsetY;
                }
            }
            else if (C.VerticalAlignment == VAlign.Top)
            {
                C.ScreenY = ContY + MTop + C.OffsetY;
            }
            else if (C.VerticalAlignment == VAlign.Center)
            {
                C.ScreenY = ContY + (ContH - H) / 2f + C.OffsetY;
            }
            else if (C.VerticalAlignment == VAlign.Bottom)
            {
                C.ScreenY = ContY + ContH - H - MBottom + C.OffsetY;
            }
            else if (HasParent)
            {
                float Cy = C.Parent!.ScreenY + (Eb.MidY - EffBounds(C.Parent).Top) * ParentContentScale + C.OffsetY;
                C.ScreenY = Cy - H / 2f;
            }
            else
            {
                float Cy = (C.BoundsSvg.MidY - _view_min_y) * Sy + Oy + C.OffsetY;
                C.ScreenY = Cy - H / 2f;
            }
        }

        C.ScreenY -= ScrollY;

        var ScrollC = C as Elements.ScrollViewer;
        float ChildScroll = ScrollY + (ScrollC is not null ? ScrollC.ScrollOffsetY - ScrollC.ScrollPaddingTop : 0f);
        foreach (var Child in C.Children)
        {
            UpdateControlLayout(Child, Ctx, Sx, Sy, Ox, Oy, ChildScroll);
        }

        if (ScrollC is not null)
        {
            float MaxBottom = 0f;
            foreach (var Child in ScrollC.Children)
            {

                float Bottom = Child.ScreenY + ScrollC.ScrollOffsetY - ScrollC.ScreenY + Child.ScreenHeight;
                if (Bottom > MaxBottom) MaxBottom = Bottom;
            }
            ScrollC.ScrollMaxY = MathF.Max(0f, MaxBottom + ScrollC.ScrollPaddingBottom - ScrollC.ScreenHeight);
            if (ScrollC.ScrollOffsetY > ScrollC.ScrollMaxY) ScrollC.ScrollOffsetY = ScrollC.ScrollMaxY;
            if (ScrollC.ScrollOffsetY < 0f) ScrollC.ScrollOffsetY = 0f;
        }
    }

    private void ParseViewBox(XElement root)
    {
        var vb = (string?)root.Attribute("viewBox");
        if (vb is not null)
        {
            var Parts = vb.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (Parts.Length == 4 &&
                float.TryParse(Parts[0], System.Globalization.CultureInfo.InvariantCulture, out var MinX) &&
                float.TryParse(Parts[1], System.Globalization.CultureInfo.InvariantCulture, out var MinY) &&
                float.TryParse(Parts[2], System.Globalization.CultureInfo.InvariantCulture, out var Width) &&
                float.TryParse(Parts[3], System.Globalization.CultureInfo.InvariantCulture, out var Height))
            {
                _view_min_x = MinX;
                _view_min_y = MinY;
                _view_w = Width;
                _view_h = Height;
                return;
            }
        }
        _view_w = ParseLen((string?)root.Attribute("width"), 100);
        _view_h = ParseLen((string?)root.Attribute("height"), 100);
    }

    private void ParseRootParams(XElement Root, XElement? AuiRoot)
    {
        var TargetRoot = AuiRoot ?? Root;
        HorizontalAlignment = ParseH(Attr(TargetRoot, "halign") ?? Attr(TargetRoot, "HorizontalAlignment")) ?? HAlign.Center;
        VerticalAlignment = ParseV(Attr(TargetRoot, "valign") ?? Attr(TargetRoot, "VerticalAlignment")) ?? VAlign.Center;
        Stretch = (Attr(TargetRoot, "stretch") ?? Attr(TargetRoot, "Stretch"))?.ToLowerInvariant() switch
        {
            "none" => AuiStretch.None,
            "fill" => AuiStretch.Fill,
            _ => AuiStretch.Uniform,
        };

        ApplyWindowParams(TargetRoot);
    }

    private void ApplyWindowParams(XElement TargetRoot)
    {
        if ((Attr(TargetRoot, "Title") ?? Attr(TargetRoot, "title")) is { } TitleVal)
            Title = TitleVal;

        if ((Attr(TargetRoot, "Icon") ?? Attr(TargetRoot, "icon")) is { } IconVal)
            IconPath = IconVal;

        if ((Attr(TargetRoot, "Width") ?? Attr(TargetRoot, "width")) is { } WidthVal)
            Width = (int)ParseLen(WidthVal, Width);
        if ((Attr(TargetRoot, "Height") ?? Attr(TargetRoot, "height")) is { } HeightVal)
            Height = (int)ParseLen(HeightVal, Height);

        if ((Attr(TargetRoot, "WindowStyle") ?? Attr(TargetRoot, "windowstyle")) is { } WsVal
            && Enum.TryParse<WindowStyle>(WsVal, true, out var Ws))
            WindowStyle = Ws;

        if ((Attr(TargetRoot, "WindowStartupLocation") ?? Attr(TargetRoot, "windowstartuplocation")) is { } WslVal
            && Enum.TryParse<WindowStartupLocation>(WslVal, true, out var Wsl))
            WindowStartupLocation = Wsl;
    }

    private static void ConvertTextToPaths(XElement root, HashSet<string>? textModuleIds = null)
    {
        var Texts = root.Descendants().Where(e => e.Name.LocalName == "text").ToList();
        foreach (var T in Texts)
        {

            if (T.AncestorsAndSelf().Any(a => IsTemplateId((string?)a.Attribute("id"))))
                continue;

            if (textModuleIds is not null &&
                T.AncestorsAndSelf().Any(a => textModuleIds.Contains((string?)a.Attribute("id") ?? "")))
            {
                var id = (string?)T.Attribute("id");
                if (id is not null && textModuleIds.Contains(id))
                {

                    T.Value = "";
                    T.SetAttributeValue("AuiWasText", "true");
                }
                else
                {

                    T.Remove();
                }
                continue;
            }

            float BaseSize = ParseLen(Attr(T, "font-size"), 16f);
            if (BaseSize <= 0f) BaseSize = 16f;
            string BaseFamily = Attr(T, "font-family") ?? "Segoe UI";
            string BaseWeight = Attr(T, "font-weight") ?? "";
            string BaseStyle = Attr(T, "font-style") ?? "";
            string BaseAnchor = (Attr(T, "text-anchor") ?? "start").Trim();
            string BaseFill = Attr(T, "fill") ?? "#000";
            string? BaseFillOpacity = Attr(T, "fill-opacity");

            float PenX = ParseLen(Attr(T, "x"), 0f);
            float PenY = ParseLen(Attr(T, "y"), 0f);

            var Buckets = new List<(string Fill, string? FillOpacity, SKPath Path)>();

            void AddRun(string Content, XElement? Span)
            {
                if (Content.Length == 0)
                    return;

                float Size = Span is null ? BaseSize : ParseLen(Attr(Span, "font-size"), BaseSize);
                if (Size <= 0f) Size = BaseSize;
                string Family = (Span is null ? null : Attr(Span, "font-family")) ?? BaseFamily;
                string Weight = (Span is null ? null : Attr(Span, "font-weight")) ?? BaseWeight;
                string StyleAttr = (Span is null ? null : Attr(Span, "font-style")) ?? BaseStyle;
                string Anchor = ((Span is null ? null : Attr(Span, "text-anchor")) ?? BaseAnchor).Trim();
                string Fill = (Span is null ? null : Attr(Span, "fill")) ?? BaseFill;
                string? FillOpacity = (Span is null ? null : Attr(Span, "fill-opacity")) ?? BaseFillOpacity;

                bool Bold = Weight.Contains("bold", StringComparison.OrdinalIgnoreCase)
                            || (int.TryParse(Weight, out var W) && W >= 600);
                bool Italic = StyleAttr.Contains("italic", StringComparison.OrdinalIgnoreCase);

                float Rx = PenX, Ry = PenY;
                if (Span is not null)
                {
                    var Xa = Attr(Span, "x");
                    var Ya = Attr(Span, "y");
                    if (Xa is not null) Rx = ParseLen(Xa, PenX);
                    if (Ya is not null) Ry = ParseLen(Ya, PenY);
                }

                var Align = Anchor switch
                {
                    "middle" => SKTextAlign.Center,
                    "end" => SKTextAlign.Right,
                    _ => SKTextAlign.Left,
                };

                var Tf = FrameContext.ResolveFont(Family, Bold, Italic);
                var RunPath = BuildShapedTextPath(Content, Tf, Size, Rx, Ry, Align);

                using (var Font = new SKFont(Tf, Size))
                {
                    float Adv = Font.MeasureText(Content);
                    PenX = Align switch
                    {
                        SKTextAlign.Center => Rx + Adv / 2f,
                        SKTextAlign.Right => Rx,
                        _ => Rx + Adv,
                    };
                }
                PenY = Ry;

                if (RunPath.IsEmpty)
                {
                    RunPath.Dispose();
                    return;
                }

                for (int I = 0; I < Buckets.Count; I++)
                {
                    if (Buckets[I].Fill == Fill && Buckets[I].FillOpacity == FillOpacity)
                    {
                        Buckets[I].Path.AddPath(RunPath);
                        RunPath.Dispose();
                        return;
                    }
                }
                Buckets.Add((Fill, FillOpacity, RunPath));
            }

            foreach (var Node in T.Nodes().ToList())
            {
                if (Node is XText Txt)
                    AddRun(Txt.Value, null);
                else if (Node is XElement El && El.Name.LocalName == "tspan")
                    AddRun(El.Value, El);

            }

            var PathEls = new List<XElement>();
            foreach (var B in Buckets)
            {
                string D = B.Path.ToSvgPathData();
                B.Path.Dispose();
                if (string.IsNullOrEmpty(D))
                    continue;
                var P = new XElement(root.Name.Namespace + "path", new XAttribute("d", D));
                string Style = $"fill:{B.Fill};fill-rule:nonzero;";
                if (B.FillOpacity is not null) Style += $"fill-opacity:{B.FillOpacity};";
                P.SetAttributeValue("style", Style);
                PathEls.Add(P);
            }

            if (PathEls.Count == 0)
            {
                var text_val = T.Value;
                Console.WriteLine($"[AUI] Text '{text_val}' removed because PathEls is empty.");
                T.Remove();
                continue;
            }
            else
            {
                var text_val = T.Value;
                var path_len = PathEls[0].Attribute("d")?.Value.Length ?? 0;
                Console.WriteLine($"[AUI] Replaced text '{text_val}' with path of length {path_len}");
            }

            XElement Replacement;
            if (PathEls.Count == 1)
            {
                Replacement = PathEls[0];
            }
            else
            {
                Replacement = new XElement(root.Name.Namespace + "g");
                foreach (var P in PathEls)
                    Replacement.Add(P);
            }
            if (Attr(T, "id") is { } Id) Replacement.SetAttributeValue("id", Id);
            if (T.Attribute("transform") is { } Tr) Replacement.SetAttributeValue("transform", Tr.Value);
            Replacement.SetAttributeValue("AuiWasText", "true");

            T.ReplaceWith(Replacement);
        }
    }

    private static SKPath BuildShapedTextPath(string Text, SKTypeface Tf, float Size, float X, float Y, SKTextAlign Align)
    {
        var Result = new SKPath();
        using var Font = new SKFont(Tf, Size);
        using var Shaper = new SKShaper(Tf);
        var Shaped = Shaper.Shape(Text, Font);

        float Ox = X;
        if (Align != SKTextAlign.Left)
        {
            float Width = Font.MeasureText(Text);
            Ox = Align == SKTextAlign.Center ? X - Width / 2f : X - Width;
        }

        var Points = Shaped.Points;
        var Glyphs = Shaped.Codepoints;
        for (int I = 0; I < Glyphs.Length; I++)
        {
            using var Gp = Font.GetGlyphPath((ushort)Glyphs[I]);
            if (Gp is null || Gp.IsEmpty)
                continue;
            var P = Points[I];
            Result.AddPath(Gp, Ox + P.X, Y + P.Y);
        }
        return Result;
    }

    private static float DetectCornerInset(XElement El, SKRect Bbox)
    {
        float MaxR = MathF.Min(Bbox.Width, Bbox.Height) / 2f;
        if (MaxR <= 0.5f)
            return 0f;

        var Shape = El.Name.LocalName is "rect" or "path"
            ? El
            : El.Descendants().FirstOrDefault(e => e.Name.LocalName is "rect" or "path");
        if (Shape is null)
            return 0f;

        if (Shape.Name.LocalName == "rect")
        {
            float Rx = ParseLen((string?)Shape.Attribute("rx"), 0f);
            float Ry = ParseLen((string?)Shape.Attribute("ry"), 0f);
            float R0 = MathF.Max(Rx, Ry);
            return R0 > 0.5f ? MathF.Min(R0, MaxR) : 0f;
        }

        string? D = (string?)Shape.Attribute("d");
        if (D is null)
            return 0f;

        var Subs = SvgPath.Flatten(D);
        if (Subs.Count == 0)
            return 0f;

        float Eps = MathF.Max(0.5f, MathF.Min(Bbox.Width, Bbox.Height) * 0.002f);
        float MinTopX = float.MaxValue, MaxTopX = float.MinValue;
        float MinLeftY = float.MaxValue, MaxLeftY = float.MinValue;
        foreach (var S in Subs)
            foreach (var P in S.Points)
            {
                if (MathF.Abs(P.Y - Bbox.Top) < Eps) { MinTopX = MathF.Min(MinTopX, P.X); MaxTopX = MathF.Max(MaxTopX, P.X); }
                if (MathF.Abs(P.X - Bbox.Left) < Eps) { MinLeftY = MathF.Min(MinLeftY, P.Y); MaxLeftY = MathF.Max(MaxLeftY, P.Y); }
            }
        if (MinTopX > MaxTopX || MinLeftY > MaxLeftY)
            return 0f;

        float R = MathF.Min(
            MathF.Min(MinTopX - Bbox.Left, Bbox.Right - MaxTopX),
            MathF.Min(MinLeftY - Bbox.Top, Bbox.Bottom - MaxLeftY));
        if (R <= 0.5f)
            return 0f;
        return MathF.Min(R, MaxR);
    }

    private static void DrawNineSlice(SKCanvas Canvas, SKPicture Pic, SKRect Src, SKRect Dst,
        float SrcInset, float DstInset, SKPaint Paint)
    {
        float Si = MathF.Min(SrcInset, MathF.Min(Src.Width, Src.Height) / 2f);
        float DiX = MathF.Min(DstInset, Dst.Width / 2f);
        float DiY = MathF.Min(DstInset, Dst.Height / 2f);

        Span<float> Sx = stackalloc float[] { Src.Left, Src.Left + Si, Src.Right - Si, Src.Right };
        Span<float> Sy = stackalloc float[] { Src.Top, Src.Top + Si, Src.Bottom - Si, Src.Bottom };
        Span<float> Dx = stackalloc float[] { Dst.Left, Dst.Left + DiX, Dst.Right - DiX, Dst.Right };
        Span<float> Dy = stackalloc float[] { Dst.Top, Dst.Top + DiY, Dst.Bottom - DiY, Dst.Bottom };

        for (int I = 0; I < 3; I++)
            for (int J = 0; J < 3; J++)
            {
                float Sw = Sx[I + 1] - Sx[I], Sh = Sy[J + 1] - Sy[J];
                float Dw = Dx[I + 1] - Dx[I], Dh = Dy[J + 1] - Dy[J];
                if (Sw <= 1e-3f || Sh <= 1e-3f || Dw <= 1e-3f || Dh <= 1e-3f)
                    continue;

                Canvas.Save();

                float Cl = Dx[I] - (I > 0 ? 0.5f : 0f);
                float Ct = Dy[J] - (J > 0 ? 0.5f : 0f);
                float Cr = Dx[I + 1] + (I < 2 ? 0.5f : 0f);
                float Cb = Dy[J + 1] + (J < 2 ? 0.5f : 0f);
                Canvas.ClipRect(new SKRect(Cl, Ct, Cr, Cb));

                Canvas.Translate(Dx[I], Dy[J]);
                Canvas.Scale(Dw / Sw, Dh / Sh);
                Canvas.Translate(-Sx[I], -Sy[J]);
                Canvas.DrawPicture(Pic, Paint);
                Canvas.Restore();
            }
    }

    private static InputInfo ExtractInputInfo(XElement Txt)
    {
        string Weight = Attr(Txt, "font-weight") ?? "";
        bool Bold = Weight.Contains("bold", StringComparison.OrdinalIgnoreCase)
                    || (int.TryParse(Weight, out var W) && W >= 600);
        var Fill = Color.Black;
        if (Attr(Txt, "fill") is { } F && Color.TryParse(F, out var C)) Fill = C;
        
        string Anchor = (Attr(Txt, "text-anchor") ?? "start").Trim().ToLowerInvariant();
        var Alignment = Anchor switch
        {
            "middle" => SKTextAlign.Center,
            "end" => SKTextAlign.Right,
            _ => SKTextAlign.Left
        };

        return new InputInfo
        {
            X = ParseLen(Attr(Txt, "x"), 0f),
            Y = ParseLen(Attr(Txt, "y"), 0f),
            Size = MathF.Max(1f, ParseLen(Attr(Txt, "font-size"), 16f)),
            Family = Attr(Txt, "font-family") ?? "Segoe UI",
            Bold = Bold,
            Fill = Fill,
            InitialText = Txt.Value,
            Alignment = Alignment,
        };
    }

    private static string? SerifOrId(XElement El)
    {
        foreach (var A in El.Attributes())
            if (A.Name.LocalName == "id" && A.Name.NamespaceName.Length > 0)
                return A.Value;
        return (string?)El.Attribute("id");
    }

    private static string? ClipRef(XElement El)
    {
        string? Raw = (string?)El.Attribute("clip-path");
        if (Raw is null)
        {
            var Style = (string?)El.Attribute("style");
            if (Style is not null)
            {
                int I = Style.IndexOf("clip-path", StringComparison.OrdinalIgnoreCase);
                if (I >= 0)
                {
                    int Colon = Style.IndexOf(':', I);
                    int Semi = Style.IndexOf(';', I);
                    if (Colon >= 0)
                        Raw = Style.Substring(Colon + 1, (Semi < 0 ? Style.Length : Semi) - Colon - 1);
                }
            }
        }
        if (Raw is null) return null;

        int U = Raw.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (U < 0) return null;
        int Open = Raw.IndexOf('#', U);
        int Close = Raw.IndexOf(')', U);
        if (Open < 0 || Close < 0 || Close <= Open) return null;
        return Raw.Substring(Open + 1, Close - Open - 1).Trim().Trim('"', '\'');
    }

    private static string? Attr(XElement El, string Name)
    {
        var A = (string?)El.Attribute(Name);
        if (A is not null)
            return A;
        foreach (var At in El.Attributes())
            if (At.Name.LocalName == Name)
                return At.Value;

        var Style = (string?)El.Attribute("style");
        if (Style is not null)
        {
            var Parts = Style.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var Part in Parts)
            {
                var Kv = Part.Split(new[] { ':' }, 2);
                if (Kv.Length == 2 && Kv[0].Trim().Equals(Name, StringComparison.OrdinalIgnoreCase))
                {
                    return Kv[1].Trim();
                }
            }
        }
        return null;
    }

    private static HAlign? ParseH(string? s) => s?.ToLowerInvariant() switch
    {
        "left" => HAlign.Left,
        "center" => HAlign.Center,
        "right" => HAlign.Right,
        "stretch" => HAlign.Stretch,
        _ => null,
    };

    private static VAlign? ParseV(string? s) => s?.ToLowerInvariant() switch
    {
        "top" => VAlign.Top,
        "center" => VAlign.Center,
        "bottom" => VAlign.Bottom,
        "stretch" => VAlign.Stretch,
        _ => null,
    };

    private static float ParseLen(string? S, float Def)
    {
        if (S is null) return Def;
        S = S.Trim();
        int End = S.Length;
        while (End > 0 && !char.IsDigit(S[End - 1]) && S[End - 1] != '.') End--;
        return float.TryParse(S.AsSpan(0, End), NumberStyles.Float, CultureInfo.InvariantCulture, out var V) ? V : Def;
    }

    private static bool ParseBool(string? S, bool Def)
    {
        if (string.IsNullOrWhiteSpace(S)) return Def;
        return S.Equals("true", StringComparison.OrdinalIgnoreCase) || S.Equals("1");
    }

    private static Thickness ParseThickness(string? S)
    {
        if (string.IsNullOrWhiteSpace(S)) return new Thickness(0);
        var Parts = S.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (Parts.Length == 1 && float.TryParse(Parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var U))
            return new Thickness(U);
        if (Parts.Length == 2 && float.TryParse(Parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var H)
                              && float.TryParse(Parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var V))
            return new Thickness(H, V);
        if (Parts.Length == 4 && float.TryParse(Parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var L)
                              && float.TryParse(Parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var T)
                              && float.TryParse(Parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var R)
                              && float.TryParse(Parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var B))
            return new Thickness(L, T, R, B);
        return new Thickness(0);
    }

    private static Cursor ParseCursor(string? S)
    {
        if (string.IsNullOrWhiteSpace(S)) return Cursor.Default;
        if (Enum.TryParse<Cursor>(S, true, out var CursorVal))
            return CursorVal;
        return Cursor.Default;
    }

    private static float ParseFloat(string? S, float Def)
    {
        if (string.IsNullOrWhiteSpace(S)) return Def;
        return float.TryParse(S, NumberStyles.Float, CultureInfo.InvariantCulture, out var V) ? V : Def;
    }

    private static float? ParseFloatNullable(string? S)
    {
        if (string.IsNullOrWhiteSpace(S)) return null;
        return float.TryParse(S, NumberStyles.Float, CultureInfo.InvariantCulture, out var V) ? V : null;
    }

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers", Justification = "Event handler methods are preserved via generator-emitted DynamicDependency attributes")]
    private void BindEvents(Control Ctl, XElement TargetEl)
    {
        void Bind(string EventName, Action<Action> Subscribe)
        {
            var MethodName = Attr(TargetEl, EventName) ?? Attr(TargetEl, EventName.ToLowerInvariant());
            if (MethodName is null)
                return;

            var Method = GetType().GetMethod(MethodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (Method is null)
            {
                Console.WriteLine($"[WARNING] Method {MethodName} for event {EventName} on control {Ctl.Id} was not found.");
                return;
            }

            var Params = Method.GetParameters();
            if (Params.Length > 1 || (Params.Length == 1 && !typeof(Control).IsAssignableFrom(Params[0].ParameterType)))
            {
                Console.WriteLine($"[WARNING] Method {MethodName} on event {EventName} on control {Ctl.Id} has incompatible signature.");
                return;
            }

            void Handler()
            {
                if (Params.Length == 1)
                    Method.Invoke(this, new object[] { Ctl });
                else
                    Method.Invoke(this, null);
            }
            Subscribe(Handler);
        }
        Bind("Click", H => Ctl.OnClick += H);
        Bind("Press", H => Ctl.OnPress += H);
        Bind("HoverEnter", H => Ctl.OnHoverEnter += H);
        Bind("HoverLeave", H => Ctl.OnHoverLeave += H);
        if (Ctl is Elements.TextBox Tb)
            Bind("Submit", H => Tb.OnSubmit += H);
    }

#if DEBUG

    private void StartHotReloadWatcher()
    {
        try
        {
            var Paths = new List<string>();
            if (_svg_source_path is not null) Paths.Add(_svg_source_path);
            if (_aui_source_path is not null) Paths.Add(_aui_source_path);
            if (Paths.Count == 0)
                return;

            var Dir = Path.GetDirectoryName(Paths[0]);
            if (Dir is null || !Directory.Exists(Dir))
                return;

            var Names = new HashSet<string>(Paths.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

            _hot_watcher = new FileSystemWatcher(Dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            void OnChange(object Sender, FileSystemEventArgs E)
            {
                if (E.Name is null || !Names.Contains(E.Name))
                    return;
                lock (_hot_lock)
                {
                    _hot_pending = true;
                    _hot_retries = 0;
                    _hot_request_ticks = DateTime.UtcNow.Ticks;
                    if (E.Name.EndsWith(".aui", StringComparison.OrdinalIgnoreCase))
                        _hot_aui_changed = true;
                }
            }
            _hot_watcher.Changed += OnChange;
            _hot_watcher.Created += OnChange;
            _hot_watcher.Renamed += (S, E) => OnChange(S, E);
            _hot_watcher.EnableRaisingEvents = true;
            Console.WriteLine($"[AUI HOT-RELOAD] Watching file changes: {string.Join(", ", Names)}");
        }
        catch (Exception Ex)
        {
            Console.WriteLine($"[AUI HOT-RELOAD WARNING] Failed to start watcher: {Ex.Message}");
        }
    }

    private void ProcessHotReload()
    {
        bool DoReload = false;
        bool AuiChanged = false;
        long startTicks = 0;
        lock (_hot_lock)
        {
            if (_hot_pending && DateTime.UtcNow.Ticks - _hot_request_ticks > TimeSpan.FromMilliseconds(250).Ticks)
            {
                DoReload = true;
                AuiChanged = _hot_aui_changed;
                startTicks = _hot_request_ticks;
            }
        }
        if (!DoReload)
            return;

        try
        {
            LoadUi(ApplyAuiProps: AuiChanged);
            lock (_hot_lock)
            {
                if (_hot_request_ticks == startTicks)
                {
                    _hot_pending = false;
                    _hot_aui_changed = false;
                    _hot_retries = 0;
                }
            }
            OnHotReload();
            Console.WriteLine("[AUI HOT-RELOAD] UI rebuilt according to modified design.");
        }
        catch (Exception Ex)
        {
            lock (_hot_lock)
            {
                if (++_hot_retries >= 8)
                {
                    _hot_pending = false;
                    _hot_aui_changed = false;
                    Console.WriteLine($"[AUI HOT-RELOAD WARNING] Reload failed: {Ex.Message}");
                }
                else
                {
                    _hot_request_ticks = DateTime.UtcNow.Ticks;
                }
            }
        }
    }

    private void SyncAuiWithSvg(string SvgXmlText, string AuiXmlText)
    {
        if (_aui_source_path is null)
            return;
        try
        {
            var Synced = Generator.AuiSyncCore.ComputeSyncedAui(SvgXmlText, AuiXmlText);
            if (Synced is not null && Generator.AuiSyncCore.WriteIfChanged(_aui_source_path, Synced))
                Console.WriteLine($"[AUI SYNC] .aui synchronized with the SVG tree structure: {_aui_source_path}");

            var Ids = Generator.AuiSyncCore.CollectSvgControls(ParseXmlLenient(SvgXmlText));
            var Ns = ParseXmlLenient(Synced ?? AuiXmlText).Root?.Name.NamespaceName ?? string.Empty;
            var XsdPath = Path.Combine(Path.GetDirectoryName(_aui_source_path)!, "AurevonUI.xsd");
            if (Generator.AuiSyncCore.WriteIfChanged(XsdPath, Generator.AuiSyncCore.GenerateXsd(Ids.Select(C => C.Id).ToList(), Ns)))
                Console.WriteLine($"[AUI SCHEMA] Updated XSD schema for XML IntelliSense: {XsdPath}");
        }
        catch (Exception Ex)
        {
            Console.WriteLine($"[AUI SYNC WARNING] Error synchronizing AUI with SVG: {Ex.Message}");
        }
    }
#endif

    private static Control CreateControlInstance(string TypeName) => TypeName switch
    {
        "Group" => new Elements.Group(),
        "Path" => new Elements.Path(),
        "TextControl" => new Elements.TextControl(),
        "Rect" => new Elements.Rect(),
        "Circle" => new Elements.Circle(),
        "Ellipse" => new Elements.Ellipse(),
        "Line" => new Elements.Line(),
        "Polyline" => new Elements.Polyline(),
        "Polygon" => new Elements.Polygon(),
        "Image" => new Elements.Image(),
        "TextBox" => new Elements.TextBox(),
        "ScrollViewer" => new Elements.ScrollViewer(),
        _ => new Control(),
    };

    public Control Get(string id)
    {
        if (_controls_dict.TryGetValue(id, out var c))
            return c;
        throw new KeyNotFoundException(
            $"Control '{id}' not found in .aui. Available: {string.Join(", ", _controls_dict.Keys)}");
    }

    public T Get<T>(string id) where T : Control
    {
        var C = Get(id);
        if (C is T Typed)
            return Typed;
        throw new InvalidCastException(
            $"Control '{id}' is of type {C.GetType().Name}, not {typeof(T).Name}. " +
            $"Check Type=\"...\" in .aui or the SVG element type.");
    }

    public bool TryGet(string id, out Control control) => _controls_dict.TryGetValue(id, out control!);

    public IReadOnlyList<Control> Controls => _control_list;

    public ItemsControl AsItemsControl(string TemplateId, Orientation Orientation = Orientation.Vertical, float Spacing = 8f)
    {
        if (!_templates.TryGetValue(TemplateId, out var Tpl))
            throw new KeyNotFoundException($"Template '{TemplateId}' not found. Available: {string.Join(", ", _templates.Keys)}");

        Control? Container = null;
        var P = Tpl.Parent;
        while (P is not null && P != _root)
        {
            var pid = (string?)P.Attribute("id");
            if (pid is not null && _controls_dict.TryGetValue(pid, out Container)) break;
            P = P.Parent;
        }
        if (Container is null)
            throw new InvalidOperationException($"Template '{TemplateId}' does not have a parent Control (container).");

        var Ic = new ItemsControl(this, TemplateId, Container, Orientation, Spacing);
        _items_controls.Add(Ic);
        return Ic;
    }

    internal void GenerateItems(ItemsControl Ic, System.Collections.IEnumerable? Items)
    {
        foreach (var Old in Ic._generated)
        {
            Old.SvgOwner?.Dispose();
            _control_list.Remove(Old);
            Ic.Container.Children.Remove(Old);
            _controls_dict.Remove(Old.Id);
        }
        Ic._generated.Clear();
        if (Items is null) return;

        var Tpl = _templates[Ic.TemplateId];
        _svg_bounds.TryGetValue(Ic.TemplateId, out var TplBounds);

        float CS = Ic.Container.ContentScale <= 0f ? 1f : Ic.Container.ContentScale;
        float ExtentPx = (Ic.Orientation == Orientation.Vertical ? TplBounds.Height : TplBounds.Width) * CS;
        float Step = ExtentPx + Ic.Spacing;

        int I = 0;
        foreach (var Item in Items)
        {
            var Clone = new XElement(Tpl);
            string ItemId = $"__item{_gen_seq++}";
            Clone.SetAttributeValue("id", ItemId);

            if (Item is not null)
                BindTemplate(Clone, Item);
            ConvertTextToPaths(Clone);

            var Doc = BuildItemDoc(Clone);
            var Svg = LoadSvg(Doc);

            var Ctl = new Control
            {
                Id = ItemId,
                Parent = Ic.Container,
                XmlElement = Clone,
                BoundsSvg = TplBounds,
                FrameBoundsSvg = TplBounds,
                Picture = Svg?.Picture,
                SvgOwner = Svg,
            };
            Ctl.OffsetX = Ic.Orientation == Orientation.Horizontal ? I * Step : 0f;
            Ctl.OffsetY = Ic.Orientation == Orientation.Vertical ? I * Step : 0f;

            Ic.Container.Children.Add(Ctl);
            _control_list.Add(Ctl);
            _controls_dict[ItemId] = Ctl;
            Ic._generated.Add(Ctl);

            if (Item is not null)
                Ic.OnItemCreated?.Invoke(Ctl, Item);
            I++;
        }
    }

    private static readonly Regex _bind_token =
        new(@"\{\s*([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?::([^{}]*))?\s*\}",
            RegexOptions.Compiled);

    private static void BindTemplate(XElement Clone, object Item)
    {
        SubstituteTokens(Clone, Item);

        foreach (var El in Clone.Descendants().ToList())
        {
            var Fid = (string?)El.Attribute("id");
            if (Fid is null || !TryResolve(Item, Fid, out var Val))
                continue;

            if (Val is bool B)
            {
                if (!B) El.Remove();
                continue;
            }

            string S = FormatValue(Val, null);
            switch (El.Name.LocalName)
            {
                case "text":
                    El.Value = S;
                    break;
                case "image":
                    El.SetAttributeValue(XName.Get("href", "http://www.w3.org/1999/xlink"), S);
                    El.SetAttributeValue("href", S);
                    break;
                case "g":

                    SetGroupText(El, S);
                    break;
                default:
                    SetStyleFill(El, S);
                    break;
            }
        }
    }

    private static void SubstituteTokens(XElement Root, object Item)
    {
        foreach (var El in Root.DescendantsAndSelf())
        {
            foreach (var A in El.Attributes())
            {
                if (A.Value.IndexOf('{') >= 0)
                    A.Value = ReplaceTokens(A.Value, Item);
            }
            foreach (var N in El.Nodes())
            {
                if (N is XText T && T.Value.IndexOf('{') >= 0)
                    T.Value = ReplaceTokens(T.Value, Item);
            }
        }
    }

    private static string ReplaceTokens(string Input, object Item) =>
        _bind_token.Replace(Input, M =>
        {
            if (!TryResolve(Item, M.Groups[1].Value, out var Val))
                return M.Value;
            return FormatValue(Val, M.Groups[2].Success ? M.Groups[2].Value : null);
        });

    private static string FormatValue(object? Val, string? Format)
    {
        if (Val is null) return "";
        if (Format is not null && Val is IFormattable F)
            return F.ToString(Format, CultureInfo.InvariantCulture);
        return Convert.ToString(Val, CultureInfo.InvariantCulture) ?? "";
    }

    private static void SetGroupText(XElement Group, string Text)
    {
        string Fill = FirstFill(Group) ?? Attr(Group, "fill") ?? "#000";
        string? Family = Attr(Group, "font-family");
        string? Weight = Attr(Group, "font-weight");

        Group.RemoveNodes();

        string Style = $"fill:{Fill};";
        if (Family is not null) Style += $"font-family:{Family};";
        if (Weight is not null) Style += $"font-weight:{Weight};";

        var Txt = new XElement(Group.Name.Namespace + "text",
            new XAttribute("x", "0"),
            new XAttribute("y", "0"),
            new XAttribute("font-size", "1"),
            new XAttribute("style", Style),
            Text);
        Group.Add(Txt);
    }

    private static string? FirstFill(XElement El)
    {
        foreach (var D in El.Descendants())
        {
            var F = Attr(D, "fill");
            if (F is not null && !F.Equals("none", StringComparison.OrdinalIgnoreCase))
                return F;
        }
        return null;
    }

    private static void SetStyleFill(XElement El, string Fill)
    {
        var Style = (string?)El.Attribute("style") ?? "";
        var Parts = Style.Split(';')
            .Where(p => p.Trim().Length > 0 && !p.TrimStart().StartsWith("fill:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Parts.Add($"fill:{Fill}");
        El.SetAttributeValue("style", string.Join(";", Parts) + ";");
        El.SetAttributeValue("fill", null);
    }

    private static readonly ConcurrentDictionary<(Type Type, string Path), Func<object, object?>?> _accessor_cache = new();

    private static bool TryResolve(object Item, string Path, out object? Val)
    {
        Val = null;

        if (Item is System.Collections.IDictionary D)
        {
            if (!D.Contains(Path)) return false;
            Val = D[Path];
            return true;
        }

        var Accessor = _accessor_cache.GetOrAdd((Item.GetType(), Path),
            static key => BuildAccessor(key.Type, key.Path));
        if (Accessor is null) return false;
        Val = Accessor(Item);
        return true;
    }

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers", Justification = "Bound top-level members preserved via Bind<T>; nested paths documented as requiring untrimmed apps")]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers", Justification = "Bound top-level members preserved via Bind<T>; nested paths documented as requiring untrimmed apps")]
    private static Func<object, object?>? BuildAccessor(Type Type, string Path)
    {
        var Segments = Path.Split('.');
        var Members = new MemberInfo[Segments.Length];
        var T = Type;
        for (int I = 0; I < Segments.Length; I++)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            MemberInfo? Mi = T.GetProperty(Segments[I], Flags) ?? (MemberInfo?)T.GetField(Segments[I], Flags);
            if (Mi is null) return null;
            Members[I] = Mi;
            T = Mi is PropertyInfo P ? P.PropertyType : ((FieldInfo)Mi).FieldType;
        }

        return Obj =>
        {
            object? Cur = Obj;
            foreach (var Mi in Members)
            {
                if (Cur is null) return null;
                Cur = Mi is PropertyInfo P ? P.GetValue(Cur) : ((FieldInfo)Mi).GetValue(Cur);
            }
            return Cur;
        };
    }

    private XDocument BuildItemDoc(XElement Content)
    {
        var NewRoot = new XElement(_root.Name, _root.Attributes());
        foreach (var d in _root.Elements())
        {
            var n = d.Name.LocalName;
            if (n is "defs" or "clipPath" or "linearGradient" or "radialGradient" or "pattern" or "mask" or "filter")
                NewRoot.Add(new XElement(d));
        }
        NewRoot.Add(new XElement(Content));
        return new XDocument(NewRoot);
    }

    protected virtual void OnFrame(FrameContext ctx) { }

    protected virtual void OnFrameBefore(FrameContext ctx) { }

    public sealed override void OnUpdate(FrameContext ctx)
    {
        _currentTime = ctx.Time;
        if (_backspaceDown && _focused is not null)
        {
            if (_currentTime >= _backspaceNextRepeatTime)
            {
                if (_focused.Text.Length > 0) _focused.Text = _focused.Text[..^1];
                _backspaceNextRepeatTime = _currentTime + 0.045f;
            }
        }
        else
        {
            _backspaceDown = false;
        }

#if DEBUG

        ProcessHotReload();
#endif

        FlushDirtyStyles();
        ActivateInputs();
        UpdateItemsLayout();

        float Vw = MathF.Max(_view_w, 1e-3f);
        float Vh = MathF.Max(_view_h, 1e-3f);

        float Sx = 1f;
        float Sy = 1f;

        switch (Stretch)
        {
            case AuiStretch.Fill:
                Sx = ctx.Width / Vw;
                Sy = ctx.Height / Vh;
                break;
            case AuiStretch.Uniform:
                float Scale = MathF.Min(ctx.Width / Vw, ctx.Height / Vh);
                Sx = Scale;
                Sy = Scale;
                break;
            case AuiStretch.None:
            default:
                Sx = 1f;
                Sy = 1f;
                break;
        }

        float ContentW = Vw * Sx;
        float ContentH = Vh * Sy;

        float Ox = HorizontalAlignment switch
        {
            HAlign.Left => 0,
            HAlign.Right => ctx.Width - ContentW,
            _ => (ctx.Width - ContentW) / 2f,
        };
        float Oy = VerticalAlignment switch
        {
            VAlign.Top => 0,
            VAlign.Bottom => ctx.Height - ContentH,
            _ => (ctx.Height - ContentH) / 2f,
        };

        foreach (var C in _control_list)
        {
            if (C.Parent is null)
            {
                UpdateControlLayout(C, ctx, Sx, Sy, Ox, Oy);
            }
        }
        UpdateHover();

        var Canvas = ctx.Canvas;

        OnFrameBefore(ctx);

        if (_base_picture is not null)
        {
            Canvas.Save();
            Canvas.Translate(Ox, Oy);
            Canvas.Scale(Sx, Sy);
            Canvas.Translate(-_view_min_x, -_view_min_y);

            var Options = ctx.RenderOptions;
#pragma warning disable CS0618
            _draw_paint.IsAntialias = Options.Antialiasing;
            _draw_paint.FilterQuality = Options.FilterQuality.UseCubic || Options.FilterQuality.Filter != SKFilterMode.Nearest ? SKFilterQuality.High : SKFilterQuality.None;
            _draw_paint.Color = SKColors.White;
#pragma warning restore CS0618
            Canvas.DrawPicture(_base_picture, _draw_paint);
            Canvas.Restore();
        }

        foreach (var c in _control_list)
        {
            float TotalOpacity = GetAccumulatedOpacity(c);
            if (!c.Visible || TotalOpacity <= 0.001f || c.Picture is null)
                continue;

            var Options = ctx.RenderOptions;
            byte Alpha = (byte)(TotalOpacity * 255);

            Canvas.Save();

            var Clip = c.Parent;
            while (Clip is not null && Clip is not Elements.ScrollViewer) Clip = Clip.Parent;
            if (Clip is not null)
            {
                var Cr = new SKRect(Clip.ScreenX, Clip.ScreenY,
                    Clip.ScreenX + Clip.ScreenWidth, Clip.ScreenY + Clip.ScreenHeight);
                float Rr = Clip.CornerInset * Clip.ContentScale;
                if (Rr > 0.5f)
                {
                    using var RRect = new SKRoundRect(Cr, Rr);
                    Canvas.ClipRoundRect(RRect, SKClipOperation.Intersect, true);
                }
                else
                {
                    Canvas.ClipRect(Cr, SKClipOperation.Intersect, true);
                }
            }

            Canvas.Translate(c.ScreenX, c.ScreenY);

            if (c is Elements.Image ImgC && ImgC.Source is not null)
            {
                var Bmp = ImgC.Source.Sk;
                Canvas.Scale(c.ScreenWidth / Bmp.Width, c.ScreenHeight / Bmp.Height);
#pragma warning disable CS0618
                _draw_paint.FilterQuality = SKFilterQuality.None;
#pragma warning restore CS0618
                _draw_paint.IsAntialias = Options.Antialiasing;
                _draw_paint.Color = SKColors.White.WithAlpha(Alpha);
                Canvas.DrawImage(Bmp, 0, 0, Options.FilterQuality, _draw_paint);
            }
            else
            {
                var Cb = EffBounds(c);
                float Bw = MathF.Max(Cb.Width, 1e-3f);
                float Bh = MathF.Max(Cb.Height, 1e-3f);
                float ScaleX = c.ScreenWidth / Bw;
                float ScaleY = c.ScreenHeight / Bh;

#pragma warning disable CS0618
                _draw_paint.IsAntialias = Options.Antialiasing;
                _draw_paint.FilterQuality = Options.FilterQuality.UseCubic || Options.FilterQuality.Filter != SKFilterMode.Nearest ? SKFilterQuality.High : SKFilterQuality.None;
                _draw_paint.Color = SKColors.White.WithAlpha(Alpha);
#pragma warning restore CS0618

                float Cs = c.ContentScale;
                bool Stretched = MathF.Abs(ScaleX - Cs) > 0.002f || MathF.Abs(ScaleY - Cs) > 0.002f;
                if (c.CornerInset > 0.5f && Stretched)
                {
                    var Dst = new SKRect(0, 0, c.ScreenWidth, c.ScreenHeight);
                    if (Alpha < 255)
                    {
                        Canvas.SaveLayer(_draw_paint);
                        _draw_paint.Color = SKColors.White;
                        
                        DrawNineSlice(Canvas, c.Picture, Cb, Dst,
                            c.CornerInset, c.CornerInset * Cs, _draw_paint);
                            
                        Canvas.Restore();
                        _draw_paint.Color = SKColors.White.WithAlpha(Alpha);
                    }
                    else
                    {
                        DrawNineSlice(Canvas, c.Picture, Cb, Dst,
                            c.CornerInset, c.CornerInset * Cs, _draw_paint);
                    }
                }
                else
                {
                    Canvas.Scale(ScaleX, ScaleY);
                    Canvas.Translate(-Cb.Left, -Cb.Top);
                    Canvas.DrawPicture(c.Picture, _draw_paint);
                }
            }
            Canvas.Restore();
        }

        DrawScrollbars(Canvas);
        DrawInputs(ctx);

        OnFrame(ctx);
    }

    private void DrawInputs(FrameContext ctx)
    {
        var Canvas = ctx.Canvas;
        foreach (var Ctl in _control_list)
        {
            if (Ctl is not Elements.TextControl C || !C.Visible)
                continue;

            var Eb = EffBounds(C);
            float ScaleX = Eb.Width > 0f ? C.ScreenWidth / Eb.Width : 1f;
            float ScaleY = Eb.Height > 0f ? C.ScreenHeight / Eb.Height : 1f;
            float Bx = C.ScreenX + (C.TextSvgX - Eb.Left) * ScaleX;
            float By = C.ScreenY + (C.TextSvgY - Eb.Top) * ScaleY;
            float Size = C.TextSvgSize * ScaleY;
            if (Size < 1f) continue;

            float TotalOpacity = GetAccumulatedOpacity(C);
            if (TotalOpacity <= 0.001f)
                continue;

            bool Empty = C.Text.Length == 0;
            string Shown = Empty ? C.Placeholder : C.Text;
            var TxtColor = (Empty ? C.PlaceholderColor : C.TextColor).ToSK();
            TxtColor = TxtColor.WithAlpha((byte)(TxtColor.Alpha * TotalOpacity));

            Canvas.Save();
            if (C is Elements.TextBox)
            {
                var Box = new SKRect(C.ScreenX, C.ScreenY, C.ScreenX + C.ScreenWidth, C.ScreenY + C.ScreenHeight);
                float R = C.CornerInset * C.ContentScale;
                if (R > 0.5f) { using var RR = new SKRoundRect(Box, R); Canvas.ClipRoundRect(RR, SKClipOperation.Intersect, true); }
                else Canvas.ClipRect(Box, SKClipOperation.Intersect, true);
            }

            if (Shown.Length > 0)
                ctx.DrawText(Shown, Bx, By, Size, TxtColor, C.TextFontFamily, C.TextBold, Align: C.TextAlignment);

            if (C is Elements.TextBox Tb && Tb.IsFocused && ((int)(ctx.Time * 2f) & 1) == 0)
            {
                float CaretX = Bx + FrameContext.MeasureText(C.Text, Size, C.TextFontFamily, C.TextBold);
                _draw_paint.IsAntialias = true;
                _draw_paint.Color = C.TextColor.ToSK().WithAlpha((byte)(C.TextColor.A * TotalOpacity));
                Canvas.DrawRect(new SKRect(CaretX, By - Size * 0.78f, CaretX + MathF.Max(1.5f, Size * 0.06f), By + Size * 0.12f), _draw_paint);
            }
            Canvas.Restore();
        }
    }

    private void DrawScrollbars(SKCanvas Canvas)
    {
        foreach (var Ctl in _control_list)
        {
            if (Ctl is not Elements.ScrollViewer C || !C.ScrollbarVisible || C.ScrollMaxY <= 0.5f || !C.Visible)
                continue;

            float Pad = C.ScrollbarPadding;
            float BarW = MathF.Max(2f, C.ScrollbarWidth);
            float TrackX = C.ScreenX + C.ScreenWidth - BarW - Pad;
            float TrackY = C.ScreenY + Pad;
            float TrackH = C.ScreenHeight - 2f * Pad;
            if (TrackH <= 4f)
                continue;

            float Radius = BarW / 2f;

            if (C.ScrollbarTrackColor.A > 0)
            {
                _draw_paint.IsAntialias = true;
                _draw_paint.Color = C.ScrollbarTrackColor.ToSK();
                using var TrackRR = new SKRoundRect(new SKRect(TrackX, TrackY, TrackX + BarW, TrackY + TrackH), Radius);
                Canvas.DrawRoundRect(TrackRR, _draw_paint);
            }

            float ContentH = C.ScreenHeight + C.ScrollMaxY;
            float ThumbH = MathF.Max(24f, TrackH * (C.ScreenHeight / ContentH));
            ThumbH = MathF.Min(ThumbH, TrackH);
            float T = C.ScrollMaxY > 0f ? C.ScrollOffsetY / C.ScrollMaxY : 0f;
            float ThumbY = TrackY + T * (TrackH - ThumbH);

            _draw_paint.IsAntialias = true;
            _draw_paint.Color = C.ScrollbarColor.ToSK();
            using var ThumbRR = new SKRoundRect(new SKRect(TrackX, ThumbY, TrackX + BarW, ThumbY + ThumbH), Radius);
            Canvas.DrawRoundRect(ThumbRR, _draw_paint);
        }
    }

    private Control? HitTestTree(Control C, float Px, float Py)
    {
        if (!C.Visible || !C.IsEnabled)
            return null;

        for (int I = C.Children.Count - 1; I >= 0; I--)
        {
            var Hit = HitTestTree(C.Children[I], Px, Py);
            if (Hit is not null)
                return Hit;
        }

        if (C.IsInteractive && Px >= C.ScreenX && Px <= C.ScreenX + C.ScreenWidth &&
            Py >= C.ScreenY && Py <= C.ScreenY + C.ScreenHeight)
        {
            return C;
        }

        return null;
    }

    private void UpdateHover()
    {
        Control? hit = null;
        for (int i = _control_list.Count - 1; i >= 0; i--)
        {
            if (_control_list[i].Parent is null)
            {
                var h = HitTestTree(_control_list[i], _mouse_pos.X, _mouse_pos.Y);
                if (h is not null)
                {
                    hit = h;
                    break;
                }
            }
        }

        var prev = _hovered;
        if (!ReferenceEquals(prev, hit))
        {
            prev?.SetHovered(false);
            hit?.SetHovered(true);
            _hovered = hit;
        }
    }

    internal override void HandleMouseMove(Vector2 pos)
    {
        _mouse_pos = pos;
        UpdateHover();
    }

    internal override void HandleMouseDown(Vector2 pos)
    {
        _mouse_pos = pos;
        UpdateHover();
        _pressed = _hovered;
        if (_pressed is not null)
        {
            _pressed.IsPressed = true;
            _pressed.RaisePress();
        }

        var NewFocus = _pressed as Elements.TextBox;
        if (!ReferenceEquals(_focused, NewFocus))
        {
            if (_focused is not null) _focused.IsFocused = false;
            _focused = NewFocus;
            if (_focused is not null) _focused.IsFocused = true;
        }
    }

    private float _currentTime;
    private bool _backspaceDown;
    private float _backspacePressTime;
    private float _backspaceNextRepeatTime;

    internal override void HandleKeyChar(char c)
    {
        if (_focused is null || c < ' ' || c == 127) return;
        _focused.Text += c;
    }

    internal override void HandleKeyDown(Silk.NET.Input.Key key)
    {
        if (_focused is null) return;
        switch (key)
        {
            case Silk.NET.Input.Key.Backspace:
                _backspaceDown = true;
                _backspacePressTime = _currentTime;
                _backspaceNextRepeatTime = _currentTime + 0.4f;
                if (_focused.Text.Length > 0) _focused.Text = _focused.Text[..^1];
                break;
            case Silk.NET.Input.Key.Enter:
            case Silk.NET.Input.Key.KeypadEnter:
                _focused.RaiseSubmit();
                break;
            case Silk.NET.Input.Key.Escape:
                _focused.IsFocused = false;
                _focused = null;
                break;
        }
    }

    internal override void HandleKeyUp(Silk.NET.Input.Key key)
    {
        if (key == Silk.NET.Input.Key.Backspace)
        {
            _backspaceDown = false;
        }
    }

    internal override void HandleMouseUp(Vector2 pos)
    {
        _mouse_pos = pos;
        UpdateHover();

        var pressed = _pressed;
        _pressed = null;
        if (pressed is not null)
        {
            pressed.IsPressed = false;
            if (ReferenceEquals(pressed, _hovered))
                pressed.RaiseClick();
        }
    }

    internal override void HandleScroll(Vector2 pos, float delta)
    {

        Elements.ScrollViewer? Target = null;
        for (int I = _control_list.Count - 1; I >= 0; I--)
        {
            if (_control_list[I] is not Elements.ScrollViewer C || C.ScrollMaxY <= 0f)
                continue;
            if (pos.X >= C.ScreenX && pos.X <= C.ScreenX + C.ScreenWidth &&
                pos.Y >= C.ScreenY && pos.Y <= C.ScreenY + C.ScreenHeight)
            {
                Target = C;
                break;
            }
        }
        if (Target is null)
            return;

        const float Speed = 40f;
        Target.ScrollY = Target.ScrollOffsetY - delta * Speed;
    }

    internal override Cursor DesiredCursor => _hovered?.Cursor ?? Cursor.Default;

    public override void OnClose()
    {
#if DEBUG
        _hot_watcher?.Dispose();
        _hot_watcher = null;
#endif
        foreach (var c in _control_list)
        {
            if (c is Elements.Image Img) { Img.Source?.Dispose(); Img.Source = null; }
            c.SvgOwner?.Dispose();
            c.SvgOwner = null;
        }
        _draw_paint.Dispose();
        base.OnClose();
    }
}
