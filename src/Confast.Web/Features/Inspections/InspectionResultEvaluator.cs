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
    public const decimal DefaultNominalToleranceFloor = 0.33m;
    public const decimal DefaultNominalToleranceDivisor = 3m;

    private static readonly Regex NominalSpecifier = new(
        @"(?<![A-Za-z])(?:Nominal|Nom\.)(?![A-Za-z])|(?<![A-Za-z])Nom(?![A-Za-z\.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReferenceSpecifier = new(
        @"(?<![A-Za-z])(?:Reference|Ref\.)(?![A-Za-z])|(?<![A-Za-z])Ref(?![A-Za-z\.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingPrecisionQualifier = new(
        @"^\s*(?<number>[+-]?(?:\d+(?:,\d{3})*(?:\.\d*)?|\.\d+))\s+(?:Nominal|Nom\.?|Reference|Ref\.?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IgnorableTaggedPunctuation = new(
        @"[\(\)\[\]:;]",
        RegexOptions.CultureInvariant);

    public static InspectionResultEvaluation Evaluate(
        string? specifiedMinimum,
        string? specifiedMaximum,
        string? actualMinimum,
        string? actualMaximum,
        bool deviationApproved = false,
        decimal nominalToleranceFloor = DefaultNominalToleranceFloor,
        decimal nominalToleranceDivisor = DefaultNominalToleranceDivisor)
    {
        if (deviationApproved)
        {
            return HasValidRecordedEntry(actualMinimum)
                && HasValidRecordedEntry(actualMaximum)
                ? InspectionResultEvaluation.Pass
                : InspectionResultEvaluation.Incomplete;
        }

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

            var tolerance = decimal.Max(nominal / nominalToleranceDivisor, nominalToleranceFloor);
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

    private static bool HasValidRecordedEntry(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && IsValidMeasurementEntry(value);

    public static bool HasInvalidRecordedOrder(string? recordedMinimum, string? recordedMaximum) =>
        TryParseNumber(recordedMinimum, out var minimum)
        && TryParseNumber(recordedMaximum, out var maximum)
        && minimum > maximum;

    public static string? EnsureRecordedDecimalPlaces(
        string? recordedValue,
        string? specifiedMinimum,
        string? specifiedMaximum)
    {
        if (!TryGetPlainNumberDecimalPlaceCount(recordedValue, out var recordedDecimalPlaces))
        {
            return recordedValue;
        }

        var normalizedRecordedValue = AddLeadingZero(recordedValue!);

        var requiredDecimalPlaces = 0;
        if (TryGetSpecifiedDecimalPlaceCount(specifiedMinimum, out var minimumDecimalPlaces))
        {
            requiredDecimalPlaces = minimumDecimalPlaces;
        }

        if (TryGetSpecifiedDecimalPlaceCount(specifiedMaximum, out var maximumDecimalPlaces))
        {
            requiredDecimalPlaces = Math.Max(requiredDecimalPlaces, maximumDecimalPlaces);
        }

        return requiredDecimalPlaces <= recordedDecimalPlaces
            ? normalizedRecordedValue
            : normalizedRecordedValue.Contains('.')
                ? normalizedRecordedValue + new string('0', requiredDecimalPlaces - recordedDecimalPlaces)
                : normalizedRecordedValue + "." + new string('0', requiredDecimalPlaces);
    }

    public static bool TryParseNumber(string? value, out decimal result) =>
        decimal.TryParse(
            value?.Trim(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out result);

    private static bool TryGetSpecifiedDecimalPlaceCount(string? specification, out int decimalPlaces)
    {
        if (TryGetPlainNumberDecimalPlaceCount(specification, out decimalPlaces))
        {
            return true;
        }

        var qualifierMatch = specification is null ? null : TrailingPrecisionQualifier.Match(specification);
        return qualifierMatch is not null
            && qualifierMatch.Success
            && TryGetPlainNumberDecimalPlaceCount(qualifierMatch.Groups["number"].Value, out decimalPlaces);
    }

    private static bool TryGetPlainNumberDecimalPlaceCount(string? value, out int decimalPlaces)
    {
        decimalPlaces = 0;
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || !TryParseNumber(value, out _))
        {
            return false;
        }

        var decimalSeparatorIndex = value.IndexOf('.');
        if (decimalSeparatorIndex < 0 || decimalSeparatorIndex != value.LastIndexOf('.'))
        {
            return decimalSeparatorIndex < 0;
        }

        decimalPlaces = value.Length - decimalSeparatorIndex - 1;
        return true;
    }

    private static string AddLeadingZero(string value) => value switch
    {
        [ '.', .. ] => "0" + value,
        [ '-', '.', .. ] => "-0" + value[1..],
        [ '+', '.', .. ] => "+0" + value[1..],
        _ => value
    };

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
