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

    [Fact]
    public void ApprovedDeviationOverridesAnOutOfToleranceMeasurement()
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            "20",
            "21",
            "19.9",
            "21.1",
            deviationApproved: true);

        Assert.Equal(InspectionResultEvaluation.Pass, evaluation);
    }

    [Theory]
    [InlineData(null, "21.1")]
    [InlineData("19.9", null)]
    [InlineData("not a measurement", "21.1")]
    [InlineData("19.9", "not a measurement")]
    [InlineData("Pass", null)]
    public void ApprovedDeviationWithoutTwoValidRecordedEntriesIsIncomplete(
        string? actualMin,
        string? actualMax)
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            "20",
            "21",
            actualMin,
            actualMax,
            deviationApproved: true);

        Assert.Equal(InspectionResultEvaluation.Incomplete, evaluation);
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
    [InlineData("0.3 Nominal", "-0.03", "0.63", InspectionResultEvaluation.Pass)]
    [InlineData("0.3 nom", "-0.0301", "0.63", InspectionResultEvaluation.Fail)]
    [InlineData("0.3 NOM.", "-0.03", "0.6301", InspectionResultEvaluation.Fail)]
    [InlineData("(Nominal) 0.15", "-0.18", "0.48", InspectionResultEvaluation.Pass)]
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

    [Fact]
    public void NominalToleranceUsesConfiguredLargeDimensionDivisor()
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            null,
            "6 Nom",
            "4.4",
            "7.6",
            nominalToleranceFloor: 0.5m,
            nominalToleranceDivisor: 5m);

        Assert.Equal(InspectionResultEvaluation.Fail, evaluation);
    }

    [Fact]
    public void NominalToleranceUsesConfiguredFloor()
    {
        var evaluation = InspectionResultEvaluator.Evaluate(
            null,
            "0.3 Nom",
            "-0.2",
            "0.8",
            nominalToleranceFloor: 0.5m,
            nominalToleranceDivisor: 3m);

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
    [InlineData("1.2", "0.12", "0.123", "1.200")]
    [InlineData("1.2", "0.123 Nom", null, "1.200")]
    [InlineData("1.2", "0.123 Nom.", null, "1.200")]
    [InlineData("1.2", "0.123 nOmInAl", null, "1.200")]
    [InlineData("1.2", "0.123 Ref", null, "1.200")]
    [InlineData("1.2", "0.123 Ref.", null, "1.200")]
    [InlineData("1.2", "0.123 Reference", null, "1.200")]
    [InlineData("1.2345", "0.12", "0.123", "1.2345")]
    public void RecordedMeasurementsArePaddedToTheGreatestSpecifiedPrecision(
        string recordedValue,
        string? specifiedMinimum,
        string? specifiedMaximum,
        string expected)
    {
        var formatted = InspectionResultEvaluator.EnsureRecordedDecimalPlaces(
            recordedValue,
            specifiedMinimum,
            specifiedMaximum);

        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData("Pass")]
    [InlineData("1.2")]
    public void RecordedMeasurementsAreNotFormattedWithoutANumericSpecification(string recordedValue)
    {
        var formatted = InspectionResultEvaluator.EnsureRecordedDecimalPlaces(
            recordedValue,
            "Reference",
            "GO / NO-GO");

        Assert.Equal(recordedValue, formatted);
    }

    [Theory]
    [InlineData(".546", "0.546")]
    [InlineData("-.546", "-0.546")]
    [InlineData("+.546", "+0.546")]
    public void RecordedMeasurementsWithoutALeadingZeroAreNormalized(string recordedValue, string expected)
    {
        var formatted = InspectionResultEvaluator.EnsureRecordedDecimalPlaces(
            recordedValue,
            null,
            null);

        Assert.Equal(expected, formatted);
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
