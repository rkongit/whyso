namespace Worktree.Events;

public static class Protocol
{
    public const string Version = "1.0";
    public const string EventIdPrefix = "evt_";
    public const string SessionIdPrefix = "sess_";

    public static string NewEventId() => $"{EventIdPrefix}{Guid.NewGuid():D}";

    public static string NewSessionId() => $"{SessionIdPrefix}{Guid.NewGuid():D}";

    public static string SessionIdFromExternal(string raw) =>
        raw.StartsWith(SessionIdPrefix, StringComparison.Ordinal)
            ? raw
            : $"{SessionIdPrefix}{raw}";

    public static bool IsValidPrefixedId(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length > prefix.Length
        && !string.IsNullOrWhiteSpace(value[prefix.Length..]);

    public static void ValidateEnvelope(AgentEvent event_)
    {
        if (event_.Version != Version)
        {
            throw new ValidationError.UnsupportedVersion(event_.Version);
        }

        if (!IsValidPrefixedId(event_.EventId, EventIdPrefix))
        {
            throw new ValidationError.InvalidEventId();
        }

        if (!IsValidPrefixedId(event_.SessionId, SessionIdPrefix))
        {
            throw new ValidationError.InvalidSessionId();
        }

        if (string.IsNullOrWhiteSpace(event_.Agent))
        {
            throw new ValidationError.InvalidAgent();
        }

        var maxFuture = DateTimeOffset.UtcNow.AddHours(24);
        if (event_.Timestamp > maxFuture)
        {
            throw new ValidationError.TimestampTooFarInFuture();
        }
    }

    public static bool TryValidateEnvelope(AgentEvent event_, out ValidationError? error)
    {
        try
        {
            ValidateEnvelope(event_);
            error = null;
            return true;
        }
        catch (ValidationError ve)
        {
            error = ve;
            return false;
        }
    }
}
