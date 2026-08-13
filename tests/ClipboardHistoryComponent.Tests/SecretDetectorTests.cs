using Xunit;

namespace YingqiClipboard.Tests;

public sealed class SecretDetectorTests
{
    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyzABCDE1234567890")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c")]
    [InlineData("api_key = abcdefghijklmnopqrstuvwxyz123456")]
    public void DetectsHighConfidenceSecrets(string value) => Assert.True(SecretDetector.ContainsHighConfidenceSecret(value));

    [Theory]
    [InlineData("验证码 123456")]
    [InlineData("https://example.com/path")]
    [InlineData("这是一段普通长文本，不应该因为长度被误判。")]
    public void KeepsOrdinaryText(string value) => Assert.False(SecretDetector.ContainsHighConfidenceSecret(value));
}
