using System.Text.RegularExpressions;

namespace DocRouter.Core;

/// <summary>Тип документа: явный (поле view) → правила маршрута по префиксу/маске имени → тип по умолчанию → "" (не определён).</summary>
public static class TypeClassifier
{
    public static string Resolve(RouteOptions route, string fileName, string? explicitType)
    {
        if (!string.IsNullOrWhiteSpace(explicitType)) return explicitType;
        foreach (var rule in route.TypeRules)
            if (Matches(rule, fileName)) return rule.Type;
        return route.DefaultType ?? "";
    }

    private static bool Matches(TypeRule r, string name) =>
        (r.Prefix is { Length: > 0 } p && name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
        (r.Mask is { Length: > 0 } m && Glob(m, name));

    private static bool Glob(string mask, string name)
    {
        var pattern = "^" + Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
