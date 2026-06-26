using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public static class AgentMessageParser
{
    public static IReadOnlyList<AgentMessageResponse> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var messages = new List<AgentMessageResponse>();
        var sequence = 0;
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseLine(line, sequence, out var message))
            {
                return [];
            }

            messages.Add(message);
            sequence++;
        }

        return messages;
    }

    private static bool TryParseLine(string line, int sequence, out AgentMessageResponse message)
    {
        message = default!;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var content = ReadString(document.RootElement, "message")
                ?? ReadString(document.RootElement, "content")
                ?? ReadString(document.RootElement, "text")
                ?? ReadString(document.RootElement, "output");
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var role = ReadString(document.RootElement, "role")
                ?? ReadString(document.RootElement, "source")
                ?? ReadString(document.RootElement, "type");
            var createdAt = ReadDateTimeOffset(document.RootElement, "createdAt")
                ?? ReadDateTimeOffset(document.RootElement, "timestamp")
                ?? ReadDateTimeOffset(document.RootElement, "time");
            message = new AgentMessageResponse(sequence, role, content, createdAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
