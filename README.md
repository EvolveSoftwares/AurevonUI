# AurevonUI

**Cross-platform .NET UI toolkit that renders its entire interface from SVG.**
Design in any vector editor, wire it up with a tiny `.aui` overlay, and get strongly-typed
controls, anchor layout, keyframe animation, and live hot reload — no XAML, no designer tool.

> .NET 10 · rendering via [SkiaSharp](https://github.com/mono/SkiaSharp) over OpenGL
> ([Silk.NET](https://github.com/dotnet/Silk.NET)) · Windows / Linux / macOS

---

## Install

Fastest way — scaffold a ready-to-run app from the templates:

```bash
dotnet new install AurevonUI.Templates
dotnet new aurevonui-app -n MyApp
cd MyApp
dotnet run
```

Need another window later? Run `dotnet new aurevonui-window -n SettingsWindow` inside the project.
Both templates also appear in the Visual Studio **New Project** / **Add New Item** dialogs.

Or add the library to an existing project:

```bash
dotnet add package AurevonUI
```

The library and its Roslyn source generator ship in one package.

## Quick start

Draw your UI in an SVG, giving each interactive element an `id`. Add a matching `MainWindow.aui`:

```xml
<Window Svg="MainWindow.svg" Stretch="Uniform">
  <Background StretchToWindow="True" Press="DragWindow" />
  <Logo Cursor="Hand" HoverEnter="OnLogoHoverEnter" Click="OnLogoClick" />
</Window>
```

Every named element becomes a typed property on your window — no string lookups:

```csharp
using AurevonUI;

public partial class MainWindow : AuiWindow
{
    public MainWindow() : base("MainWindow.aui")
    {
        Title = "My App"; Width = 1280; Height = 720;

        Animator.Timeline(0.8f, Ease.CubicOut, delay: 0.3f,
            new Step(0.0, new Value(() => Logo.Opacity, 0f)),
            new Step(1.0, new Value(() => Logo.Opacity, 1f)));
    }

    void OnLogoHoverEnter() =>
        Animator.Timeline(0.2f, new Step(1.0, new Value(() => Logo.Scale, 1.05f)));
}
```

```csharp
// Program.cs
FrameContext.LoadEmbeddedFonts();
new MainWindow().Run();
```

## Features

- **The SVG *is* the UI** — gradients, clip paths, embedded fonts, and rounded corners render as designed.
- **Typed controls, zero boilerplate** — a source generator turns each named element into a strongly-typed property; rename it in the SVG, it renames in code.
- **Keyframe animation** — animate `Opacity`, `Scale`, `Color`, `Thickness`, path geometry (shape morphing) and more, with automatic interruption and smooth hand-off between running animations.
- **Data-bound lists** — `ItemsControl` templates fill from any object using `{Mustache}` tokens (`{Name}`, `{Address.City}`, `{Price:0.00}`) in text or attributes.
- **Live hot reload** — edit the SVG while the app runs; the control tree rebuilds in place, animations and event subscriptions survive.

```csharp
AsItemsControl("CardTemplate", Orientation.Vertical, spacing: 10f)
    .Bind(users, (card, user) => card.OnClick += () => Open(user));
```

## Publishing

Bundle the managed code into a single self-contained executable (ships beside three native libraries):

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

Trimming is supported (`partial` mode plus rooting the Silk.NET / Svg.Model native-interop
assemblies) — the project template's `.csproj` ships the ready-made config.

## Documentation

Full guide — element types, layout, animation, templates, window styling, rendering options — is in
[`AurevonUI/README.md`](AurevonUI/README.md).

## License

MIT © Dominik Erdinger — WertexDigital
