using System.Text.Json;
using hhnl.Formicae.Infrastructure;
using hhnl.Formicae.Infrastructure.Containers;
using Microsoft.Extensions.Options;

namespace hhnl.Formicae.Tests;

public sealed class ContainerExecutionIdentityTests
{
    private const string JobName = "formicae-plan-durable";
    private static RuntimeJobSpec Spec() => new(JobName, "worker:test", new Dictionary<string, string>(), ["worker"], ReuseExisting: true);
    private static string Inspect(string owner = "formicae", string job = JobName, string status = "running") => JsonSerializer.Serialize(new[]
    {
        new { Config = new { Labels = new Dictionary<string, string> { ["formicae.managed-by"] = owner, ["formicae.job"] = job } },
            State = new { Status = status, Running = status == "running", ExitCode = 0 } }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_durable_container_attaches_without_rewriting_context_or_secrets(bool race)
    {
        var root = Path.Combine(Path.GetTempPath(), $"formicae-identity-{Guid.NewGuid():N}");
        var context = Path.Combine(root, JobName, "context", "prompt.md");
        var secret = Path.Combine(root, JobName, "secrets", "auth", "auth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(context)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
        await File.WriteAllTextAsync(context, "original context");
        await File.WriteAllTextAsync(secret, "original secret");
        try
        {
            var cli = new Cli { RunResult = new(125, "", "name already in use") };
            if (race) cli.Inspections.Enqueue(new(1, "", "no such container"));
            cli.Inspections.Enqueue(new(0, Inspect(), ""));
            var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions { WorkspaceRoot = root }), []);
            var result = await runtime.StartJobAsync(Spec() with
            {
                ContextFiles = [new("prompt.md", "new context")],
                SecretFiles = [new("auth", "/auth", new Dictionary<string, string> { ["auth.json"] = "new secret" })]
            }, default);
            Assert.Equal(JobName, result.ExternalId);
            Assert.Equal("original context", await File.ReadAllTextAsync(context));
            Assert.Equal("original secret", await File.ReadAllTextAsync(secret));
            Assert.Equal(race ? new[] { "inspect", "run", "inspect" } : new[] { "inspect" }, cli.Commands);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Concurrent_name_conflict_attaches_to_matching_managed_container()
    {
        var cli = new Cli { RunResult = new(125, "", "name already in use") };
        cli.Inspections.Enqueue(new(1, "", "no such container"));
        cli.Inspections.Enqueue(new(0, Inspect(), ""));
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions()), []);
        Assert.Equal(JobName, (await runtime.StartJobAsync(Spec(), default)).ExternalId);
        Assert.Equal(new[] { "inspect", "run", "inspect" }, cli.Commands);
    }

    [Theory]
    [InlineData("someone-else", JobName)]
    [InlineData("formicae", "different-job")]
    public async Task Name_conflict_with_unrelated_container_is_rejected(string owner, string job)
    {
        var cli = new Cli();
        cli.Inspections.Enqueue(new(0, Inspect(owner, job), ""));
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions()), []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartJobAsync(Spec(), default));
        Assert.Equal(new[] { "inspect" }, cli.Commands);
    }

    [Fact]
    public async Task Missing_container_and_failed_launch_is_a_real_failure()
    {
        var cli = new Cli { RunResult = new(125, "", "invalid image") };
        cli.Inspections.Enqueue(new(1, "", "no such container"));
        cli.Inspections.Enqueue(new(1, "", "no such container"));
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions()), []);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartJobAsync(Spec(), default));
        Assert.Contains("invalid image", error.Message);
    }

    [Fact]
    public async Task Created_but_not_started_container_is_started_instead_of_recreated()
    {
        var cli = new Cli();
        cli.Inspections.Enqueue(new(0, Inspect(status: "created"), ""));
        var runtime = new ContainerJobRuntime(cli, Options.Create(new ContainerRuntimeOptions()), []);
        Assert.Equal(JobName, (await runtime.StartJobAsync(Spec(), default)).ExternalId);
        Assert.Equal(new[] { "inspect", "start" }, cli.Commands);
    }

    private sealed class Cli : IContainerCli
    {
        public List<string> Commands { get; } = [];
        public Queue<ContainerCliResult> Inspections { get; } = new();
        public ContainerCliResult RunResult { get; init; } = new(0, "container-id", "");
        public Task<ContainerCliResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Commands.Add(arguments[0]);
            return Task.FromResult(arguments[0] == "inspect" ? Inspections.Dequeue() : RunResult);
        }
    }
}
