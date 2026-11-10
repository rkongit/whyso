namespace Whyso.Events;

public static class Protocol
{
    /// <summary>Version written by this build. v1.1 adds optional actor, org_id, project_id and visibility.</summary>
    public const string Version = "1.1";

    /// <summary>Versions accepted on ingest.</summary>
    public static readonly IReadOnlySet<string> SupportedVersions = new HashSet<string> { "1.0", "1.1" };

    public const string VisibilityPrivate = "private";
    public const string VisibilityTeam = "team";
    public const string VisibilityAudit = "audit";

    public static readonly IReadOnlyList<string> Visibilities = [VisibilityPrivate, VisibilityTeam, VisibilityAudit];
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
        if (!SupportedVersions.Contains(event_.Version))
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

        if (event_.Visibility is not null && !Visibilities.Contains(event_.Visibility))
        {
            throw new ValidationError.InvalidVisibility(event_.Visibility);
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
