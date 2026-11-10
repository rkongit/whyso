using Whyso.Events;

namespace Whyso.Storage;

public static class Jsonl
{
    public static string PathFor(string root, string sessionId, DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return Path.Combine(
            WhysoPaths.EventsDir(root),
            utc.Year.ToString("D4"),
            utc.Month.ToString("D2"),
            utc.Day.ToString("D2"),
            $"{sessionId}.jsonl");
    }

    public static void AppendLine(string path, AgentEvent event_)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.AppendAllText(path, event_.ToJson() + "\n");
    }

    public static List<AgentEvent> ReadEvents(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var events = new List<AgentEvent>();
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            try
            {
                events.Add(AgentEvent.FromJson(trimmed));
            }
            catch (Exception ex)
            {
                throw new StorageException($"malformed JSONL at {path}:{i + 1}: {ex.Message}");
            }
        }

        return events;
    }
}
