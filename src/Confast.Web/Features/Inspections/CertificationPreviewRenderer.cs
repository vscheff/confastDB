using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Confast.Web.Features.Inspections;

public sealed class CertificationPreviewOptions
{
    public string RendererPath { get; set; } = "pdftoppm";

    public int ResolutionDpi { get; set; } = 150;

    public int MaximumPages { get; set; } = 50;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class CertificationPreviewRenderer(
    CertificationPreviewOptions options,
    ILogger<CertificationPreviewRenderer> logger)
{
    public async Task<byte[]?> RenderAsync(
        byte[] originalPdf,
        CancellationToken cancellationToken = default)
    {
        if (originalPdf.Length == 0
            || options.ResolutionDpi <= 0
            || options.MaximumPages <= 0
            || options.Timeout <= TimeSpan.Zero)
        {
            return null;
        }

        var workDirectory = Path.Combine(
            Path.GetTempPath(),
            $"confast-cert-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        var inputPath = Path.Combine(workDirectory, "input.pdf");
        var outputPrefix = Path.Combine(workDirectory, "page");
        await File.WriteAllBytesAsync(inputPath, originalPdf, cancellationToken);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = options.RendererPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-jpeg");
            process.StartInfo.ArgumentList.Add("-r");
            process.StartInfo.ArgumentList.Add(options.ResolutionDpi.ToString(CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("-jpegopt");
            process.StartInfo.ArgumentList.Add("quality=85");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-l");
            process.StartInfo.ArgumentList.Add(options.MaximumPages.ToString(CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add(inputPath);
            process.StartInfo.ArgumentList.Add(outputPrefix);

            try
            {
                process.Start();
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                logger.LogWarning(exception, "Unable to start the certification PDF preview renderer {RendererPath}.", options.RendererPath);
                return null;
            }

            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                logger.LogWarning("Certification PDF preview rendering timed out or was cancelled.");
                return null;
            }

            var error = await errorTask;
            await outputTask;
            if (process.ExitCode != 0)
            {
                logger.LogWarning(
                    "Certification PDF preview renderer exited with code {ExitCode}: {Error}",
                    process.ExitCode,
                    error.Trim());
                return null;
            }

            var pagePaths = Directory.GetFiles(workDirectory, "page-*.jpg")
                .OrderBy(GetPageNumber)
                .Take(options.MaximumPages)
                .ToArray();
            if (pagePaths.Length == 0)
            {
                logger.LogWarning("Certification PDF preview renderer produced no pages.");
                return null;
            }

            var pages = new List<RasterizedPdfPage>(pagePaths.Length);
            foreach (var pagePath in pagePaths)
            {
                var jpeg = await File.ReadAllBytesAsync(pagePath, cancellationToken);
                var dimensions = JpegDimensions.Read(jpeg);
                pages.Add(new RasterizedPdfPage(jpeg, dimensions.Width, dimensions.Height));
            }

            return RasterizedPdfBuilder.Build(pages, options.ResolutionDpi);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            logger.LogWarning(exception, "Certification PDF preview rendering failed.");
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "Unable to remove temporary certification preview directory.");
            }
        }
    }

    private static int GetPageNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(name.AsSpan("page-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MaxValue;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed record RasterizedPdfPage(byte[] Jpeg, int Width, int Height);

internal static class RasterizedPdfBuilder
{
    public static byte[] Build(IReadOnlyList<RasterizedPdfPage> pages, int resolutionDpi)
    {
        if (pages.Count == 0 || resolutionDpi <= 0)
        {
            throw new ArgumentException("At least one page and a positive resolution are required.");
        }

        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.4\n%\xFF\xFF\xFF\xFF\n");
        var objectOffsets = new List<long> { 0 };
        WriteObject(output, objectOffsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");

        var kids = string.Join(
            " ",
            Enumerable.Range(0, pages.Count).Select(index => $"{3 + index * 3} 0 R"));
        WriteObject(output, objectOffsets, 2, $"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");

        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            var pageObject = 3 + index * 3;
            var contentObject = pageObject + 1;
            var imageObject = pageObject + 2;
            var widthPoints = page.Width * 72d / resolutionDpi;
            var heightPoints = page.Height * 72d / resolutionDpi;
            var content = $"q\n{widthPoints.ToString("0.###", CultureInfo.InvariantCulture)} 0 0 {heightPoints.ToString("0.###", CultureInfo.InvariantCulture)} 0 0 cm\n/Im0 Do\nQ\n";

            WriteObject(
                output,
                objectOffsets,
                pageObject,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPoints.ToString("0.###", CultureInfo.InvariantCulture)} {heightPoints.ToString("0.###", CultureInfo.InvariantCulture)}] /Resources << /XObject << /Im0 {imageObject} 0 R >> >> /Contents {contentObject} 0 R >>");
            WriteStreamObject(output, objectOffsets, contentObject, Encoding.ASCII.GetBytes(content), null);
            WriteStreamObject(
                output,
                objectOffsets,
                imageObject,
                page.Jpeg,
                $"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode >>");
        }

        var xrefOffset = output.Position;
        WriteAscii(output, $"xref\n0 {objectOffsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in objectOffsets.Skip(1))
        {
            WriteAscii(output, $"{offset:0000000000} 00000 n \n");
        }

        WriteAscii(
            output,
            $"trailer\n<< /Size {objectOffsets.Count} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static void WriteObject(MemoryStream output, List<long> offsets, int number, string body)
    {
        EnsureObjectSlot(offsets, number);
        offsets[number] = output.Position;
        WriteAscii(output, $"{number} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteStreamObject(
        MemoryStream output,
        List<long> offsets,
        int number,
        byte[] data,
        string? dictionary)
    {
        EnsureObjectSlot(offsets, number);
        offsets[number] = output.Position;
        var prefix = dictionary is null
            ? $"<< /Length {data.Length} >>"
            : $"{dictionary.TrimEnd('>')} /Length {data.Length} >>";
        WriteAscii(output, $"{number} 0 obj\n{prefix}\nstream\n");
        output.Write(data);
        WriteAscii(output, "\nendstream\nendobj\n");
    }

    private static void EnsureObjectSlot(List<long> offsets, int number)
    {
        while (offsets.Count <= number)
        {
            offsets.Add(0);
        }
    }

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));
}

internal static class JpegDimensions
{
    public static (int Width, int Height) Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException("Renderer output was not a JPEG file.");
        }

        var position = 2;
        while (position + 8 < jpeg.Length)
        {
            while (position < jpeg.Length && jpeg[position] != 0xFF)
            {
                position++;
            }

            while (position < jpeg.Length && jpeg[position] == 0xFF)
            {
                position++;
            }

            if (position >= jpeg.Length)
            {
                break;
            }

            var marker = jpeg[position++];
            if (marker is 0xD8 or 0xD9)
            {
                continue;
            }

            if (position + 2 > jpeg.Length)
            {
                break;
            }

            var length = (jpeg[position] << 8) | jpeg[position + 1];
            if (length < 2 || position + length > jpeg.Length)
            {
                break;
            }

            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                var height = (jpeg[position + 3] << 8) | jpeg[position + 4];
                var width = (jpeg[position + 5] << 8) | jpeg[position + 6];
                return (width, height);
            }

            position += length;
        }

        throw new InvalidDataException("JPEG dimensions could not be read.");
    }
}
