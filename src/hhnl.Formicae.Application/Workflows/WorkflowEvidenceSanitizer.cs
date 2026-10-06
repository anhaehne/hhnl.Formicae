using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace hhnl.Formicae.Application.Workflows;

public static partial class WorkflowEvidenceSanitizer
{
    public static JsonNode? Sanitize(object evidence)
    {
        var json = JsonSerializer.SerializeToNode(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Redact(json);
        return json;
    }
    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (SensitiveKey().IsMatch(key)) { obj[key] = "[REDACTED]"; continue; }
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (key.EndsWith("Json", StringComparison.OrdinalIgnoreCase))
                    {
                        try { var nested = JsonNode.Parse(text); Redact(nested); obj[key] = nested?.ToJsonString(); continue; }
                        catch (JsonException) { }
                    }
                    obj[key] = RedactText(text);
                }
                else Redact(obj[key]);
            }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) array[index] = RedactText(text);
                else Redact(array[index]);
            }
    }
    public static string RedactText(string text)
    {
        if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
        {
            try { var json = JsonNode.Parse(text); Redact(json); return json?.ToJsonString() ?? text; }
            catch (JsonException) { }
        }
        return CredentialAssignment().Replace(Bearer().Replace(text, "Bearer [REDACTED]"), "$1[REDACTED]");
    }
    [GeneratedRegex("^(?:.*[_-])?(?:api[_-]?key|llmApiKey|password|authorization|auth|token|secret|private[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|credential(?:s|Json)?|subscriptionCredentialJson|codexAuthJson)$", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKey();
    [GeneratedRegex("Bearer\\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();
    [GeneratedRegex("((?:api[_-]?key|password|access[_-]?token|refresh[_-]?token|client[_-]?secret)\\s*[=:]\\s*)[^\\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialAssignment();
}
