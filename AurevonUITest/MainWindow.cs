using System;
using System.Collections.Generic;
using AurevonUI;
using AurevonUI.Elements;

namespace AurevonUITest;

public class Member
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Accent { get; set; } = "#4c8dff";
    public int Score { get; set; }

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
}

public partial class MainWindow : AuiWindow
{
    private const string CirclePath =
        "M290,288 C329.77,288 362,320.23 362,360 C362,399.77 329.77,432 290,432 C250.23,432 218,399.77 218,360 C218,320.23 250.23,288 290,288 Z";
    private const string SquarePath =
        "M290,288 C362,288 362,288 362,360 C362,432 362,432 290,432 C218,432 218,432 218,360 C218,288 218,288 290,288 Z";
    private bool _morphed;

    public MainWindow() : base("MainWindow.aui")
    {
        IconPath = "Icon/AUI.png";
        Title = "AurevonUI — Demo";
        Width = 1280;
        Height = 720;
        WindowStyle = WindowStyle.Window;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        APP.Opacity = 0;

        //CloseButton.Cursor = Cursor.Hand;
        //MinimizeButton.Cursor = Cursor.Hand;
        //CloseButton.OnClick += Close;
        //MinimizeButton.OnClick += Minimize;
        Background.OnPress += DragWindow;

        Animator.Timeline(1f, Ease.CubicOut, 1f,
            new Step(0.0, new Value(() => APP.Opacity, 0f)),
            new Step(1.0, new Value(() => APP.Opacity, 1f))
        );

        Animator.Timeline(1f, Ease.CubicOut, 1f,
            new Step(0.0, new Value(() => BAR.Opacity, 0f)),
            new Step(1.0, new Value(() => BAR.Opacity, 0.5f))
        );

        Animator.Timeline(1f, Ease.CubicOut, 0.8f,
            new Step(0.0, new Value(() => Logo.StrokeWidth, 0f)),
            new Step(1.0, new Value(() => Logo.StrokeWidth, 20f))
        );

        var Team = new List<Member>
        {
            new() { Name = "Alice Nováková",  Role = "Lead Designer",       Accent = "#4c8dff", Score = 98 },
            new() { Name = "Bob Dvořák",      Role = "Frontend Engineer",   Accent = "#22c55e", Score = 91 },
            new() { Name = "Cyril Marek",     Role = "Backend Engineer",    Accent = "#f59e0b", Score = 87 },
            new() { Name = "Dana Kučerová",   Role = "Product Manager",     Accent = "#ec4899", Score = 94 },
            new() { Name = "Emil Horák",      Role = "QA Engineer",         Accent = "#a855f7", Score = 82 },
            new() { Name = "Filip Beneš",     Role = "DevOps",              Accent = "#06b6d4", Score = 89 },
        };

        AsItemsControl("UserCardTemplate", Orientation.Vertical, 10f)
            .Bind(Team, (Card, Item) =>
            {
                Card.Cursor = Cursor.Hand;
                Card.OnHoverEnter += () => Animator.Timeline(0.35f, new Step(1.0, new Value(() => Card.Scale, 1.03f)));
                Card.OnHoverLeave += () => Animator.Timeline(0.35f, new Step(1.0, new Value(() => Card.Scale, 1.0f)));
                Card.OnClick += () => Console.WriteLine($"Selected: {Item.Name} — {Item.Role} ({Item.Score})");
            });

        var Morph = MorphShape;
        Morph.Cursor = Cursor.Hand;
        Morph.OnHoverEnter += () => Animator.Timeline(0.4f, Ease.BackOut, new Step(1.0, new Value(() => Morph.Scale, 1.08f)));
        Morph.OnHoverLeave += () => Animator.Timeline(0.4f, new Step(1.0, new Value(() => Morph.Scale, 1.0f)));
        Morph.OnClick += () =>
        {
            _morphed = !_morphed;
            Animator.Timeline(0.7f, Ease.CubicInOut,
                new Step(1.0, new PathValue(() => Morph.PathData, _morphed ? SquarePath : CirclePath)));
        };
    }

    public void OnLogoHoverEnter()
        => Animator.Timeline(0.6f, new Step(1.0, new Value(() => Logo.StrokeWidth, 30f)));

    public void OnLogoHoverLeave()
        => Animator.Timeline(0.6f, new Step(1.0, new Value(() => Logo.StrokeWidth, 20f)));

    public void OnLogoClick()
        => Animator.Timeline(0.4f, Ease.CubicIn, new Step(1.0, new Value(() => Logo.StrokeWidth, 0f)));

    public void OnButtonInstallHoverEnter() => Animator.Timeline(0.6f, new Step(1.0, new Value(() => ButtonInstall.Scale, 1.02f)));

    public void OnButtonInstallHoverLeave()
        => Animator.Timeline(0.6f, new Step(1.0, new Value(() => ButtonInstall.Scale, 1.0f)));

    public void OnButtonInstallClick()
    {
        Animator.Timeline(0.8f, new Step(1.0, new Value(() => Logo.StrokeWidth, ProgressValueBar.MarginPercent.Right == 80 ? 20f : 2f)));
        Animator.Timeline(0.8f, new Step(1.0, new Value(() => ProgressValueBar.MarginPercent.Right, ProgressValueBar.MarginPercent.Right == 80 ? 40.0f : 80.0f)));
    }
}
