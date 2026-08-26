namespace Confast.Web.Features.Inspections;

public sealed class CertificationDocument
{
    public long Id { get; set; }

    public long InspectionCertificationId { get; set; }

    public InspectionCertification InspectionCertification { get; set; } = null!;

    public string OriginalFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];

    public DateTimeOffset UploadedAtUtc { get; set; }

    public uint Version { get; set; }
}
