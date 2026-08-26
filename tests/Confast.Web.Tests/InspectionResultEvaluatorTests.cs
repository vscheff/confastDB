using Confast.Web.Features.Inspections;

namespace Confast.Web.Tests;

public sealed class InspectionResultEvaluatorTests
{
    [Fact]
    public void MeasurementsEntirelyInsideTolerancePass()
    {
        var evaluation = InspectionResultEvaluator.Evaluate("20", "21", "20.1", "20.9");

        Assert.Equal(InspectionResultEvaluation.Pass, evaluation);
    }

    [Fact]
    public void ActualMinimumBelowLowerLimitFails()
    {
        var evaluation = InspectionResultEvaluator.Evaluate("20", "21", "19.9", "20.9");

        Assert.Equal(InspectionResultEvaluation.Fail, evaluation);
    }

    [Fact]
    public void ActualMaximumAboveUpperLimitFails()
    {
        var evaluation = InspectionResultEvaluator.Evaluate("20", "21", "20.1", "21.1");

        Assert.Equal(InspectionResultEvaluation.Fail, evaluation);
    }

    [Theory]
    [InlineData(null, "20.9")]
    [InlineData("20.1", null)]
    [InlineData(null, null)]
    [InlineData("not a measurement", "20.9")]
    public void MissingOrInvalidActualMeasurementIsIncomplete(
        string? actualMin,
        string? actualMax)
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            "20",
            "21",
            actualMin,
            actualMax);

        Assert.Equal(InspectionResultEvaluation.Incomplete, evaluation);
    }

    [Fact]
    public void TextCalloutWithoutPassingEntryIsIncomplete()
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            "GO / NO-GO",
            "M4 - 0.7 6H",
            null,
            null);

        Assert.Equal(InspectionResultEvaluation.Incomplete, evaluation);
    }

    [Theory]
    [InlineData("Pass", null)]
    [InlineData(null, "pass")]
    [InlineData("OK", null)]
    [InlineData(null, "ok")]
    [InlineData("  pAsS  ", "20.5")]
    public void PassOrOkInEitherActualFieldPasses(string? actualMin, string? actualMax)
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            "GO / NO-GO",
            "M4 - 0.7 6H",
            actualMin,
            actualMax);

        Assert.Equal(InspectionResultEvaluation.Pass, evaluation);
    }

    [Theory]
    [InlineData("20", "20.1", "22", InspectionResultEvaluation.Pass)]
    [InlineData("20", "19.9", "22", InspectionResultEvaluation.Fail)]
    [InlineData("20", "22", "19.9", InspectionResultEvaluation.Fail)]
    public void MinimumOnlyChecksBothMeasurements(
        string specifiedMin,
        string actualMin,
        string actualMax,
        InspectionResultEvaluation expected)
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            specifiedMin,
            null,
            actualMin,
            actualMax);

        Assert.Equal(expected, evaluation);
    }

    [Theory]
    [InlineData("21", "20", "20.9", InspectionResultEvaluation.Pass)]
    [InlineData("21", "21.1", "20.9", InspectionResultEvaluation.Fail)]
    [InlineData("21", "20", "21.1", InspectionResultEvaluation.Fail)]
    public void MaximumOnlyChecksBothMeasurements(
        string specifiedMax,
        string actualMin,
        string actualMax,
        InspectionResultEvaluation expected)
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            null,
            specifiedMax,
            actualMin,
            actualMax);

        Assert.Equal(expected, evaluation);
    }

    [Theory]
    [InlineData("0.3 Nominal", "0.2", "0.4", InspectionResultEvaluation.Pass)]
    [InlineData("0.3 nom", "0.1999", "0.4", InspectionResultEvaluation.Fail)]
    [InlineData("0.3 NOM.", "0.2", "0.4001", InspectionResultEvaluation.Fail)]
    [InlineData("(Nominal) 0.15", "0.05", "0.25", InspectionResultEvaluation.Pass)]
    public void NominalToleranceUsesInclusiveCalculatedRange(
        string specification,
        string recordedMin,
        string recordedMax,
        InspectionResultEvaluation expected)
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            null,
            specification,
            recordedMin,
            recordedMax);

        Assert.Equal(expected, evaluation);
    }

    [Fact]
    public void NominalSpecifierMayAppearInSpecifiedMinimum()
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            "0.6 Nom",
            null,
            "0.4",
            "0.8");

        Assert.Equal(InspectionResultEvaluation.Pass, evaluation);
    }

    [Theory]
    [InlineData("Reference")]
    [InlineData("ref")]
    [InlineData("REF.")]
    public void ReferenceTolerancePassesWithTwoNumericMeasurements(string specification)
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            null,
            specification,
            "-100",
            "500");

        Assert.Equal(InspectionResultEvaluation.Pass, evaluation);
    }

    [Fact]
    public void ReferenceToleranceWithoutTwoMeasurementsIsIncomplete()
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            "Reference",
            null,
            "20",
            null);

        Assert.Equal(InspectionResultEvaluation.Incomplete, evaluation);
    }

    [Theory]
    [InlineData("20", "19", true)]
    [InlineData("20", "20", false)]
    [InlineData("20", "21", false)]
    [InlineData("21", "20", true)]
    [InlineData("OK", "20", false)]
    public void RecordedMaximumCannotBeLessThanRecordedMinimum(
        string recordedMin,
        string recordedMax,
        bool expectedInvalid)
    {
        Assert.Equal(
            expectedInvalid,
            InspectionResultEvaluator.HasInvalidRecordedOrder(recordedMin, recordedMax));
    }

    [Theory]
    [InlineData("pass", "Pass")]
    [InlineData("pASS", "Pass")]
    [InlineData(" Pass ", "Pass")]
    [InlineData("ok", "OK")]
    [InlineData("Ok", "OK")]
    [InlineData(" OK ", "OK")]
    public void PassingEntriesAreNormalized(string entry, string expected)
    {
        var wasNormalized = InspectionResultEvaluator.TryNormalizePassingEntry(
            entry,
            out var normalized);

        Assert.True(wasNormalized);
        Assert.Equal(expected, normalized);
    }
}
