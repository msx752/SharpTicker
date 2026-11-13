#nullable disable
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SharpTicker.Caching.StackExchangeRedis.Helpers;

// The Lua scripts rewrite a row through cjson, which writes an empty array as {}. Since 1.3.0 the scripts turn the two
// collections they know (Children, RetryIntervals) back into []; a row written before that, or an empty collection of an
// application's own ticker type, still holds {} and does not deserialize.
// Used only after a row failed to deserialize. The serializer says where it failed (JsonException.Path); when the value
// there is an empty object, it is read as an empty array and the row is tried again, once for each such place. Anything
// else is left as it is: the row is not readable for another reason, and the caller reports the first failure.
// A value that reads from an empty object without a failure (a dictionary, an object-typed property) does not come here.
// Nothing is written: the text in the store stays as it was until the row's next write.
internal static class EmptyCollectionRepair
{
    // The row read with its empty objects taken as empty collections, or null when that does not make it readable.
    internal static object TryRead(string json, JsonTypeInfo typeInfo, JsonException failure)
    {
        try
        {
            var root = JsonNode.Parse(json);

            // Every round turns one empty object into an array or gives up, so the rounds end with the row's empty objects.
            while (true)
            {
                if (!TryReplaceEmptyObject(root, failure.Path))
                    return null;

                try
                {
                    return JsonSerializer.Deserialize(root.ToJsonString(), typeInfo);
                }
                catch (JsonException next)
                {
                    failure = next;
                }
            }
        }
        catch (Exception)
        {
            // The text is not something this can work on (not JSON, a key twice in one object), or reading it again failed
            // in another way. The row stays unread and the caller reports the first failure, as it did before.
            return null;
        }
    }

    private static bool TryReplaceEmptyObject(JsonNode root, string path)
    {
        if (!TryResolve(root, path, out var parent, out var name, out var index))
            return false;

        // A property of an object, or an element of an array (an empty list inside a list).
        if (parent is JsonObject owner)
        {
            if (owner[name] is not JsonObject { Count: 0 }) return false;
            owner[name] = new JsonArray();
            return true;
        }

        if (parent is JsonArray list)
        {
            if (list[index] is not JsonObject { Count: 0 }) return false;
            list[index] = new JsonArray();
            return true;
        }

        return false;
    }

    // Walks a path as System.Text.Json writes it: $.Name, $.Name[0].Other, $['a name']. Gives the node that holds the last
    // step, with the name or the index of that step. False for a path that does not lead to a value of this document; a
    // property name or dictionary key that contains '] is not followed, and such a row stays unread.
    private static bool TryResolve(JsonNode root, string path, out JsonNode parent, out string name, out int index)
    {
        parent = null;
        name = null;
        index = -1;

        if (string.IsNullOrEmpty(path) || path[0] != '$' || path.Length == 1)
            return false;

        var current = root;
        var position = 1;

        while (position < path.Length)
        {
            string stepName = null;
            var stepIndex = -1;

            if (path[position] == '.')
            {
                var end = path.IndexOfAny(['.', '['], position + 1);
                if (end < 0) end = path.Length;
                stepName = path[(position + 1)..end];
                position = end;
            }
            else if (path[position] == '[' && position + 1 < path.Length && path[position + 1] == '\'')
            {
                var end = path.IndexOf("']", position + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                stepName = path[(position + 2)..end];
                position = end + 2;
            }
            else if (path[position] == '[')
            {
                var end = path.IndexOf(']', position + 1);
                if (end < 0 || !int.TryParse(path.AsSpan(position + 1, end - position - 1), out stepIndex) || stepIndex < 0)
                    return false;
                position = end + 1;
            }
            else
            {
                return false;
            }

            JsonNode next;
            if (stepName != null)
            {
                if (current is not JsonObject holder || !holder.TryGetPropertyValue(stepName, out next))
                    return false;
            }
            else
            {
                if (current is not JsonArray list || stepIndex >= list.Count)
                    return false;
                next = list[stepIndex];
            }

            if (position >= path.Length)
            {
                parent = current;
                name = stepName;
                index = stepIndex;
                return true;
            }

            current = next;
        }

        return false;
    }
}
