using System.Text;

namespace Confast.Web.Features.Chat;

public sealed record ChatMentionTargets(IReadOnlySet<string> Users, bool Everyone, bool Here);
public enum ChatMentionKind { User, Everyone, Here }
public sealed record ChatMentionToken(int Start, int Length, ChatMentionKind Kind, string? UserId, string? Name);
public sealed record ChatMessagePart(string Text, bool IsTag, string? UserId = null, string? Name = null);

public static class ChatMentionParser
{
    public static string TagForMember(ChatUser member, IReadOnlyList<ChatUser> members)
    {
        var ambiguous = members.Any(x => x.Id != member.Id &&
            (x.Name.Equals(member.Name, StringComparison.OrdinalIgnoreCase)
                || (x.UserName?.Equals(member.Name, StringComparison.OrdinalIgnoreCase) ?? false)));
        var reserved = member.Name.Equals("everyone", StringComparison.OrdinalIgnoreCase)
            || member.Name.Equals("here", StringComparison.OrdinalIgnoreCase);
        return ambiguous || reserved ? member.UserName ?? member.Name : member.Name;
    }

    public static ChatMentionTargets Find(string body, IReadOnlyList<ChatUser> members)
    {
        var users = new HashSet<string>(StringComparer.Ordinal);
        var everyone = false;
        var here = false;
        foreach (var token in FindTokens(body, members))
        {
            switch (token.Kind)
            {
                case ChatMentionKind.User:
                    users.Add(token.UserId!);
                    break;
                case ChatMentionKind.Everyone:
                    everyone = true;
                    break;
                case ChatMentionKind.Here:
                    here = true;
                    break;
            }
        }
        return new ChatMentionTargets(users, everyone, here);
    }

    public static string NormalizeTags(string body, IReadOnlyList<ChatUser> members)
    {
        var tokens = FindTokens(body, members);
        if (tokens.Count == 0) return body;

        var normalized = new StringBuilder(body.Length);
        var cursor = 0;
        foreach (var token in tokens)
        {
            normalized.Append(body, cursor, token.Start - cursor);
            var tag = token.Kind switch
            {
                ChatMentionKind.Everyone => "everyone",
                ChatMentionKind.Here => "here",
                _ => TagForMember(members.First(member => member.Id == token.UserId), members)
            };
            normalized.Append('@').Append(tag);
            cursor = token.Start + token.Length;
        }
        normalized.Append(body, cursor, body.Length - cursor);
        return normalized.ToString();
    }

    public static IReadOnlyList<ChatMessagePart> GetParts(string body, IReadOnlyList<ChatMentionToken> tokens)
    {
        var parts = new List<ChatMessagePart>();
        var cursor = 0;
        foreach (var token in tokens.OrderBy(x => x.Start))
        {
            if (token.Start < cursor || token.Length < 1 || token.Start + token.Length > body.Length) continue;
            if (token.Start > cursor) parts.Add(new ChatMessagePart(body[cursor..token.Start], false));
            parts.Add(new ChatMessagePart(body.Substring(token.Start, token.Length), true, token.UserId, token.Name));
            cursor = token.Start + token.Length;
        }
        if (cursor < body.Length) parts.Add(new ChatMessagePart(body[cursor..], false));
        return parts;
    }

    public static IReadOnlyList<ChatMentionToken> FindTokens(string body, IReadOnlyList<ChatUser> members)
    {
        var tokens = new List<ChatMentionToken>();
        var aliases = members.Select(x => (Alias: TagForMember(x, members), UserId: x.Id))
            .Where(x => x.Alias.Length > 0
                && !x.Alias.Equals("everyone", StringComparison.OrdinalIgnoreCase)
                && !x.Alias.Equals("here", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Alias, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Select(alias => alias.UserId).Distinct().Count() == 1)
            .Select(x => x.First())
            .OrderByDescending(x => x.Alias.Length).ToArray();

        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] != '@' || (index > 0 && IsNameCharacter(body[index - 1]))) continue;
            var start = index + 1;
            if (Matches(body, start, "everyone"))
            {
                tokens.Add(new ChatMentionToken(index, "everyone".Length + 1, ChatMentionKind.Everyone, null, null));
                index = start + "everyone".Length - 1;
            }
            else if (Matches(body, start, "here"))
            {
                tokens.Add(new ChatMentionToken(index, "here".Length + 1, ChatMentionKind.Here, null, null));
                index = start + "here".Length - 1;
            }
            else
            {
                foreach (var alias in aliases)
                {
                    if (!Matches(body, start, alias.Alias)) continue;
                    var name = members.First(x => x.Id == alias.UserId).Name;
                    tokens.Add(new ChatMentionToken(index, alias.Alias.Length + 1,
                        ChatMentionKind.User, alias.UserId, name));
                    index = start + alias.Alias.Length - 1;
                    break;
                }
            }
        }
        return tokens;
    }

    private static bool Matches(string body, int start, string alias) =>
        start + alias.Length <= body.Length
        && body.AsSpan(start, alias.Length).Equals(alias.AsSpan(), StringComparison.OrdinalIgnoreCase)
        && (start + alias.Length == body.Length || !IsNameCharacter(body[start + alias.Length]));

    private static bool IsNameCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
