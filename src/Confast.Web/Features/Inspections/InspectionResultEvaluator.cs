using System.Globalization;
using System.Text.RegularExpressions;

namespace Confast.Web.Features.Inspections;

public enum InspectionResultEvaluation
{
    Incomplete,
    Pass,
    Fail
}

public static class InspectionResultEvaluator
{
    private static readonly Regex NominalSpecifier = new(
        @"(?<![A-Za-z])(?:Nominal|Nom\.)(?![A-Za-z])|(?<![A-Za-z])Nom(?![A-Za-z\.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReferenceSpecifier = new(
        @"(?<![A-Za-z])(?:Reference|Ref\.)(?![A-Za-z])|(?<![A-Za-z])Ref(?![A-Za-z\.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IgnorableTaggedPunctuation = new(
        @"[\(\)\[\]:;]",
        RegexOptions.CultureInvariant);

    public static InspectionResultEvaluation Evaluate(
        string? specifiedMinimum,
        string? specifiedMaximum,
        string? actualMinimum,
        string? actualMaximum)
    {
        if (IsPassingEntry(actualMinimum) || IsPassingEntry(actualMaximum))
        {
            return InspectionResultEvaluation.Pass;
        }

        if (!TryParseNumber(actualMinimum, out var recordedMinimum)
            || !TryParseNumber(actualMaximum, out var recordedMaximum))
        {
            return InspectionResultEvaluation.Incomplete;
        }

        if (ContainsSpecifier(specifiedMinimum, ReferenceSpecifier)
            || ContainsSpecifier(specifiedMaximum, ReferenceSpecifier))
        {
            return InspectionResultEvaluation.Pass;
        }

        var nominalSpecification = ContainsSpecifier(specifiedMaximum, NominalSpecifier)
            ? specifiedMaximum
            : ContainsSpecifier(specifiedMinimum, NominalSpecifier)
                ? specifiedMinimum
                : null;
        if (nominalSpecification is not null)
        {
            if (!TryParseTaggedNumber(nominalSpecification, NominalSpecifier, out var nominal))
            {
                return InspectionResultEvaluation.Incomplete;
            }

            var tolerance = decimal.Max(nominal / 3m, 0.1m);
            var lowerLimit = nominal - tolerance;
            var upperLimit = nominal + tolerance;
            return recordedMinimum >= lowerLimit
                && recordedMinimum <= upperLimit
                && recordedMaximum >= lowerLimit
                && recordedMaximum <= upperLimit
                    ? InspectionResultEvaluation.Pass
                    : InspectionResultEvaluation.Fail;
        }

        var hasSpecifiedMinimum = !string.IsNullOrWhiteSpace(specifiedMinimum);
        var hasSpecifiedMaximum = !string.IsNullOrWhiteSpace(specifiedMaximum);

        if (hasSpecifiedMinimum && hasSpecifiedMaximum)
        {
            if (!TryParseNumber(specifiedMinimum, out var minimum)
                || !TryParseNumber(specifiedMaximum, out var maximum))
            {
                return InspectionResultEvaluation.Incomplete;
            }

            return recordedMinimum >= minimum && recordedMaximum <= maximum
                ? InspectionResultEvaluation.Pass
                : InspectionResultEvaluation.Fail;
        }

        if (hasSpecifiedMinimum)
        {
            if (!TryParseNumber(specifiedMinimum, out var minimum))
            {
                return InspectionResultEvaluation.Incomplete;
            }

            return recordedMinimum >= minimum && recordedMaximum >= minimum
                ? InspectionResultEvaluation.Pass
                : InspectionResultEvaluation.Fail;
        }

        if (hasSpecifiedMaximum)
        {
            if (!TryParseNumber(specifiedMaximum, out var maximum))
            {
                return InspectionResultEvaluation.Incomplete;
            }

            return recordedMinimum <= maximum && recordedMaximum <= maximum
                ? InspectionResultEvaluation.Pass
                : InspectionResultEvaluation.Fail;
        }

        return InspectionResultEvaluation.Incomplete;
    }

    public static bool IsPassingEntry(string? value) =>
        TryNormalizePassingEntry(value, out _);

    public static bool TryNormalizePassingEntry(string? value, out string normalized)
    {
        if (string.Equals(value?.Trim(), "Pass", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Pass";
            return true;
        }

        if (string.Equals(value?.Trim(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "OK";
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static bool IsValidMeasurementEntry(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || IsPassingEntry(value)
        || TryParseNumber(value, out _);

    public static bool HasInvalidRecordedOrder(string? recordedMinimum, string? recordedMaximum) =>
        TryParseNumber(recordedMinimum, out var minimum)
        && TryParseNumber(recordedMaximum, out var maximum)
        && minimum > maximum;

    public static bool TryParseNumber(string? value, out decimal result) =>
        decimal.TryParse(
            value?.Trim(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out result);

    private static bool ContainsSpecifier(string? value, Regex specifier) =>
        !string.IsNullOrWhiteSpace(value) && specifier.IsMatch(value);

    private static bool TryParseTaggedNumber(
        string value,
        Regex specifier,
        out decimal result)
    {
        var withoutSpecifier = specifier.Replace(value, string.Empty);
        var withoutPunctuation = IgnorableTaggedPunctuation.Replace(withoutSpecifier, string.Empty);
        return TryParseNumber(withoutPunctuation, out result);
    }
}
