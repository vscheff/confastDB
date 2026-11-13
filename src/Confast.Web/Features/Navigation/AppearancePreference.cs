using System.Collections.Immutable;

namespace Confast.Web.Features.Navigation;

internal enum AppearanceMode { System, Light, Dark }

internal sealed record AppearancePreference(AppearanceMode Mode, string AccentColor)
{
    internal const string DefaultAccent = "#176B9C";
    internal static AppearancePreference Default { get; } = new(AppearanceMode.System, DefaultAccent);

    internal static ImmutableArray<(string Name, string Color)> Presets { get; } =
    [
        ("Classic Blue", DefaultAccent),
        ("Teal", "#0F766E"),
        ("Purple", "#7C3AED"),
        ("Orchid", "#C026D3"),
        ("Pink", "#DB2777"),
        ("Burnt Orange", "#C2410C"),
        ("Slate", "#64748B")
    ];

    internal string ModeName => Mode.ToString().ToLowerInvariant();

    internal static AppearancePreference FromStored(string? mode, string? accent) =>
        new(mode?.Trim().ToLowerInvariant() switch
        {
            "light" => AppearanceMode.Light,
            "dark" => AppearanceMode.Dark,
            _ => AppearanceMode.System
        }, NormalizeAccent(accent) ?? DefaultAccent);

    internal static string? NormalizeAccent(string? value)
    {
        var digits = value?.Trim();
        if (digits is null) return null;
        if (digits.StartsWith('#')) digits = digits[1..];
        if (digits.Length is not (3 or 6) || !digits.All(Uri.IsHexDigit))
            return null;

        digits = digits.ToUpperInvariant();
        return digits.Length == 3
            ? $"#{digits[0]}{digits[0]}{digits[1]}{digits[1]}{digits[2]}{digits[2]}"
            : "#" + digits;
    }
}
