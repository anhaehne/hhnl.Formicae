using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

internal static partial class WorkerCommand
{
    private const int MaximumOutputCorrections = 2;

    internal static async Task<int> RunOutputTaskAsync(WorkerEnvironment environment, string directory, WorkerReporter reporter,
        CancellationToken token, Func<string, IReadOnlyList<string>, string, CancellationToken, Action<string>, Task<int>>? execute = null)
    {
        execute ??= (file, arguments, workingDirectory, cancellation, observer) =>
            RunProcessAsync(file, arguments, workingDirectory, reporter, cancellation, stdoutObserver: observer);
        var schema = environment.OutputSchema!;
        var prompt = CustomTaskDefinitions.EnsureOutputInstruction(environment.Prompt, schema);
        string? conversationId = null;
        for (var correction = 0; correction <= MaximumOutputCorrections; correction++)
        {
            token.ThrowIfCancellationRequested();
            var collector = new AgentTaskOutputCollector(environment.UsesCodexSubscription);
            var arguments = correction == 0
                ? environment.UsesCodexSubscription ? BuildCodexArguments(environment with { Prompt = prompt }, directory)
                    : new List<string> { "--headless", "--json", "--override-with-envs", "-t", prompt }
                : BuildOutputCorrectionArguments(environment, directory, conversationId!, prompt);
            var exit = await execute(environment.UsesCodexSubscription ? "npx" : "openhands", arguments, directory, token,
                line => collector.Observe(reporter.Sanitize(line)));
            if (environment.UsesCodexSubscription)
                await reporter.ReportCodexAuthAsync(environment.AiSettingsId, ReadCodexAuth(), token);
            token.ThrowIfCancellationRequested();
            if (exit != 0) return exit;
            conversationId ??= collector.ConversationId;
            string error;
            try
            {
                var response = collector.FinalResponse ?? throw new InvalidOperationException("The agent did not emit an authoritative final response.");
                var outputs = CustomTaskDefinitions.ParseOutputs(response, schema);
                await ReportOutputResultAsync(reporter, new(true, JsonSerializer.Serialize(outputs, JsonSerializerOptions.Web), null, correction), token);
                return 0;
            }
            catch (InvalidOperationException exception) { error = exception.Message; }
            await reporter.ReportAsync("worker-output-validation", $"Output validation failed after {correction} correction turn(s): {error}", token);
            if (correction == MaximumOutputCorrections || string.IsNullOrWhiteSpace(conversationId))
            {
                var reason = string.IsNullOrWhiteSpace(conversationId)
                    ? $"Output validation failed; the CLI did not provide a conversation ID for correction. {error}"
                    : $"Output validation failed after {MaximumOutputCorrections} correction turns. {error}";
                await ReportOutputResultAsync(reporter, new(false, null, reason, correction), token);
                await reporter.ReportAsync("worker-error", reason, token);
                return 1;
            }
            prompt = $"Your completed task's output could not be extracted or validated: {error}\n"
                + "Use the work already completed in this conversation and return only the corrected final output. "
                + "Do not repeat the original task or start new work.\n" + CustomTaskDefinitions.OutputInstruction(schema);
            // Runtime logs and callbacks retain the limit and prompt before a new turn starts.
            // Orchestration restart polls this same worker; Kubernetes never restarts its process.
            await reporter.ReportAsync("worker-output-correction", $"Correction turn {correction + 1}/{MaximumOutputCorrections} in conversation {conversationId}:\n{prompt}", token);
        }
        throw new InvalidOperationException("Output correction loop exceeded its bound.");
    }

    internal static List<string> BuildOutputCorrectionArguments(WorkerEnvironment environment, string directory, string conversationId, string prompt)
    {
        if (!environment.UsesCodexSubscription)
            return ["--headless", "--json", "--override-with-envs", "--resume", conversationId, "-t", prompt];
        var arguments = BuildCodexArguments(environment with { Prompt = prompt }, directory);
        arguments.RemoveAt(arguments.Count - 1);
        arguments.AddRange(["resume", conversationId, prompt]);
        return arguments;
    }

    private static Task ReportOutputResultAsync(WorkerReporter reporter, AgentTaskOutputResult result, CancellationToken token)
        => reporter.ReportAsync("worker", JsonSerializer.Serialize(new { formicaeCustomResult = result }, JsonSerializerOptions.Web), token);
}
