# AurevonUI Templates

`dotnet new` templates for [AurevonUI](https://github.com/EvolveSoftwares/AurevonUI) — a
cross-platform .NET UI toolkit that renders its entire interface from SVG.

## Install

```bash
dotnet new install AurevonUI.Templates
```

## Create an app

```bash
dotnet new aurevonui-app -n MyApp
cd MyApp
dotnet run
```

You get a ready-to-run window with `MainWindow.svg` (the design), `MainWindow.aui` (interactivity),
and typed C# code-behind — plus a `.csproj` already configured for embedded assets, trimming, and
single-file publishing.

## Add another window

Run inside an existing project:

```bash
dotnet new aurevonui-window -n SettingsWindow
```

Adds `SettingsWindow.svg`, `SettingsWindow.aui`, and `SettingsWindow.cs`, wired to your project's
root namespace. Works in any subfolder — the app template's asset globs are recursive.

## Templates in this package

| Short name | Type | Description |
|---|---|---|
| `aurevonui-app` | Project | Cross-platform desktop app rendered from an SVG design. |
| `aurevonui-window` | Item | An AurevonUI window: SVG + `.aui` overlay + typed code-behind. |

Both also show up in the Visual Studio **New Project** / **Add New Item** dialogs.

## Update / uninstall

```bash
dotnet new update
dotnet new uninstall AurevonUI.Templates
```

## Documentation

Full guide is in the [main repository](https://github.com/EvolveSoftwares/AurevonUI).

## License

MIT © Dominik Erdinger — WertexDigital
