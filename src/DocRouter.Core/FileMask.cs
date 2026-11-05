using System.Text.RegularExpressions;

namespace DocRouter.Core;

/// <summary>Маски имён файлов: * — любая последовательность, ? — один символ; без учёта регистра.</summary>
public static class FileMask
{
    public static bool IsMatch(string mask, string name)
    {
        var pattern = "^" + Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool MatchesAny(IEnumerable<string> masks, string name) => masks.Any(m => IsMatch(m, name));
}
