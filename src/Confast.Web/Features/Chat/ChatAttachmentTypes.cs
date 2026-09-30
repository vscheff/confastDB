using System.Text;

namespace Confast.Web.Features.Chat;

public static class ChatAttachmentTypes
{
    public const int MaximumBytes = 25 * 1024 * 1024;
    public const int MaximumTextBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".log", ".json", ".xml", ".yaml", ".yml",
        ".cs", ".css", ".js", ".html", ".htm", ".sql",
        ".c", ".h", ".cc", ".cpp", ".hpp", ".hh", ".py", ".ts", ".tsx", ".jsx",
        ".java", ".sh", ".ps1", ".go", ".rs", ".rb", ".php", ".toml", ".ini"
    };

    public static bool IsSupportedTextFileName(string fileName) => TextExtensions.Contains(Path.GetExtension(fileName));

    public static (ChatAttachmentKind Kind, string ContentType) Classify(string fileName, byte[] content)
    {
        if (content.Length is < 1 or > MaximumBytes)
            throw new InvalidOperationException("Files must be between 1 byte and 25 MB.");
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            content.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return (ChatAttachmentKind.Image, "image/png");
        if ((extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) &&
            content.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
            return (ChatAttachmentKind.Image, "image/jpeg");
        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) &&
            (content.AsSpan().StartsWith("GIF87a"u8) || content.AsSpan().StartsWith("GIF89a"u8)))
            return (ChatAttachmentKind.Image, "image/gif");
        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) && content.Length >= 12 &&
            content.AsSpan().StartsWith("RIFF"u8) && content.AsSpan(8).StartsWith("WEBP"u8))
            return (ChatAttachmentKind.Image, "image/webp");
        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) && content.Length >= 12 &&
            content.AsSpan(4).StartsWith("ftyp"u8))
            return (ChatAttachmentKind.Video, "video/mp4");
        if (extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) &&
            content.AsSpan().StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }))
            return (ChatAttachmentKind.Video, "video/webm");
        if (IsSupportedTextFileName(fileName) && content.Length <= MaximumTextBytes)
        {
            try
            {
                var text = StrictUtf8.GetString(content);
                if (!text.Contains('\0')) return (ChatAttachmentKind.Text, "text/plain; charset=utf-8");
            }
            catch (DecoderFallbackException) { }
        }
        return (ChatAttachmentKind.Download, "application/octet-stream");
    }

    public static string DecodeText(byte[] content) => StrictUtf8.GetString(content);
}
