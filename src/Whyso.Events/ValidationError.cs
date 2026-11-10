namespace Whyso.Events;

public abstract class ValidationError : Exception
{
    protected ValidationError(string message) : base(message) { }

    public sealed class UnsupportedVersion : ValidationError
    {
        public string Version { get; }
        public UnsupportedVersion(string version)
            : base($"unsupported protocol version: {version}")
        {
            Version = version;
        }
    }

    public sealed class InvalidEventId : ValidationError
    {
        public InvalidEventId()
            : base($"event_id must be non-empty and start with '{Protocol.EventIdPrefix}'")
        {
        }
    }

    public sealed class InvalidSessionId : ValidationError
    {
        public InvalidSessionId()
            : base($"session_id must be non-empty and start with '{Protocol.SessionIdPrefix}'")
        {
        }
    }

    public sealed class InvalidVisibility : ValidationError
    {
        public InvalidVisibility(string value)
            : base($"visibility must be one of {string.Join(", ", Protocol.Visibilities)} (got '{value}')")
        {
        }
    }

    public sealed class InvalidAgent : ValidationError
    {
        public InvalidAgent() : base("agent must be a non-empty string") { }
    }

    public sealed class TimestampTooFarInFuture : ValidationError
    {
        public TimestampTooFarInFuture()
            : base("timestamp must not be more than 24 hours in the future")
        {
        }
    }
}
