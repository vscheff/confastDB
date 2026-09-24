using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Confast.Web.Features.Chat;

public sealed record ChatEmojiOption(string Emoji, string Name, string Group, string Subgroup, double Version);
public sealed record ChatEmojiFamily(ChatEmojiOption Default, IReadOnlyList<ChatEmojiOption> Tones);

public static partial class ChatEmojiCatalog
{
    public static readonly IReadOnlyList<ChatEmojiOption> All = Load();
    public static readonly IReadOnlyList<ChatEmojiFamily> Families = All
        .GroupBy(x => FamilyKey(x.Emoji), StringComparer.Ordinal)
        .Select(CreateFamily)
        .ToArray();
    private static readonly IReadOnlyDictionary<string, ChatEmojiFamily> FamiliesByDefault = Families
        .ToDictionary(x => x.Default.Emoji, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, ChatEmojiFamily> FamiliesByEmoji = Families
        .SelectMany(family => family.Tones.Select(tone => (tone.Emoji, family)))
        .ToDictionary(x => x.Emoji, x => x.family, StringComparer.Ordinal);
    public static readonly IReadOnlyList<string> Groups = All.Select(x => x.Group).Distinct().ToArray();
    public static readonly IReadOnlyList<string> CompositeCandidates = Families
        .SelectMany(x => x.Tones)
        .Where(x => x.Emoji.Contains('\u200D') || x.Group == "Flags")
        .Select(x => x.Emoji).Distinct(StringComparer.Ordinal).ToArray();

    public static ChatEmojiFamily? FindFamily(string defaultEmoji) =>
        FamiliesByDefault.GetValueOrDefault(defaultEmoji);

    public static ChatEmojiFamily? FindFamilyForEmoji(string emoji) =>
        FamiliesByEmoji.GetValueOrDefault(emoji);

    public static bool IsToneOf(string defaultEmoji, string emoji) =>
        FindFamily(defaultEmoji)?.Tones.Any(x => x.Emoji == emoji) == true;

    private static string WithoutSkinTone(string emoji) => string.Concat(emoji.EnumerateRunes()
        .Where(rune => rune.Value is < 0x1F3FB or > 0x1F3FF)
        .Select(rune => rune.ToString()));

    private static string FamilyKey(string emoji) => WithoutSkinTone(emoji).Replace("\uFE0F", "", StringComparison.Ordinal);

    private static ChatEmojiFamily CreateFamily(IGrouping<string, ChatEmojiOption> group)
    {
        var tones = group.ToList();
        var neutral = tones.FirstOrDefault(x => x.Emoji == WithoutSkinTone(x.Emoji));
        if (neutral is null)
        {
            // Some mixed-tone sequences have no neutral row in emoji-test.txt.
            var first = tones[0];
            neutral = new ChatEmojiOption(WithoutSkinTone(first.Emoji),
                first.Name.Split(':')[0], first.Group, first.Subgroup, first.Version);
            tones.Insert(0, neutral);
        }
        return new ChatEmojiFamily(neutral, tones);
    }

    public static IReadOnlyList<string> Subgroups(string group) => All
        .Where(x => x.Group == group).Select(x => x.Subgroup).Distinct().ToArray();

    private static ChatEmojiOption[] Load()
    {
        using var stream = typeof(ChatEmojiCatalog).Assembly.GetManifestResourceStream(
            "Confast.Web.Features.Chat.emoji-test-source.txt")
            ?? throw new InvalidOperationException("The Unicode emoji catalog is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var options = new List<ChatEmojiOption>();
        var group = string.Empty;
        var subgroup = string.Empty;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# group: ", StringComparison.Ordinal))
            {
                group = line[9..];
                continue;
            }
            if (line.StartsWith("# subgroup: ", StringComparison.Ordinal))
            {
                subgroup = line[12..];
                continue;
            }
            if (!line.Contains("; fully-qualified", StringComparison.Ordinal)) continue;
            var match = EmojiLine().Match(line);
            if (!match.Success) throw new InvalidOperationException("The Unicode emoji catalog has an unexpected format.");
            var emoji = string.Concat(match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(hex => char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
            options.Add(new ChatEmojiOption(emoji, match.Groups[3].Value, group, subgroup,
                double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
        }
        return options.ToArray();
    }

    [GeneratedRegex(@"^([0-9A-F ]+)\s*; fully-qualified\s*#\s*\S+\s+E([0-9.]+)\s+(.+)$")]
    private static partial Regex EmojiLine();
}
