# AurevonUI

**AurevonUI** is a cross-platform .NET UI toolkit that renders its entire interface from SVG.
Design your screens in Affinity Designer, Illustrator, Inkscape, or any SVG editor; wire up
interactivity with a small companion `.aui` file; get typed controls, layout, animation, and
live hot reload — no XAML, no separate designer tool, no hand-written render loop.

> Requires **.NET 10** or later. Rendering is powered by [SkiaSharp](https://github.com/mono/SkiaSharp)
> over OpenGL via [Silk.NET](https://github.com/dotnet/Silk.NET), so it runs anywhere Skia and a GL
> context are available (Windows, Linux, macOS).

---

## Table of contents

- [Why AurevonUI](#why-aurevonui)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Project setup](#project-setup)
- [Core concepts](#core-concepts)
  - [The .svg / .aui pairing](#the-svg--aui-pairing)
  - [Typed controls & the source generator](#typed-controls--the-source-generator)
  - [Element types](#element-types)
  - [Layout system](#layout-system)
  - [Events](#events)
  - [Animation](#animation)
  - [Lists: ItemsControl & templates](#lists-itemscontrol--templates)
  - [Hot reload](#hot-reload)
  - [Window configuration](#window-configuration)
  - [Rendering options](#rendering-options)
  - [XML IntelliSense](#xml-intellisense)
- [Publishing (single-file & trimming)](#publishing-single-file--trimming)
- [Full example](#full-example)
- [Known limitations](#known-limitations)
- [License](#license)

---

## Why AurevonUI

- **The SVG *is* the UI.** No intermediate design format — export from whatever vector tool you
  already use. Gradients, clip paths, embedded fonts, and rounded corners all render as designed.
- **Typed controls, zero boilerplate.** A Roslyn source generator turns every named SVG element
  into a strongly-typed property (`Logo`, `CloseButton`, `ProgressValueBar`, …) on your window
  class — rename an element in the SVG, and the property renames itself on next build.
  Structural elements are typed automatically (`Path`, `Rect`, `Group`, …); higher-level widgets
  (`TextBox`, `ScrollViewer`) opt in with a single `Type="…"` attribute.
- **A tiny declarative overlay, not a second UI framework.** The `.aui` file only adds what SVG
  can't express: layout anchoring, event handlers, and a handful of behavioral flags. It's kept
  in sync with the SVG automatically — add a shape, get an `.aui` node for free.
- **Real keyframe animation**, not property-by-property tweening: `Animator.Timeline` animates
  `Opacity`, `Scale`, `StrokeWidth`, `Color`, `Thickness`, even path geometry (shape morphing),
  with automatic interruption (no animation fighting on rapid hover) and smooth hand-off between
  running animations.
- **Live hot reload.** Edit the SVG in your design tool while the app is running — the control
  tree rebuilds in place, in-flight animations and event subscriptions survive.
- **XML IntelliSense for free.** The generator also emits an `.xsd` schema next to your `.aui`
  file, so your editor autocompletes element names and attributes as you type.

---

## Installation

### From the templates (recommended)

Scaffolds a ready-to-run app with the `.csproj` already configured — embedded assets, trimming,
and single-file publishing all set up, so you can skip [Project setup](#project-setup) entirely:

```bash
dotnet new install AurevonUI.Templates
dotnet new aurevonui-app -n MyApp
cd MyApp
dotnet run
```

Add further windows with `dotnet new aurevonui-window -n SettingsWindow`. Both templates also show
up in the Visual Studio **New Project** / **Add New Item** dialogs.

### Into an existing project

```bash
dotnet add package AurevonUI
```

That's it for the library and the source generator — both come from the same package (the
generator ships as a Roslyn analyzer inside it). See [Project setup](#project-setup) below for
the few `.csproj` lines your app project still needs (to tell MSBuild which files are UI assets).

---

## Quick start

### 1. Design in SVG

Give elements you want to reference from code an `id` — that's the only requirement. A rounded
rectangle named `CloseButton`, a group named `Logo`, whatever your design calls for.

### 2. Describe interactivity in `.aui`

Create `MainWindow.aui` next to `MainWindow.svg`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window Svg="MainWindow.svg" HorizontalAlignment="Center" VerticalAlignment="Center">
  <Background StretchToWindow="True" Press="DragWindow" />
  <WindowButtons VerticalAlignment="Top" HorizontalAlignment="Right" Margin="10">
    <CloseButton Cursor="Hand" Click="Close" />
    <MinimizeButton Cursor="Hand" Click="Minimize" />
  </WindowButtons>
  <Logo Cursor="Hand" HoverEnter="OnLogoHoverEnter" HoverLeave="OnLogoHoverLeave" />
</Window>
```

You don't have to write this by hand from scratch: create an empty (or minimal) `.aui`, point its
`Svg="…"` attribute at the file, and the generator fills in the tree from your SVG structure on
the next build — you just add the attributes you need.

### 3. Code-behind

```csharp
using AurevonUI;

public class MainWindow : AuiWindow
{
    public MainWindow() : base("MainWindow.aui")
    {
        Title = "My App";
        Width = 1280;
        Height = 720;

        // CloseButton, Logo, etc. are generated properties — typed, no string lookups needed.
        Animator.Timeline(0.8f, Ease.CubicOut, delay: 0.3f,
            new Step(0.0, new Value(() => Logo.Opacity, 0f)),
            new Step(1.0, new Value(() => Logo.Opacity, 1f)));
    }

    private void OnLogoHoverEnter()
        => Animator.Timeline(0.2f, new Step(1.0, new Value(() => Logo.Scale, 1.05f)));

    private void OnLogoHoverLeave()
        => Animator.Timeline(0.2f, new Step(1.0, new Value(() => Logo.Scale, 1.0f)));
}
```

### 4. Entry point

```csharp
using AurevonUI;

FrameContext.LoadEmbeddedFonts();
new MainWindow().Run();
```

---

## Project setup

Your application project (not the library — this is what your `.csproj` needs) must tell MSBuild
which files are UI assets, so the source generator can see them and the app can load them at
runtime:

```xml
<ItemGroup>
  <!-- Generator input: lets the source generator produce typed properties and keep .aui in sync. -->
  <AdditionalFiles Include="*.svg" />
  <AdditionalFiles Include="*.aui" />

  <!-- Runtime: embedded so the compiled app doesn't need loose files next to the .exe. -->
  <EmbeddedResource Include="*.svg" />
  <EmbeddedResource Include="*.aui" />
  <EmbeddedResource Include="**\*.ttf" />
  <EmbeddedResource Include="**\*.otf" />
  <EmbeddedResource Include="*.png" />   <!-- window icon(s) referenced by IconPath -->
</ItemGroup>
```

Embedding assets like this is also what lets a published app ship as a single self-contained
executable — see [Publishing](#publishing-single-file--trimming).

In `DEBUG` builds, `AuiWindow` prefers reading the `.svg`/`.aui` straight from your project
folder (falling back to the embedded copy), which is what makes [hot reload](#hot-reload) work —
you don't need a rebuild to see design changes.

---

## Core concepts

### The .svg / .aui pairing

A window loads from a pair of files: the `.svg` (the visual design — every named element becomes
a `Control`) and the `.aui` (interactivity — layout anchoring, event handlers, and a few
behavioral flags that SVG has no attribute for). You can also point `base(...)` at just one file:
a `.aui` names its SVG via `Svg="…"`, and a bare `.svg` is used as-is if no matching `.aui` exists.

Elements are matched between the two files by `id` (or `Id`/`Name` on the `.aui` side); an `.aui`
node with no `id` is matched by its own tag name, so `<Logo Click="…" />` binds to the SVG element
with `id="Logo"`.

### Typed controls & the source generator

For every class inheriting `AuiWindow`, the generator emits one property per control id found in
the paired SVG/`.aui`:

```csharp
public global::AurevonUI.Elements.Path Background => Get<global::AurevonUI.Elements.Path>("Background");
public global::AurevonUI.Elements.Group WindowButtons => Get<global::AurevonUI.Elements.Group>("WindowButtons");
```

You can also look controls up manually:

```csharp
Control c = Get("SomeId");                 // throws if missing, lists available ids in the message
Path p = Get<Path>("SomeId");               // throws InvalidCastException on type mismatch
bool found = TryGet("MaybeId", out var c);  // no exception
IReadOnlyList<Control> all = Controls;      // every control, in document order
```

The generator also keeps your `.aui` file's element **tree** synchronized with the SVG's
structure — new shapes get inserted under the right parent in document order, moved/renamed
shapes get relocated, deleted shapes get removed, and your attributes/comments are preserved
throughout. This runs live via a file watcher inside the IDE process, not just on build.

### Element types

SVG tags map automatically to typed `Control` subclasses in the `AurevonUI.Elements` namespace:

| SVG tag | Type | Notes |
|---|---|---|
| `<g>`, `<svg>` | `Group` | Plain container. |
| `<path>` | `Path` | Adds `PathData` (the `d` attribute) — readable, writable, and morph-animatable. |
| `<rect>` | `Rect` | |
| `<circle>` | `Circle` | |
| `<ellipse>` | `Ellipse` | |
| `<line>` | `Line` | |
| `<polyline>` | `Polyline` | |
| `<polygon>` | `Polygon` | |
| `<image>` | `Image` | Adds `Source` (a `Bitmap`), auto-populated from an embedded `data:image/...` href. |
| `<text>` | `TextControl` | See below. |

Higher-level widgets opt in with `Type="…"` in `.aui` (only valid on `<g>`):

| `Type=` | Result | Notes |
|---|---|---|
| `Text` | `TextControl` | Live, editable-by-code text — not baked into the picture like plain `<text>`. |
| `TextBox` | `TextBox` (`: TextControl`) | Adds keyboard focus, typing, caret, `OnSubmit` (Enter). |
| `ScrollViewer` | `ScrollViewer` (`: Group`) | Clips + scrolls its children, draws a scrollbar. |

Every `Control` (the common base, in the `AurevonUI` namespace) exposes:

```csharp
string Id { get; }
Control? Parent { get; }
List<Control> Children { get; }
bool IsHittable, IsEnabled, Visible, IsVisible { get; set; }
Cursor Cursor { get; set; }
ICommand? Command { get; set; }
float Opacity, Scale, OffsetX, OffsetY { get; set; }
HAlign? HorizontalAlignment { get; set; }
VAlign? VerticalAlignment { get; set; }
Thickness Margin, MarginPercent { get; set; }
bool IsHovered, IsPressed { get; }
event Action? OnClick, OnPress, OnHoverEnter, OnHoverLeave;
string? Fill, Stroke { get; set; }        // raw SVG paint, live-editable
float? StrokeWidth { get; set; }
Color? FillColor, StrokeColor { get; set; }  // typed wrapper over Fill/Stroke
```

`TextControl` additionally exposes `Text`, `Placeholder`, `TextColor`, `PlaceholderColor`, and
`event Action<string>? OnTextChanged`. `TextBox` adds `IsFocused` and `event Action? OnSubmit`.
`ScrollViewer` adds `ScrollY`, `ScrollMax`, `ScrollPaddingTop/Bottom`, and `Scrollbar*` styling.

`Color` and `Bitmap` are AurevonUI's own lightweight wrapper types — SkiaSharp stays an
implementation detail, not part of the public API:

```csharp
var c = Color.Parse("#3388FF");     // or Color.TryParse(text, out var c)
Logo.FillColor = c.WithAlpha(200);

var bmp = Bitmap.FromFile("icon.png");
avatar.Source = bmp;
```

### Layout system

Positioning is anchor-based, relative to the **parent control** (or the window, at the root):

- `HorizontalAlignment` / `VerticalAlignment` (`Left/Center/Right/Stretch`,
  `Top/Center/Bottom/Stretch`) pin an element to an edge or stretch it across its container.
  Leave both `null` and the element just keeps its design-time position — useful for freely
  placed decorative shapes that should still track a resizing parent proportionally.
- `Margin` (pixels) and `MarginPercent` (percent of the parent's size) both anchor an element to
  its container **without** an explicit alignment: specify one or both sides on an axis and the
  element sizes itself to fit the gap. This is how progress-bar fills and responsive panels work:

  ```xml
  <ProgressBar>
    <ProgressValueBar Margin="2" MarginPercent="-1,-1,40,-1" />
  </ProgressBar>
  ```

  Here the fill is pinned 2px from every edge, *and* percent-anchored 40% from the right — so its
  width tracks the parent and shrinks as the right margin grows (drive it from code:
  `new Value(() => ProgressValueBar.MarginPercent.Right, 60f)` to animate progress to 60%).

  **`-1` is a sentinel meaning "ignore this component on this side"** — not "zero". A margin value
  of exactly `0` still anchors that side flush against the parent edge. Any other negative number
  is a genuine negative margin.
- `StretchToWindow="True"` ignores layout entirely and fills the whole window — for backgrounds.
- `Scale`, `OffsetX`, `OffsetY` apply on top of the computed position, and are what you animate
  for hover/press feedback without touching layout.

### Events

```csharp
Button.OnClick += () => { /* press + release inside the element */ };
Button.OnPress += DragWindow;   // fires immediately on mouse-down — used for window drag
Button.OnHoverEnter += () => { };
Button.OnHoverLeave += () => { };
TextBoxControl.OnSubmit += () => { };       // TextBox only, fires on Enter
TextField.OnTextChanged += text => { };     // TextControl / TextBox
```

Or bind by name straight from `.aui` — the attribute value is a method name on your window class
(no parameters, or one `Control` parameter), resolved by reflection:

```xml
<Logo Click="OnLogoClick" HoverEnter="OnLogoHoverEnter" HoverLeave="OnLogoHoverLeave" />
```

```csharp
private void OnLogoClick() { /* … */ }
```

Setting `Cursor` to anything other than `Cursor.Default`, or subscribing any handler, is enough
to make an element hit-testable — you don't need a separate "IsHittable" flag for normal buttons.

### Animation

`Animator.Timeline` is keyframe-based. A `Step` is a moment in normalized time (`0`–`1`) holding
one or more bound values; values across `Step`s pair up **by position**, not by name:

```csharp
Animator.Timeline(1f, Ease.CubicOut, delay: 0.2f,
    new Step(0.0, new Value(() => Panel.Opacity, 0f)),
    new Step(1.0, new Value(() => Panel.Opacity, 1f)));
```

- **Omit the `t = 0` keyframe** and the animation starts from the property's *current* value —
  ideal for one-shot hover/press feedback:

  ```csharp
  Button.OnHoverEnter += () =>
      Animator.Timeline(0.15f, new Step(1.0, new Value(() => Button.Scale, 1.05f)));
  ```

- **Every animation is automatically interruptible.** Identity is derived from the binding itself
  (the target instance + property) — no manual keys to manage. Trigger the same property again
  (e.g. rapid hover in/out) and the new animation simply replaces the running one.
- **`LazyStop`** (default `true`) makes that replacement smooth: the new animation continues from
  the *current live value* instead of snapping back to a `t = 0` keyframe, so interrupted motion
  never jumps.
- **`Value<T>`** animates more than `float` — `Thickness`, `Color`, `Vector2/3/4`, `double`, `int`
  all interpolate correctly out of the box:

  ```csharp
  new Step(1.0, new Value<Color>(() => Badge.FillColor!.Value, Color.Parse("#FF3366")))
  ```

  It even reaches one level into a struct-returning property — the `MarginPercent.Right` example
  above works because `Value`/`Value<T>` detect that pattern and read-mutate-write the whole
  struct through its declaring property's setter.
- **`PathValue`** morphs SVG path geometry between two `d` strings:

  ```csharp
  Icon.OnClick += () =>
  {
      _open = !_open;
      Animator.Timeline(0.5f, Ease.CubicInOut,
          new Step(1.0, new PathValue(() => Icon.PathData, _open ? OpenPath : ClosedPath)));
  };
  ```

  When both shapes share the same command structure — same commands, same number of points — it
  interpolates every coordinate smoothly; otherwise it hard-switches at the halfway point. Author
  both `d` strings from the same shape (points *moved*, never added or removed) to keep the morph
  fluid.

Lower-level helpers, if you want to drive values yourself instead of through `Timeline`:

```csharp
Animation.Ease Ease;                 // Linear, QuadIn/Out/InOut, CubicIn/Out/InOut, SineInOut,
                                      // ExpoOut, BackOut, ElasticOut, BounceOut
float Animation.Lerp(float a, float b, float t);
Vector2/3/4 Animation.Lerp(...);
string Animation.MorphPathData(string? from, string? to, float t);

var tween = new Animation.Tween(0.4f, Ease.CubicOut, loop: false, pingPong: false);
float v = tween.Update(deltaTime, from: 0f, to: 1f);

var spring = new Animation.Spring(value: 0f, stiffness: 150f);
spring.Target = 1f;
float v = spring.Update(deltaTime);   // physically-damped follow, stable under FPS drops
```

### Lists: ItemsControl & templates

Mark a blueprint element's id with `Template` anywhere in it (e.g. `UserCardTemplate`) — the
blueprint itself is never drawn. At runtime it's cloned once per data item, its placeholders are
filled from that item, and the clones stack in order (vertically or horizontally) from the
template's own position, re-spacing themselves as the window resizes.

Bind a list from code with the fluent, typed `Bind<T>` — an optional per-item callback lets you
wire cursors, events, or even per-row animations:

```csharp
public record User(string Name, string Role, string Accent, int Score)
{
    public string Initial => Name[..1].ToUpperInvariant();   // computed members bind too
}

AsItemsControl("UserCardTemplate", Orientation.Vertical, spacing: 10f)
    .Bind(users, (card, user) =>
    {
        card.Cursor = Cursor.Hand;
        card.OnClick += () => Open(user);
        card.OnHoverEnter += () =>
            Animator.Timeline(0.3f, new Step(1.0, new Value(() => card.Scale, 1.03f)));
    });
```

The data source is any `IEnumerable<T>` — plain objects, records, or an `IDictionary` (its keys
act as property names). Re-assigning `ItemsSource` (or calling `Bind` again) regenerates the list.

There are two ways to fill the template, and they combine freely:

**1. Mustache tokens.** Put `{Property}` placeholders anywhere in **text or any attribute**, so a
single element can pull several fields. Nested paths and standard .NET format strings work too:

```xml
<g id="UserCardTemplate">
  <rect x="0" y="0" width="440" height="80" rx="14" style="fill:#1b2230;"/>
  <rect x="0" y="0" width="8"  height="80" rx="4"  style="fill:{Accent};"/>   <!-- attribute -->
  <circle cx="50" cy="40" r="22" style="fill:{Accent};"/>
  <text x="50"  y="48" text-anchor="middle">{Initial}</text>
  <text x="90"  y="34">{Name}</text>                          <!-- one field   -->
  <text x="90"  y="58">{Role} · since {JoinDate:yyyy}</text>  <!-- several + format -->
  <text x="430" y="47" text-anchor="end">{Score}</text>
</g>
```

`{Address.City}` walks nested members; `{Price:0.00}` / `{Date:yyyy-MM-dd}` apply a format. A
token that doesn't resolve is left on screen unchanged, so a typo is visible instead of silently
blanking.

**2. Convention binding by id.** Give a template child the `id` of a property and it's bound
wholesale: a `<text>` gets its content, an `<image>` its `href`, a `bool` toggles the element's
visibility (removed when `false`), and anything else gets its fill color set.

Member lookup is case-insensitive and the resolved accessor is cached per `(type, path)`, so
regenerating a large list doesn't re-run reflection for every field.

### Hot reload

In `DEBUG` builds, a file watcher on your `.svg`/`.aui` triggers a full rebuild of the control
tree in place as soon as you save — no app restart. Controls are recycled by id and type, so
event subscriptions, running animations, and `ItemsControl` bindings all survive. Changing the
`.svg` alone preserves runtime state you've set from code (e.g. an in-progress fade); changing
`.aui` reapplies its declared attributes. Override `OnHotReload()` to react to a reload (e.g. to
reset intro-animation state). This is compiled out entirely in Release builds.

### Window configuration

Set window properties from code (in your constructor, after `base(...)`) or declaratively on the
`.aui` root — whichever's set wins, code included:

```xml
<Window Svg="MainWindow.svg" Title="My App" Width="1280" Height="720"
        WindowStyle="None" WindowStartupLocation="CenterScreen"
        HorizontalAlignment="Center" VerticalAlignment="Center" Stretch="Uniform">
```

```csharp
public string Title { get; set; }
public int Width, Height { get; set; }             // live — assigning resizes the real window
public WindowStyle WindowStyle { get; set; }        // Window | None (borderless, transparent)
public WindowStartupLocation WindowStartupLocation { get; set; }  // Manual | CenterScreen
public string? IconPath { get; set; }               // embedded resource name, or a disk path
public void Close();
public void Minimize();
public void ToggleMaximize();
public void DragWindow();       // call from an OnPress handler for custom-chrome window drag
```

`WindowStyle.None` gives you a borderless, transparent-framebuffer window for fully custom chrome
— pair it with `Background.OnPress += DragWindow` on a hit-testable background shape.

### Rendering options

```csharp
var options = new RenderOptions
{
    Antialiasing = true,
    MsaaSamples = 8,          // ignored when Antialiasing is false
    RenderScale = 2.0,        // supersampling factor, clamped to 1.0–4.0
    FilterQuality = new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell),
};
Window.Initialize(new MainWindow(), options);
Window.Run();
```

`RenderScale > 1` renders into a larger offscreen surface and downsamples with a cubic filter —
crisp edges at the cost of GPU fill rate.

Custom immediate-mode drawing on top of (or underneath) the `.aui` content is available by
overriding `OnFrame(FrameContext ctx)` / `OnFrameBefore(FrameContext ctx)` and drawing straight to
`ctx.Canvas` (a raw `SkiaSharp.SKCanvas`).

### XML IntelliSense

The generator writes an `AurevonUI.xsd` next to your `.aui` file, referencing every element id and
attribute currently valid for your SVG. Reference it from the `.aui` root and most editors
(Visual Studio, Rider, VS Code) will autocomplete element and attribute names as you type:

```xml
<Window xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
        xsi:noNamespaceSchemaLocation="AurevonUI.xsd" Svg="MainWindow.svg">
```

---

## Publishing (single-file & trimming)

Every UI asset — SVG, `.aui`, fonts, and the window icon — is loaded from **embedded resources**
at runtime (disk is only a `DEBUG` convenience for hot reload), so a published app carries no loose
asset files. Bundle the managed code into a single self-contained executable with:

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

This produces **one managed `.exe`** next to the three native libraries `libSkiaSharp.dll`,
`libHarfBuzzSharp.dll`, and `glfw3.dll` — ship the exe together with those files.

> **Don't add `-p:IncludeNativeLibrariesForSelfExtract=true`.** It would fold the native libs into
> the exe (extracted to a temp dir at launch), but Silk.NET uses its own native-library loader that
> only probes next to the executable — so a fully-bundled build fails at startup with
> *"Couldn't find a suitable window platform (GlfwPlatform — not applicable)."* Keeping the native
> `.dll`s on disk beside the exe is the reliable Silk.NET layout.

Single-file publish has an SDK quirk: **re-publishing into a folder that already contains a build
drops those loose native libs** and doesn't put them back, so the next run crashes in SkiaSharp's
type initializer. Add this target to your app `.csproj` so every publish re-lays the natives from
the build output — it makes a plain `dotnet publish` reliable, and strips the giant native `.pdb`s
(`libSkiaSharp.pdb` alone is ~84 MB) at the same time:

```xml
<Target Name="EnsurePublishNatives" AfterTargets="Publish">
  <ItemGroup>
    <_NativeLib Include="$(OutDir)lib*.dll;$(OutDir)glfw*.dll" />
    <_PublishPdb Include="$(PublishDir)**\*.pdb" />
  </ItemGroup>
  <Copy SourceFiles="@(_NativeLib)" DestinationFolder="$(PublishDir)" SkipUnchangedFiles="false" />
  <Delete Files="@(_PublishPdb)" />
</Target>
```

### Trimming

AurevonUI is marked `IsTrimmable` — its animation bindings compile through expression trees (which
root the members they touch) and its data-binding reflection is annotated, so the linker keeps
exactly what your bindings reach. Turn trimming on in your **app** `.csproj` — keep it there rather
than on the command line, so the `netstandard2.0` source generator is never fed a trim flag:

```xml
<PropertyGroup>
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>partial</TrimMode>
  <!-- Do NOT mark the app assembly IsTrimmable: your event-handler methods (Click="OnLogoClick",
       …) are invoked by reflection from the .aui, and trimming would strip them. The app is tiny,
       so there's nothing to gain. Trimming still applies to the BCL and to AurevonUI itself. -->
</PropertyGroup>

<!-- Root the reflection / native-interop assemblies so trimming can't strip what they load at
     runtime. Silk.NET 2.x is NOT reliably trim-safe (Silk.NET.Core loads native libs and resolves
     GL entry points by reflection; windowing/input create their backend via Activator), so root
     the whole family. Svg.Model resolves TypeConverters. -->
<ItemGroup>
  <TrimmerRootAssembly Include="Silk.NET.Core" />
  <TrimmerRootAssembly Include="Silk.NET.Maths" />
  <TrimmerRootAssembly Include="Silk.NET.OpenGL" />
  <TrimmerRootAssembly Include="Silk.NET.Windowing.Common" />
  <TrimmerRootAssembly Include="Silk.NET.Windowing.Glfw" />
  <TrimmerRootAssembly Include="Silk.NET.Input.Common" />
  <TrimmerRootAssembly Include="Silk.NET.Input.Glfw" />
  <TrimmerRootAssembly Include="Silk.NET.GLFW" />
  <TrimmerRootAssembly Include="Svg.Model" />
  <TrimmerRootAssembly Include="Svg.Custom" />
</ItemGroup>
```

`partial` mode trims the BCL and AurevonUI (plus SkiaSharp and Svg.Skia, which declare themselves
trim-safe) while leaving your app and the rooted reflection-driven assemblies intact — taking the
managed executable from ~90 MB down to ~16 MB. Because `PublishTrimmed`/`TrimMode` only apply on
`publish`, your debug/F5 loop is unaffected.

> Two gotchas when trimming Silk.NET: it must be **rooted whole** (the assembly *names* differ from
> the package names — there is no bare `Silk.NET.Windowing` assembly, only `.Common`/`.Glfw`), and a
> residual `IL2104` note may still print for it — that's informational, the assemblies are kept
> intact. **Always smoke-test a trimmed build before shipping.**

---

## Full example

```xml
<!-- MainWindow.aui -->
<Window xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
        xsi:noNamespaceSchemaLocation="AurevonUI.xsd" Svg="MainWindow.svg"
        HorizontalAlignment="Center" VerticalAlignment="Center" Stretch="Uniform">
  <Background StretchToWindow="True" Press="DragWindow" />
  <WindowButtons VerticalAlignment="Top" HorizontalAlignment="Right" Margin="10">
    <CloseButton Cursor="Hand" Click="Close" />
    <MinimizeButton Cursor="Hand" Click="Minimize" />
  </WindowButtons>
  <APP HorizontalAlignment="Center" VerticalAlignment="Center">
    <Logo Cursor="Hand" HoverEnter="OnLogoHoverEnter" HoverLeave="OnLogoHoverLeave" Click="OnLogoClick" />
  </APP>
  <BottomPanel VerticalAlignment="Bottom" HorizontalAlignment="Center" Margin="0,0,0,20">
    <InstallButton Cursor="Hand" HoverEnter="OnInstallHoverEnter" HoverLeave="OnInstallHoverLeave" />
    <ProgressBar><ProgressValueBar Margin="2" MarginPercent="-1,-1,40,-1" /></ProgressBar>
  </BottomPanel>
</Window>
```

```csharp
// MainWindow.cs
using AurevonUI;

public partial class MainWindow : AuiWindow
{
    public MainWindow() : base("MainWindow.aui")
    {
        Title = "AurevonUI — Demo";
        WindowStyle = WindowStyle.None;

        Animator.Timeline(1f, Ease.CubicOut, delay: 1f,
            new Step(0.0, new Value(() => Logo.Opacity, 0f)),
            new Step(1.0, new Value(() => Logo.Opacity, 1f)));
    }

    private void OnLogoHoverEnter()
        => Animator.Timeline(0.6f, new Step(1.0, new Value(() => Logo.StrokeWidth, 30f)));

    private void OnLogoHoverLeave()
        => Animator.Timeline(0.6f, new Step(1.0, new Value(() => Logo.StrokeWidth, 20f)));

    private void OnLogoClick()
        => Animator.Timeline(0.4f, Ease.CubicIn, new Step(1.0, new Value(() => Logo.StrokeWidth, 0f)));

    private void OnInstallHoverEnter()
        => Animator.Timeline(0.6f, new Step(1.0, new Value(() => InstallButton.Scale, 1.02f)));

    private void OnInstallHoverLeave()
        => Animator.Timeline(0.6f, new Step(1.0, new Value(() => InstallButton.Scale, 1.0f)));
}
```

```csharp
// Program.cs
using AurevonUI;

FrameContext.LoadEmbeddedFonts();
Window.Initialize(new MainWindow(), new RenderOptions { Antialiasing = true, RenderScale = 2.0 });
Window.Run();
```

---

## Known limitations

- `Value<T>` interpolates `float`, `double`, `int`, `Thickness`, `Color`, and `Vector2/3/4`.
  Any other `T` falls back to a hard cutover at the 50% mark rather than throwing — worth knowing
  before animating a custom struct.
- Struct-mutation bindings (`() => Control.Margin.Left`) reach exactly **one** level into a
  struct-returning property; a deeper struct-in-struct chain isn't supported.
- Template **mustache tokens** bind top-level members and one-level-and-deeper nested paths, but a
  *nested* path (`{Address.City}`) reaches into a type the `Bind<T>` trim annotation doesn't cover.
  Top-level tokens are always safe under trimming; if you trim **and** use nested tokens, keep the
  nested model type from being trimmed (root its assembly, or don't mark it trimmable).
- Hot reload is compiled out of Release builds by design (it's a development-time convenience).

## License

MIT © Dominik Erdinger — WertexDigital
