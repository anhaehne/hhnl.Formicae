using System.Text.Json;

namespace hhnl.Formicae.Application.Workflows;

public sealed record AgentTaskOutputResult(bool Succeeded, string? Output, string? FailureReason, int CorrectionTurns);

// Collect one CLI turn, rather than searching accumulated logs that may contain earlier responses.
public sealed class AgentTaskOutputCollector(bool codex)
{
    public string? ConversationId { get; private set; }
    private string? response;
    private bool completed;
    public string? FinalResponse => completed ? response : null;

    public void Observe(string line)
    {
        line = line.Trim();
        if (!codex && line.StartsWith("Conversation ID: ", StringComparison.Ordinal)
            && Guid.TryParse(line[17..].Trim(), out var conversation))
            ConversationId = conversation.ToString("N");
        if (line.Length > 1048576) { response = null; completed = false; return; }
        if (!line.StartsWith('{')) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (codex)
            {
                var type = String(root, "type");
                if (type == "thread.started") ConversationId = String(root, "thread_id");
                if (type == "turn.started") { response = null; completed = false; }
                if (type == "item.completed" && Object(root, "item", out var item) && String(item, "type") == "agent_message")
                    response = Bounded(String(item, "text"));
                if (type == "turn.completed") completed = true;
                if (type == "turn.failed") { response = null; completed = false; }
            }
            else if (String(root, "source") == "agent")
            {
                if (String(root, "kind") == "ActionEvent" && Object(root, "action", out var action) && String(action, "kind") == "FinishAction")
                {
                    response = Bounded(String(action, "message"));
                    completed = true;
                }
                else if (String(root, "action") == "finish")
                {
                    response = Object(root, "args", out var args)
                        ? Bounded(String(args, "final_thought") ?? (Object(args, "outputs", out var outputs) ? String(outputs, "content") : null)) : null;
                    completed = true;
                }
            }
        }
        catch (JsonException) { }
    }

    private static string? Bounded(string? text) => text?.Length > 262144 ? text[..262145] : text;
    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool Object(JsonElement element, string name, out JsonElement property)
        => element.TryGetProperty(name, out property) && property.ValueKind == JsonValueKind.Object;
}
