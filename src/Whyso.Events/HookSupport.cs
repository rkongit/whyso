using System.Text;
using System.Text.Json.Nodes;

namespace Whyso.Events;

/// <summary>Raised when a hook payload cannot be adapted or delivered.</summary>
public sealed class HookException : Exception
{
    public HookException(string message) : base(message) { }
    public HookException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Shared plumbing for agent hook adapters: stdin cleanup, payload size limits, delivery to the
/// local daemon, and the hook log. Adapters stay fail-open: callers never let these errors
/// reach the coding agent.
/// </summary>
public static class HookSupport
{
    public const int MaxStringBytes = 64 * 1024;
    public const string DefaultEventsUrl = "http://127.0.0.1:9477/v1/events";

    public static string NormalizeStdin(string stdin)
    {
        var trimmed = stdin.TrimStart('﻿', '￾', '\0');
        return trimmed.TrimStart();
    }

    public static AgentEvent PostEvent(AgentEvent event_, string url)
    {
        // Match SQLite busy_timeout (5s) so a locked DB does not outlive the HTTP client.
        const int maxAttempts = 3;
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var content = new StringContent(event_.ToJson(), Encoding.UTF8, "application/json");
                using var response = client.PostAsync(url, content).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HookException($"HTTP error: {(int)response.StatusCode} {body}");
                }

                return AgentEvent.FromJson(body);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                last = ex;
                Thread.Sleep(50 * attempt);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last is HookException he
            ? he
            : new HookException($"HTTP error: {last?.Message ?? "unknown"}", last!);
    }

    /// <summary>Truncates long string values in place and marks truncated objects with <c>_truncated</c>.</summary>
    public static void TruncateStrings(JsonNode? value, int maxBytes = MaxStringBytes)
    {
        switch (value)
        {
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > maxBytes)
                    {
                        arr[i] = Truncate(s, maxBytes);
                    }
                    else
                    {
                        TruncateStrings(arr[i], maxBytes);
                    }
                }

                break;
            case JsonObject map:
            {
                var truncated = false;
                foreach (var key in map.Select(kv => kv.Key).ToList())
                {
                    if (key == "_truncated")
                    {
                        continue;
                    }

                    if (map[key] is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > maxBytes)
                    {
                        map[key] = Truncate(s, maxBytes);
                        truncated = true;
                    }
                    else
                    {
                        TruncateStrings(map[key], maxBytes);
                    }
                }

                if (truncated)
                {
                    map["_truncated"] = true;
                }

                break;
            }
        }
    }

    public static string Truncate(string s, int maxBytes)
    {
        if (s.Length <= maxBytes)
        {
            return s;
        }

        var cut = s[..maxBytes];
        while (cut.Length > 0 && char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1];
        }

        return cut;
    }

    public static void AppendLog(string? logPath, string message)
    {
        if (logPath is null)
        {
            return;
        }

        try
        {
            var parent = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    public static void CopyFields(JsonObject src, JsonObject dst, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (src[key] is not null)
            {
                dst[key] = src[key]!.DeepClone();
            }
        }
    }

    /// <summary>
    /// True when a hook command invokes this tool's adapter for <paramref name="agent"/>, under the
    /// current "whyso" name or the pre-rename "worktree" name.
    /// </summary>
    public static bool IsOwnHookCommand(string command, string agent)
    {
        var lower = command.ToLowerInvariant();
        return (lower.Contains("whyso") || lower.Contains("worktree"))
            && lower.Contains("hook")
            && lower.Contains(agent);
    }
}
