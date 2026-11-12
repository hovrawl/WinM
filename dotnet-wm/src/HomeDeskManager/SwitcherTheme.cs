using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace HomeDeskManager;

internal enum SwitcherThemeMode
{
    System,
    Light,
    Dark
}

internal sealed record SwitcherPalette(
    Color Canvas,
    Color Surface,
    Color SoftSurface,
    Color Border,
    Color Text,
    Color MutedText,
    Color Accent,
    Color AccentSoft,
    Color Warning,
    Color AccentText,
    Color ShapeCanvas,
    Color ShapeBorder,
    Color[] WindowCards,
    Color[] WindowShapes);

internal static class SwitcherTheme
{
    private static readonly SwitcherPalette LightPalette = new(
        Color.Parse("#F4F1EB"), Color.Parse("#FFFEFC"), Color.Parse("#EEEAE2"), Color.Parse("#DED9CF"),
        Color.Parse("#292823"), Color.Parse("#77746C"), Color.Parse("#496F58"), Color.Parse("#E8EFE8"),
        Color.Parse("#A85F3D"), Color.Parse("#FFFEFC"), Color.Parse("#EEEAE2"), Color.Parse("#DED9CF"),
        [Color.Parse("#E8EFE8"), Color.Parse("#EEEAE2"), Color.Parse("#E9E2D6")],
        [Color.Parse("#496F58"), Color.Parse("#A85F3D"), Color.Parse("#AABDD1"), Color.Parse("#C2AFCB")]);

    private static readonly SwitcherPalette DarkPalette = new(
        Color.Parse("#171A1D"), Color.Parse("#22272B"), Color.Parse("#282E32"), Color.Parse("#394146"),
        Color.Parse("#F2F1EC"), Color.Parse("#9BA5A8"), Color.Parse("#98B69F"), Color.Parse("#2E3A34"),
        Color.Parse("#D0B99A"), Color.Parse("#17201A"), Color.Parse("#141719"), Color.Parse("#D9E5DC"),
        [Color.Parse("#DCE7DE"), Color.Parse("#E9E2D6"), Color.Parse("#DCE4E9")],
        [Color.Parse("#A9C6B0"), Color.Parse("#D0B99A"), Color.Parse("#AABDD1"), Color.Parse("#C2AFCB")]);

    private static readonly SolidColorBrush[] WindowCardBrushes = CreateBrushes(DarkPalette.WindowCards);
    private static readonly SolidColorBrush[] WindowShapeBrushes = CreateBrushes(DarkPalette.WindowShapes);

    public static SolidColorBrush Canvas { get; } = new(DarkPalette.Canvas);
    public static SolidColorBrush Surface { get; } = new(DarkPalette.Surface);
    public static SolidColorBrush SoftSurface { get; } = new(DarkPalette.SoftSurface);
    public static SolidColorBrush Border { get; } = new(DarkPalette.Border);
    public static SolidColorBrush Text { get; } = new(DarkPalette.Text);
    public static SolidColorBrush MutedText { get; } = new(DarkPalette.MutedText);
    public static SolidColorBrush Accent { get; } = new(DarkPalette.Accent);
    public static SolidColorBrush AccentSoft { get; } = new(DarkPalette.AccentSoft);
    public static SolidColorBrush Warning { get; } = new(DarkPalette.Warning);
    public static SolidColorBrush AccentText { get; } = new(DarkPalette.AccentText);
    public static SolidColorBrush ShapeCanvas { get; } = new(DarkPalette.ShapeCanvas);
    public static SolidColorBrush ShapeBorder { get; } = new(DarkPalette.ShapeBorder);
    public static IReadOnlyList<SolidColorBrush> WindowCardPalette => WindowCardBrushes;
    public static IReadOnlyList<SolidColorBrush> WindowShapePalette => WindowShapeBrushes;

    private static SwitcherThemeMode preference = SwitcherThemeMode.System;

    public static SwitcherThemeMode Preference => preference;

    public static void Initialize(Application application)
    {
        application.ActualThemeVariantChanged += (_, _) =>
        {
            if (preference == SwitcherThemeMode.System)
                ApplyPalette(ResolvePalette(preference, application.ActualThemeVariant));
        };
        SetPreference(application, SwitcherThemeMode.System);
    }

    public static void SetPreference(Application application, SwitcherThemeMode mode)
    {
        preference = mode;
        application.RequestedThemeVariant = mode switch
        {
            SwitcherThemeMode.System => ThemeVariant.Default,
            SwitcherThemeMode.Light => ThemeVariant.Light,
            SwitcherThemeMode.Dark => ThemeVariant.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        ApplyPalette(ResolvePalette(mode, application.ActualThemeVariant));
    }

    internal static SwitcherPalette ResolvePalette(SwitcherThemeMode mode, ThemeVariant actualThemeVariant) =>
        mode == SwitcherThemeMode.Dark ||
        mode == SwitcherThemeMode.System && actualThemeVariant == ThemeVariant.Dark
            ? DarkPalette
            : LightPalette;

    internal static void ApplyPalette(SwitcherPalette palette)
    {
        Canvas.Color = palette.Canvas;
        Surface.Color = palette.Surface;
        SoftSurface.Color = palette.SoftSurface;
        Border.Color = palette.Border;
        Text.Color = palette.Text;
        MutedText.Color = palette.MutedText;
        Accent.Color = palette.Accent;
        AccentSoft.Color = palette.AccentSoft;
        Warning.Color = palette.Warning;
        AccentText.Color = palette.AccentText;
        ShapeCanvas.Color = palette.ShapeCanvas;
        ShapeBorder.Color = palette.ShapeBorder;
        ApplyBrushes(WindowCardBrushes, palette.WindowCards);
        ApplyBrushes(WindowShapeBrushes, palette.WindowShapes);
    }

    public static Border Card(Control content, Thickness? padding = null) => new()
    {
        Background = Surface,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = padding ?? new Thickness(16),
        Child = content
    };

    public static TextBlock Heading(string text, double size = 16) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        Foreground = Text
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = MutedText,
        TextWrapping = TextWrapping.Wrap
    };

    public static Button Action(string text, bool primary = false) => new()
    {
        Content = text,
        Padding = new Thickness(12, 8),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = primary ? Accent : Surface,
        Foreground = primary ? AccentText : Text,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7)
    };

    private static SolidColorBrush[] CreateBrushes(IReadOnlyList<Color> colors) =>
        colors.Select(color => new SolidColorBrush(color)).ToArray();

    private static void ApplyBrushes(IReadOnlyList<SolidColorBrush> brushes, IReadOnlyList<Color> colors)
    {
        for (var index = 0; index < brushes.Count; index++) brushes[index].Color = colors[index];
    }
}
