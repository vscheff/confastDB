using Confast.Web.Data;
using Confast.Web.Features.Customers;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Inspections;

public static class ShipDateCalculator
{
    public static DateOnly NextWorkDay(DateOnly date)
    {
        do
        {
            date = date.AddDays(1);
        } while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);

        return date;
    }
}

public sealed record CertificationPackageRequest(
    long ExpectedCustomerId,
    IReadOnlyCollection<long> InspectionIds,
    DateOnly ShipDate);

public sealed record CertificationPackageLot(
    long InspectionId,
    string? LotNumber,
    string PartNumber,
    DateOnly InspectionDate,
    IReadOnlyList<string> RequiredCertificationNames,
    IReadOnlyList<string> MissingCertificationNames);

public sealed record CertificationPackage(
    long CustomerId,
    string CustomerName,
    DateOnly ShipDate,
    string FileName,
    byte[] Content,
    IReadOnlyList<CertificationPackageLot> Lots,
    IReadOnlyList<string> ToRecipients,
    IReadOnlyList<string> CcRecipients);

public sealed class CertificationPackageException(string message) : InvalidOperationException(message);

public interface ICertificationPackageService
{
    Task<CertificationPackage> BuildAsync(
        CertificationPackageRequest request,
        string applicationBaseUrl,
        CancellationToken cancellationToken = default);
}

public sealed class CertificationPackageService(
    IDbContextFactory<AppDbContext> contextFactory,
    InspectionPdfRenderer inspectionPdfRenderer,
    PdfDocumentMerger pdfMerger,
    CertificationPackageFilenameFormatter filenameFormatter) : ICertificationPackageService
{
    public async Task<CertificationPackage> BuildAsync(
        CertificationPackageRequest request,
        string applicationBaseUrl,
        CancellationToken cancellationToken = default)
    {
        if (request.InspectionIds.Count == 0)
        {
            throw new CertificationPackageException("Select at least one lot.");
        }

        var ids = request.InspectionIds.Distinct().ToArray();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var customer = await db.Customers
            .AsNoTracking()
            .Where(x => x.Id == request.ExpectedCustomerId)
            .Select(x => new
            {
                x.Id,
                x.Name,
                FilenameTemplate = x.CertificationSettings == null
                    ? null
                    : x.CertificationSettings.MultiLotFilenameTemplate,
                ToRecipients = x.CertificationRecipients
                    .Where(r => r.RecipientType == CertificationRecipientType.To)
                    .OrderBy(r => r.EmailAddress)
                    .Select(r => r.EmailAddress)
                    .ToList(),
                CcRecipients = x.CertificationRecipients
                    .Where(r => r.RecipientType == CertificationRecipientType.Cc)
                    .OrderBy(r => r.EmailAddress)
                    .Select(r => r.EmailAddress)
                    .ToList(),
                RequiredTypeIds = x.CertificationRequirements
                    .Select(r => r.CertificationTypeId)
                    .ToList()
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new CertificationPackageException("The customer could not be found.");

        if (customer.RequiredTypeIds.Count == 0)
        {
            throw new CertificationPackageException("No certification requirements are configured for this customer.");
        }

        var requiredTypes = await db.CertificationTypes
            .AsNoTracking()
            .Where(x => customer.RequiredTypeIds.Contains(x.Id))
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Name, x.DisplayOrder })
            .ToListAsync(cancellationToken);

        var lots = await db.Inspections
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new PackageInspectionRow(
                x.Id,
                x.Part.CustomerId,
                x.LotNumber,
                x.Part.PartNumber,
                x.InspectionDate,
                x.ConformancePoNumber,
                x.Certifications
                    .Where(c => customer.RequiredTypeIds.Contains(c.CertificationTypeId))
                    .SelectMany(c => c.Documents.Select(d => new PackageDocumentRow(
                        c.CertificationTypeId,
                        c.CertificationType.DisplayOrder,
                        c.CertificationTypeName,
                        d.Content)))
                    .ToList()))
            .ToListAsync(cancellationToken);

        if (lots.Count != ids.Length)
        {
            throw new CertificationPackageException("One or more selected lots no longer exist.");
        }

        if (lots.Any(x => x.CustomerId != request.ExpectedCustomerId))
        {
            throw new CertificationPackageException("All selected lots must belong to the same customer.");
        }

        var orderedLots = lots
            .OrderBy(x => x.LotNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .ToArray();
        var packageLots = new List<CertificationPackageLot>(orderedLots.Length);
        var pdfs = new List<byte[]>();

        foreach (var lot in orderedLots)
        {
            var documentsByType = lot.Documents
                .GroupBy(x => x.CertificationTypeId)
                .ToDictionary(x => x.Key, x => x.OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name).First());
            var missing = requiredTypes
                .Where(type => type.Name != "Inspection Sheet" && !documentsByType.ContainsKey(type.Id))
                .Select(type => type.Name)
                .ToArray();
            packageLots.Add(new CertificationPackageLot(
                lot.Id,
                lot.LotNumber,
                lot.PartNumber,
                lot.InspectionDate,
                requiredTypes.Select(type => type.Name).ToArray(),
                missing));
            if (missing.Length > 0)
            {
                continue;
            }

            if (requiredTypes.Any(type => type.Name == "Inspection Sheet"))
            {
                var previewUrl = $"{applicationBaseUrl.TrimEnd('/')}/inspections/{lot.Id}/print";
                pdfs.Add(await inspectionPdfRenderer.RenderAsync(previewUrl, cancellationToken));
            }
            foreach (var typeId in requiredTypes.Select(x => x.Id))
            {
                if (documentsByType.TryGetValue(typeId, out var document))
                {
                    pdfs.Add(document.Content);
                }
            }
        }

        if (packageLots.Any(x => x.MissingCertificationNames.Count > 0))
        {
            var details = string.Join(" ", packageLots.Where(x => x.MissingCertificationNames.Count > 0)
                .Select(x => $"Lot {x.LotNumber ?? x.InspectionId.ToString() } is missing: {string.Join(", ", x.MissingCertificationNames)}."));
            throw new CertificationPackageException(details);
        }

        var fileName = filenameFormatter.FormatMultiLot(
            customer.FilenameTemplate,
            new CertificationMultiLotPackageFilenameValues(customer.Name, request.ShipDate));
        return new CertificationPackage(
            customer.Id,
            customer.Name,
            request.ShipDate,
            fileName,
            MergeAll(pdfs),
            packageLots,
            customer.ToRecipients,
            customer.CcRecipients);
    }

    private byte[] MergeAll(IReadOnlyList<byte[]> pdfs)
    {
        if (pdfs.Count == 0)
        {
            throw new CertificationPackageException("The certification package contains no documents.");
        }

        var first = pdfs[0];
        var rest = pdfs.Skip(1);
        return pdfMerger.Merge(first, rest);
    }

    private sealed record PackageInspectionRow(
        long Id,
        long CustomerId,
        string? LotNumber,
        string PartNumber,
        DateOnly InspectionDate,
        string? PoNumber,
        List<PackageDocumentRow> Documents);

    private sealed record PackageDocumentRow(
        long CertificationTypeId,
        int DisplayOrder,
        string Name,
        byte[] Content);
}
