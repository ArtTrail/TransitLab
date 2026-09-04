using System.Globalization;
using System.Linq;

namespace TransitLab.Services;

/// <summary>
/// Parses user-typed and data-sourced numeric strings without trusting (or requiring) any
/// particular OS locale. None of TransitLab's numeric fields — coordinates, orbital
/// parameters, pixel scale, filter wavelengths, etc. — ever legitimately need a thousands
/// grouping separator, so any "," or "." found in the input is always treated as a decimal
/// mark, never as grouping. This lets "12,345" (comma-decimal locales such as es/pt/de/fr/it/
/// ru) and "12.345" (English) both parse to the same correct value with no locale detection
/// at all, and avoids the old bug where NumberStyles.Any + InvariantCulture silently accepted
/// a decimal comma as a valid 3-digit thousands grouping and inflated the value ~1000x.
/// </summary>
public static class NumericParseService
{
    public static bool TryParse(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return double.TryParse(Normalize(s), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // Named distinctly (not an out-float overload of TryParse) because "out var" call
    // sites can't disambiguate two TryParse overloads that differ only in the out type.
    public static bool TryParseFloat(string? s, out float value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return float.TryParse(Normalize(s), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static string Normalize(string s)
    {
        var t = s.Trim();
        t = t.Replace(" ", "").Replace(" ", "").Replace("'", "");

        int commaCount  = t.Count(c => c == ',');
        int periodCount = t.Count(c => c == '.');

        if (commaCount > 0 && periodCount > 0)
        {
            // Both kinds of separator present — whichever occurs last is the decimal
            // mark, and every other occurrence (of either character) is grouping.
            bool commaIsDecimal = t.LastIndexOf(',') > t.LastIndexOf('.');
            char groupChar = commaIsDecimal ? '.' : ',';
            char decimalChar = commaIsDecimal ? ',' : '.';
            t = t.Replace(groupChar.ToString(), "");
            t = ReplaceLast(t, decimalChar, '.');
        }
        else if (commaCount == 1)
        {
            t = t.Replace(',', '.');
        }
        else if (commaCount > 1)
        {
            // Multiple commas with no period at all — a plain thousands-grouped integer.
            t = t.Replace(",", "");
        }

        return t;
    }

    private static string ReplaceLast(string s, char oldChar, char newChar)
    {
        int idx = s.LastIndexOf(oldChar);
        return idx < 0 ? s : s[..idx] + newChar + s[(idx + 1)..];
    }
}
