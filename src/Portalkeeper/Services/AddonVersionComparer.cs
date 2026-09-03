using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Portalkeeper.Services;

public static class AddonVersionComparer
{
    private static readonly Regex VersionPattern = new(
        @"^[vV]?(?<numbers>\d+(?:\.\d+)*)(?:[-+](?<suffix>.*))?$",
        RegexOptions.Compiled);

    public static int Compare(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (string.IsNullOrWhiteSpace(left))
            return -1;

        if (string.IsNullOrWhiteSpace(right))
            return 1;

        var leftVersion = Parse(left);
        var rightVersion = Parse(right);

        if (leftVersion is null || rightVersion is null)
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);

        var numericResult = CompareNumbers(leftVersion.Numbers, rightVersion.Numbers);
        if (numericResult != 0)
            return numericResult;

        return CompareSuffix(leftVersion.Suffix, rightVersion.Suffix);
    }

    private static ParsedVersion? Parse(string value)
    {
        var match = VersionPattern.Match(value.Trim());
        if (!match.Success)
            return null;

        var numbers = match.Groups["numbers"].Value
            .Split('.')
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToArray();

        var suffix = match.Groups["suffix"].Success
            ? match.Groups["suffix"].Value.Trim()
            : string.Empty;

        return new ParsedVersion(numbers, suffix);
    }

    private static int CompareNumbers(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        var length = Math.Max(left.Count, right.Count);

        for (var index = 0; index < length; index++)
        {
            var leftValue = index < left.Count ? left[index] : 0;
            var rightValue = index < right.Count ? right[index] : 0;

            var result = leftValue.CompareTo(rightValue);
            if (result != 0)
                return result;
        }

        return 0;
    }

    private static int CompareSuffix(string left, string right)
    {
        var leftEmpty = string.IsNullOrWhiteSpace(left);
        var rightEmpty = string.IsNullOrWhiteSpace(right);

        // A release without a suffix is newer than a prerelease of the same version.
        if (leftEmpty && rightEmpty)
            return 0;
        if (leftEmpty)
            return 1;
        if (rightEmpty)
            return -1;

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private sealed record ParsedVersion(int[] Numbers, string Suffix);
}
