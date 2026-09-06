using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal sealed record CliModel(string Id, string DisplayName, bool IsDefault);

internal static class CodexModelDiscovery
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("npx")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        foreach (var arg in new[] { "-y", "@openai/codex", "app-server", "--listen", "stdio://" }) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            CodexWorkspace.Prepare(false);
            var models = await ExecuteAsync(process, TimeSpan.FromSeconds(90), cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(new { type = "formicae.models", models }, JsonSerializerOptions.Web));
            return 0;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("CLI model discovery failed. Verify authentication and retry.");
            return 1;
        }
    }

    internal static async Task<IReadOnlyList<CliModel>> ExecuteAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        process.Start();
        var drain = DrainAsync(process.StandardError, deadline.Token);
        try { return await DiscoverAsync(process.StandardOutput, process.StandardInput, deadline.Token); }
        finally
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
            catch (InvalidOperationException) { }
            await deadline.CancelAsync();
            await drain;
        }
    }

    private static async Task DrainAsync(TextReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { } }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    internal static async Task<IReadOnlyList<CliModel>> DiscoverAsync(TextReader reader, TextWriter writer, CancellationToken token)
    {
        var id = 1;
        await RequestAsync("initialize", new { clientInfo = new { name = "formicae", version = "1" } });
        await writer.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), token);
        await writer.FlushAsync(token);
        var models = new Dictionary<string, CliModel>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var result = await RequestAsync("model/list", new { limit = 100, cursor, includeHidden = false });
            foreach (var entry in result.GetProperty("data").EnumerateArray())
            {
                var model = entry.GetProperty("model").GetString();
                var name = entry.GetProperty("displayName").GetString();
                if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || string.IsNullOrWhiteSpace(name) || name.Length > 256)
                    throw new InvalidDataException("Invalid model entry.");
                models[model] = new(model, name, entry.TryGetProperty("isDefault", out var isDefault) && isDefault.GetBoolean());
                if (models.Count > 2000) throw new InvalidDataException("Model catalog exceeds limit.");
            }
            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (cursor is not null && (!seenCursors.Add(cursor) || seenCursors.Count > 100)) throw new InvalidDataException("Invalid pagination.");
        } while (cursor is not null);
        return models.Values.ToArray();

        async Task<JsonElement> RequestAsync(string method, object parameters)
        {
            var requestId = id++;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = requestId, method, @params = parameters }).AsMemory(), token);
            await writer.FlushAsync(token);
            for (var messages = 0; messages < 1000; messages++)
            {
                var line = await ReadProtocolLineAsync(reader, token);
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                if (!root.TryGetProperty("id", out var responseId)) continue;
                if (!responseId.TryGetInt32(out var receivedId) || receivedId != requestId) throw new InvalidDataException("Unexpected response.");
                if (root.TryGetProperty("error", out _)) throw new InvalidDataException("CLI request failed.");
                return root.GetProperty("result").Clone();
            }
            throw new InvalidDataException("Too many protocol notifications.");
        }
    }

    private static async Task<string> ReadProtocolLineAsync(TextReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token) > 0)
        {
            if (character[0] == '\n') return line.ToString();
            if (line.Length >= 1024 * 1024) throw new InvalidDataException("Protocol message exceeds limit.");
            line.Append(character[0]);
        }
        if (line.Length > 0) return line.ToString();
        throw new EndOfStreamException();
    }
}
