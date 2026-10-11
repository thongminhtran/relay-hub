using RelayHub.Core.Templates;

namespace RelayHub.UnitTests;

public class TemplateRendererTests
{
    [Fact]
    public void Replaces_known_tokens_and_blanks_unknown_ones()
    {
        var values = new Dictionary<string, string?> { ["name"] = "Anna", ["text"] = "Do you build websites?", ["email"] = null };

        var result = TemplateRenderer.Render("New DM from {{name}} ({{ channel }}): {{text}} {{email}}", values);

        Assert.Equal("New DM from Anna (): Do you build websites? ", result);
    }

    [Fact]
    public void Leaves_text_without_tokens_untouched()
    {
        Assert.Equal("plain", TemplateRenderer.Render("plain", new Dictionary<string, string?>()));
    }
}
