using Confast.Web.Features.Inspections;

namespace Confast.Web.Tests;

public sealed class InspectionStatusEvaluatorTests
{
    [Fact]
    public void InspectionIsAcceptedOnlyWhenEveryCriterionPasses()
    {
        InspectionResultEditModel[] passingResults = [
            PassingResult(),
            PassingResult()];

        Assert.True(InspectionStatusEvaluator.IsAccepted(passingResults));
        Assert.False(InspectionStatusEvaluator.IsAccepted(
            new[] { passingResults[0], IncompleteResult() }));
        Assert.True(InspectionStatusEvaluator.IsAccepted(
            [ApprovedDeviationResult()]));
        Assert.False(InspectionStatusEvaluator.IsAccepted(
            [ApprovedDeviationWithoutMeasurements()]));
        Assert.False(InspectionStatusEvaluator.IsAccepted(
            new[] { PassingResultWithoutGage() }));
        Assert.False(InspectionStatusEvaluator.IsAccepted(
            Array.Empty<InspectionResultEditModel>()));
    }

    [Fact]
    public void InspectionIsCompletedOnlyWhenAcceptedProcessesCompleteAndRequiredCertificationsUploaded()
    {
        var results = new[] { PassingResult() };
        var processes = new[] { new InspectionSecondaryProcessEditModel { IsComplete = true } };
        var certifications = new[]
        {
            new InspectionCertificationListItem
            {
                RequirementLevel = Confast.Web.Features.InspectionCriteria.CertificationRequirementLevel.Required,
                Documents = [new CertificationDocumentListItem(1, "certificate.pdf", "application/pdf", DateTimeOffset.UtcNow, 1)]
            }
        };

        Assert.True(InspectionStatusEvaluator.IsCompleted(results, processes, certifications));
        Assert.False(InspectionStatusEvaluator.IsCompleted(results,
            [new InspectionSecondaryProcessEditModel { IsComplete = false }], certifications));
        Assert.False(InspectionStatusEvaluator.IsCompleted(results, processes,
            [new InspectionCertificationListItem
            {
                RequirementLevel = Confast.Web.Features.InspectionCriteria.CertificationRequirementLevel.Required
            }]));
        Assert.False(InspectionStatusEvaluator.IsCompleted([IncompleteResult()], processes, certifications));
    }

    [Fact]
    public void IncompleteResultGatedByAnIncompleteSecondaryProcessDoesNotPreventAcceptance()
    {
        var results = new[] { PassingResult(), GatedIncompleteResult(42) };
        var incompleteProcess = new[]
        {
            new InspectionSecondaryProcessEditModel
            {
                SecondaryProcessRequirementId = 42,
                IsComplete = false
            }
        };

        Assert.True(InspectionStatusEvaluator.IsAccepted(results, incompleteProcess));
        Assert.False(InspectionStatusEvaluator.IsAccepted(
            results,
            [new InspectionSecondaryProcessEditModel
            {
                SecondaryProcessRequirementId = 42,
                IsComplete = true
            }]));
        Assert.False(InspectionStatusEvaluator.IsAccepted(results, []));
    }

    private static InspectionResultEditModel PassingResult() => new()
    {
        GageId = 1,
        SpecifiedMinimum = "20",
        SpecifiedMaximum = "21",
        ActualMin = "20.1",
        ActualMax = "20.9"
    };

    private static InspectionResultEditModel PassingResultWithoutGage() => new()
    {
        SpecifiedMinimum = "20",
        SpecifiedMaximum = "21",
        ActualMin = "20.1",
        ActualMax = "20.9"
    };

    private static InspectionResultEditModel IncompleteResult() => new()
    {
        SpecifiedMinimum = "20",
        SpecifiedMaximum = "21",
        ActualMin = null,
        ActualMax = null
    };

    private static InspectionResultEditModel GatedIncompleteResult(long processRequirementId) => new()
    {
        SecondaryProcessRequirementId = processRequirementId,
        SpecifiedMinimum = "20",
        SpecifiedMaximum = "21"
    };

    private static InspectionResultEditModel ApprovedDeviationResult() => new()
    {
        GageId = 1,
        SpecifiedMinimum = "20",
        SpecifiedMaximum = "21",
        ActualMin = "19.9",
        ActualMax = "21.1",
        DeviationApproved = true
    };

    private static InspectionResultEditModel ApprovedDeviationWithoutMeasurements() => new()
    {
        GageId = 1,
        SpecifiedMinimum = "20",
        SpecifiedMaximum = "21",
        DeviationApproved = true
    };
}
