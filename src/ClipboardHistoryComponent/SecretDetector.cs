using System.Text.RegularExpressions;

namespace YingqiClipboard;

public static partial class SecretDetector
{
    public static bool ContainsHighConfidenceSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        return PrivateKey().IsMatch(text)
            || KnownToken().IsMatch(text)
            || AwsAccessKey().IsMatch(text)
            || Jwt().IsMatch(text)
            || LabeledSecret().IsMatch(text);
    }

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"(?ix)\b(?:gh[pousr]_[A-Za-z0-9_]{36,}|github_pat_[A-Za-z0-9_]{40,}|sk-proj-[A-Za-z0-9_-]{32,}|sk-[A-Za-z0-9_-]{32,}|xox[baprs]-[A-Za-z0-9-]{20,})\b")]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex Jwt();

    [GeneratedRegex("(?ix)\\b(?:api[_-]?key|secret[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|auth[_-]?token|bearer)\\b['\\\"]?\\s*[:=]\\s*['\\\"]?[A-Za-z0-9][A-Za-z0-9._~+/=-]{23,}['\\\"]?")]
    private static partial Regex LabeledSecret();
}
