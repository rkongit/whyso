using System.Text.Json.Nodes;

namespace Worktree.Activities;

internal static class EditRangeExtractor
{
    public static IReadOnlyList<LineRange> Extract(JsonNode? edits)
    {
        if (edits is null)
        {
            return [];
        }

        var ranges = new List<LineRange>();
        if (edits is JsonArray array)
        {
            foreach (var item in array)
            {
                if (TryExtractOne(item, out var range))
                {
                    ranges.Add(range);
                }
            }
        }
        else if (TryExtractOne(edits, out var single))
        {
            ranges.Add(single);
        }

        return ranges;
    }

    private static bool TryExtractOne(JsonNode? node, out LineRange range)
    {
        range = null!;
        if (node is not JsonObject obj)
        {
            return false;
        }

        // Prefer explicit line fields when adapters provide them.
        if (TryInt(obj, out var start, "start_line", "startLine", "old_start", "oldStart", "new_start", "newStart")
            && TryInt(obj, out var end, "end_line", "endLine", "old_end", "oldEnd", "new_end", "newEnd"))
        {
            if (end < start)
            {
                (start, end) = (end, start);
            }

            range = new LineRange { StartLine = start, EndLine = end };
            return true;
        }

        if (obj["range"] is JsonObject r
            && TryInt(r, out start, "start", "start_line", "startLine")
            && TryInt(r, out end, "end", "end_line", "endLine"))
        {
            if (end < start)
            {
                (start, end) = (end, start);
            }

            range = new LineRange { StartLine = start, EndLine = end };
            return true;
        }

        return false;
    }

    private static bool TryInt(JsonObject obj, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            var node = obj[name];
            if (node is null)
            {
                continue;
            }

            try
            {
                value = node.GetValue<int>();
                return true;
            }
            catch
            {
                if (int.TryParse(node.ToString(), out value))
                {
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }
}
