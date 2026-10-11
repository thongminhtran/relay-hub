using System.Text.RegularExpressions;

namespace RelayHub.Core.Templates;

/// <summary>Replaces <c>{{name}}</c>-style tokens. Unknown tokens render as empty text.</summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex Token();

    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
        => Token().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : "");
}
