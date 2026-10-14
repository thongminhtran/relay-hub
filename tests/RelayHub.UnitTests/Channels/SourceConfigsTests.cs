using System.Text.Json;
using RelayHub.Core.Channels;
using RelayHub.Core.Domain;

namespace RelayHub.UnitTests.Channels;

public class SourceConfigsTests
{
    private static JsonElement? J(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Generic_accepts_object_or_null_and_stores_an_empty_object()
    {
        Assert.Equal(("{}", null), SourceConfigs.Validate(SourceType.Generic, null));
        Assert.Equal(("{}", null), SourceConfigs.Validate(SourceType.Generic, J("""{"anything":1}""")));
        Assert.Equal(("{}", null), SourceConfigs.Validate(SourceType.Generic, J("null")));
        Assert.NotNull(SourceConfigs.Validate(SourceType.Generic, J("[1,2]")).Error);
        Assert.NotNull(SourceConfigs.Validate(SourceType.Generic, J("\"text\"")).Error);
    }

    [Theory]
    [InlineData(SourceType.Instagram)]
    [InlineData(SourceType.Facebook)]
    public void Meta_sources_require_a_page_access_token_and_keep_the_optional_page_name(SourceType type)
    {
        Assert.Contains("pageAccessToken", SourceConfigs.Validate(type, null).Error);
        Assert.Contains("pageAccessToken", SourceConfigs.Validate(type, J("""{"pageAccessToken":"   "}""")).Error);
        Assert.Contains("pageAccessToken", SourceConfigs.Validate(type, J("""{"pageAccessToken":123}""")).Error);

        var (json, error) = SourceConfigs.Validate(type, J("""{"pageAccessToken":" EAABsbCS1iHgBA... ","pageName":"Studio","extra":"dropped"}"""));
        Assert.Null(error);
        Assert.Equal("""{"pageAccessToken":"EAABsbCS1iHgBA...","pageName":"Studio"}""", json);

        var source = new Source { Name = "s", KeyId = "k", Type = type, ConfigJson = json! };
        Assert.Equal("EAABsbCS1iHgBA...", SourceConfigs.PageAccessToken(source));
        Assert.Equal("Studio", SourceConfigs.PageName(source));
    }

    [Theory]
    [InlineData("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", true)]
    [InlineData("123:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw_-x", true)]
    [InlineData("AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", false)]
    [InlineData("123456789:short", false)]
    [InlineData("abc:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", false)]
    [InlineData("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw with space", false)]
    [InlineData("", false)]
    public void Telegram_bot_token_must_match_botfather_format(string token, bool valid)
    {
        var (json, error) = SourceConfigs.Validate(SourceType.Telegram, J(JsonSerializer.Serialize(new { botToken = token })));

        Assert.Equal(valid, error is null);
        if (valid)
        {
            Assert.Equal(token, SourceConfigs.BotToken(new Source { Name = "s", KeyId = "k", Type = SourceType.Telegram, ConfigJson = json! }));
        }
    }

    [Fact]
    public void Telegram_without_config_is_an_error()
    {
        Assert.Contains("botToken", SourceConfigs.Validate(SourceType.Telegram, null).Error);
    }

    [Fact]
    public void Email_from_address_is_optional_but_must_be_valid()
    {
        Assert.Equal(("""{"fromAddress":null}""", null), SourceConfigs.Validate(SourceType.Email, null));
        Assert.Equal(("""{"fromAddress":null}""", null), SourceConfigs.Validate(SourceType.Email, J("""{"fromAddress":""}""")));
        Assert.Equal(("""{"fromAddress":"inbox@studio.example"}""", null), SourceConfigs.Validate(SourceType.Email, J("""{"fromAddress":" inbox@studio.example "}""")));
        Assert.Contains("fromAddress", SourceConfigs.Validate(SourceType.Email, J("""{"fromAddress":"not-an-email"}""")).Error);
        Assert.Contains("fromAddress", SourceConfigs.Validate(SourceType.Email, J("""{"fromAddress":"two words@x.com"}""")).Error);

        Assert.Null(SourceConfigs.FromAddress(new Source { Name = "s", KeyId = "k", Type = SourceType.Email, ConfigJson = """{"fromAddress":null}""" }));
        Assert.Null(SourceConfigs.FromAddress(new Source { Name = "s", KeyId = "k", Type = SourceType.Email, ConfigJson = "not json" }));
    }

    [Theory]
    [InlineData("anna@example.com", true)]
    [InlineData("Anna.Lee+tag@sub.example.co", true)]
    [InlineData("Anna Lee <anna@example.com>", false)]
    [InlineData("anna@", false)]
    [InlineData("@example.com", false)]
    [InlineData("anna", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Bare_email_check(string? value, bool expected)
    {
        Assert.Equal(expected, SourceConfigs.IsEmail(value));
    }
}
