using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace HomeDesk.Ui;

public static class DeskTheme
{
    public static readonly IBrush Canvas = Brush("#F4F1EB");
    public static readonly IBrush Surface = Brush("#FFFEFC");
    public static readonly IBrush SoftSurface = Brush("#EEEAE2");
    public static readonly IBrush Border = Brush("#DED9CF");
    public static readonly IBrush Text = Brush("#292823");
    public static readonly IBrush MutedText = Brush("#77746C");
    public static readonly IBrush Accent = Brush("#496F58");
    public static readonly IBrush AccentSoft = Brush("#E8EFE8");
    public static readonly IBrush Warning = Brush("#A85F3D");
    public static readonly IBrush Error = Brush("#A9443B");

    public static void Apply(Application application)
    {
        application.Styles.Add(new FluentTheme());
        application.RequestedThemeVariant = ThemeVariant.Light;
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
        Foreground = primary ? Surface : Text,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7)
    };

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}