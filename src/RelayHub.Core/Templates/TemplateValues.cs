using RelayHub.Core.Domain;

namespace RelayHub.Core.Templates;

/// <summary>The tokens a route template can use: {{name}}, {{email}}, {{handle}}, {{text}}, {{channel}}, {{source}}, {{receivedAt}}.</summary>
public static class TemplateValues
{
    public static IReadOnlyDictionary<string, string?> For(Event evt, Source source) => new Dictionary<string, string?>
    {
        ["name"] = evt.SenderName,
        ["email"] = evt.SenderHandle,
        ["handle"] = evt.SenderHandle,
        ["text"] = evt.Text,
        ["channel"] = evt.Channel,
        ["source"] = source.Name,
        ["receivedAt"] = evt.ReceivedAt.ToString("u"),
    };
}
