using ChargingPanel.Infrastructure.Windows.Diagnostics;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Networking;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_JsonWithToken_SignAndConnectCode_MasksValues()
    {
        const string input =
            "{\"code\":\"game_cmd\",\"id\":\"hit\",\"token\":\"abc123token\",\"sign\":\"eJwzuserSig1234567890abcdefghij\",\"connect_code\":\"game_5 abc\"}";

        var result = SecretRedactor.Redact(input);

        Assert.DoesNotContain("abc123token", result);
        Assert.DoesNotContain("eJwzuserSig1234567890abcdefghij", result);
        Assert.DoesNotContain("game_5 abc", result);
        Assert.Contains(SecretRedactor.Mask, result);
    }

    [Fact]
    public void Redact_JwtLikeToken_MasksWholeToken()
    {
        const string jwt =
            "eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoiZ2FtZV81In0.abcdefghijklmnopqrstuvwxyz0123456789abcd";
        var result = SecretRedactor.Redact($"登录凭据={jwt}");

        Assert.DoesNotContain(jwt, result);
        Assert.Contains(SecretRedactor.Mask, result);
    }

    [Fact]
    public void Redact_Base64UserSig_MasksLongBase64Payload()
    {
        const string sig = "eJwzTkzUVEhJS0tPT0xNTE1NTU3LzMssSM0rAQAw1AUI";
        var result = SecretRedactor.Redact($"userSig={sig}");

        Assert.DoesNotContain(sig, result);
    }

    [Fact]
    public void Redact_ImPayloadWithGameCmd_MasksWholePayload()
    {
        const string payload = "{\"code\":\"game_cmd\",\"id\":\"hit\",\"token\":\"tkn\"}";
        var result = SecretRedactor.Redact($"收到 IM 消息: {payload}");

        Assert.DoesNotContain("game_cmd", result);
        Assert.DoesNotContain("tkn", result);
        Assert.Contains(SecretRedactor.Mask, result);
    }

    [Fact]
    public void Redact_BenignText_Unchanged()
    {
        const string text = "设备已连接，强度 50，波形队列已清空。";
        Assert.Equal(text, SecretRedactor.Redact(text));
    }

    [Fact]
    public void Redact_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(null));
        Assert.Equal(string.Empty, SecretRedactor.Redact(string.Empty));
    }

    [Fact]
    public void RedactKeyValuePairs_SingleQuoteToken_Masked()
    {
        const string input = "{ 'token': 's3cret-value' }";
        var result = SecretRedactor.RedactKeyValuePairs(input);
        Assert.DoesNotContain("s3cret-value", result);
    }
}
