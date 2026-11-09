using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Whyso.Storage;

/// <summary>
/// Pattern-based secret redaction for event payloads. Runs before anything is written to
/// JSONL or SQLite, so raw secrets never reach disk. Only string values are changed; keys,
/// numbers and envelope fields are left alone so provenance (session ids, SHAs) survives.
/// </summary>
public static class Redactor
{
    private sealed record Rule(string Name, Regex Pattern, int ReplaceGroup = 0);

    private static readonly Rule[] Rules =
    [
        new("private-key", new Regex(
            @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
            RegexOptions.Compiled)),
        new("aws-access-key", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        new("github-token", new Regex(
            @"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{60,})\b",
            RegexOptions.Compiled)),
        new("google-api-key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled)),
        new("slack-token", new Regex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}\b", RegexOptions.Compiled)),
        new("stripe-key", new Regex(@"\b(?:sk|rk)_(?:live|test)_[0-9A-Za-z]{16,}\b", RegexOptions.Compiled)),
        new("api-key-sk", new Regex(@"\bsk-[A-Za-z0-9_\-]{20,}\b", RegexOptions.Compiled)),
        new("bearer-token", new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=\-]{16,}", RegexOptions.Compiled)),
        // scheme://user:PASSWORD@host: replace only the password (group 2).
        new("url-credentials", new Regex(
            @"(?i)(\b[a-z][a-z0-9+.\-]*://[^\s:/@]+:)([^\s@/]+)(@)",
            RegexOptions.Compiled), ReplaceGroup: 2),
        // password = "value", token: value, api_key=value: replace only the value (group 3).
        new("assignment-secret", new Regex(
            @"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)(\s*[:=]\s*[""']?)([^\s""';,&]{6,})",
            RegexOptions.Compiled), ReplaceGroup: 3),
    ];

    private static readonly Regex HighEntropyCandidate =
        new(@"[A-Za-z0-9+/=_\-]{32,}", RegexOptions.Compiled);

    private const double EntropyThreshold = 4.2;

    public static string RedactString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var text = input;
        foreach (var rule in Rules)
        {
            text = rule.ReplaceGroup == 0
                ? rule.Pattern.Replace(text, $"[REDACTED:{rule.Name}]")
                : ReplaceGroup(rule, text);
        }

        return HighEntropyCandidate.Replace(
            text,
            m => IsLikelySecret(m.Value) ? "[REDACTED:high-entropy]" : m.Value);
    }

    /// <summary>Returns a redacted deep copy of <paramref name="node"/>.</summary>
    public static JsonNode? RedactNode(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var s) => JsonValue.Create(RedactString(s)),
        JsonObject obj => new JsonObject(obj.Select(kv => KeyValuePair.Create(kv.Key, RedactNode(kv.Value)))),
        JsonArray arr => new JsonArray(arr.Select(RedactNode).ToArray()),
        _ => node.DeepClone(),
    };

    private static string ReplaceGroup(Rule rule, string text) =>
        rule.Pattern.Replace(text, m =>
        {
            var g = m.Groups[rule.ReplaceGroup];
            var offset = g.Index - m.Index;
            return m.Value[..offset] + $"[REDACTED:{rule.Name}]" + m.Value[(offset + g.Length)..];
        });

    /// <summary>
    /// Long, mixed-case, digit-bearing tokens with high Shannon entropy. Skips ids and hashes
    /// (sess_/evt_/proj_ prefixes, pure hex such as commit SHAs) so provenance keeps working.
    /// </summary>
    private static bool IsLikelySecret(string token)
    {
        if (token.StartsWith("sess_", StringComparison.Ordinal)
            || token.StartsWith("evt_", StringComparison.Ordinal)
            || token.StartsWith("proj_", StringComparison.Ordinal)
            || token.All(IsHexDigit))
        {
            return false;
        }

        var hasLower = token.Any(char.IsLower);
        var hasUpper = token.Any(char.IsUpper);
        var hasDigit = token.Any(char.IsDigit);
        return hasLower && hasUpper && hasDigit && ShannonEntropy(token) >= EntropyThreshold;
    }

    private static bool IsHexDigit(char c) => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static double ShannonEntropy(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in s)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        double entropy = 0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / s.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
