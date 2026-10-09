using System.Text.Json;
using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Fakes;
using hhnl.Formicae.Infrastructure.OpenHands;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class CodexAuthSetupTests
{
    [Theory]
    [InlineData("E4UQ-ZWLG0", false, false)]
    [InlineData("ABCD-EFGH", false, true)]
    [InlineData("ABCDE-FGHIJ", true, false)]
    [InlineData("123456", true, true)]
    [InlineData("abcd-efgh", true, true)]
    public async Task Status_extracts_prompt_code_from_raw_and_worker_logs(string code, bool wrapped, bool running)
    {
        var lines = new[]
        {
            "Unrelated identifier TEST-12345",
            "1. Open this link in your browser and sign in to your account",
            "   \u001b[94mhttps://auth.openai.com/codex/device\u001b[0m",
            "2. Enter this one-time code \u001b[90m(expires in 15 minutes)\u001b[0m",
            "   \u001b[94m" + code + "\u001b[0m",
            "Continue only if you started this login in Codex."
        };
        var output = string.Join("\r\n", lines.Select(line => wrapped ? Wrap(line) : line));

        var status = await CreateService(output, running).GetStatusAsync("codex-ai", "login-job", CancellationToken.None);

        Assert.Equal(running ? "Running" : "Succeeded", status.Status);
        Assert.Equal("https://auth.openai.com/codex/device", status.DeviceLoginUrl);
        Assert.Equal(code, status.DeviceLoginCode);
        Assert.DoesNotContain("formicaeLog", status.Output);
        Assert.DoesNotContain('\u001b', status.Output);
        Assert.Contains(code, status.Output);
    }

    [Theory]
    [InlineData("Worker identifier TEST-12345")]
    [InlineData("https://auth.openai.com/codex/device\nWorker identifier TEST-12345")]
    [InlineData("2. Enter this one-time code (expires in 15 minutes)\n")]
    [InlineData("2. Enter this one-time code (expires in 15 minutes)\nContinue only if you started this login in Codex.")]
    public async Task Status_does_not_extract_unrelated_text_or_an_incomplete_prompt(string output)
    {
        var status = await CreateService(output, true).GetStatusAsync("codex-ai", "login-job", CancellationToken.None);

        Assert.Null(status.DeviceLoginCode);
    }

    private static string Wrap(string line) => JsonSerializer.Serialize(new
    {
        formicaeLog = 1,
        data = new { line, stream = "stdout", messageId = Guid.NewGuid(), timestamp = DateTimeOffset.UtcNow, sequence = 1 }
    });

    private static CodexAuthSetupService CreateService(string output, bool running) => new(
        new LoginRuntime(output, running),
        Options.Create(new RuntimeJobOptions()),
        Options.Create(new OpenHandsOptions()),
        new AiSettingsService(new InMemoryAiSettingsStore(), Options.Create(new OpenHandsOptions()), new SystemClock()));

    private sealed class LoginRuntime(string output, bool running) : IJobRuntime
    {
        public Task<RuntimeJobStartResult> StartJobAsync(RuntimeJobSpec spec, CancellationToken cancellationToken)
            => Task.FromResult(new RuntimeJobStartResult(spec.Name));

        public Task<RuntimeJobResult?> TryGetJobResultAsync(string externalId, CancellationToken cancellationToken)
            => Task.FromResult<RuntimeJobResult?>(running ? null : new RuntimeJobResult(true, externalId, output, null));

        public Task<string> ReadJobLogsAsync(string externalId, CancellationToken cancellationToken)
            => Task.FromResult(output);
    }
}
