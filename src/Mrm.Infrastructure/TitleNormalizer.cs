using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mrm.Infrastructure;

public static partial class TitleNormalizer
{
    public static string Normalize(string title)
    {
        // Unicode NFKD decomposition → strip diacritics
        var normalized = title.Normalize(NormalizationForm.FormKD);
        var stripped = new string(normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());

        // Lowercase → strip non-alphanumeric (except spaces) → collapse whitespace → trim
        var lower = stripped.ToLowerInvariant();
        var alphaSpace = NonAlphanumericExceptSpace().Replace(lower, " ");
        return CollapseWhitespace().Replace(alphaSpace, " ").Trim();
    }

    [GeneratedRegex(@"[^a-z0-9 ]")]
    private static partial Regex NonAlphanumericExceptSpace();

    [GeneratedRegex(@" +")]
    private static partial Regex CollapseWhitespace();
}
