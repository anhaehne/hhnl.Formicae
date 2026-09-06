using System.Text.Json;
using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class CustomTaskPreparationIntegrityTests
{
    [Theory]
    [InlineData("9007199254740992")]
    [InlineData("-9007199254740992")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("0.1234567890123456789")]
    [InlineData("0.1000000000000000000000000000001")]
    [InlineData("1e-100")]
    public void Precision_losing_numbers_are_rejected_for_defaults_and_node_inputs(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var number = parsed.RootElement;
        Assert.False(CustomTaskDefinitions.ValidateCatalog("Task", "", "{{input.n}}", [new("n", "number", DefaultValue: number)], new()).IsValid);
        var settings = new WorkflowCustomTaskSettings("task", new Dictionary<string, JsonElement> { ["n"] = number },
            new("task", 1, "Task", "", "{{input.n}}", [new("n", "number", true)], new()));
        Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.Prepare(settings, Workflow()));
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("1.25")]
    [InlineData("9007199254740991")]
    [InlineData("-9007199254740991")]
    [InlineData("1e-7")]
    [InlineData("0.1000")]
    public void Browser_roundtrip_safe_numbers_remain_valid(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var settings = new WorkflowCustomTaskSettings("task", new Dictionary<string, JsonElement> { ["n"] = parsed.RootElement },
            new("task", 1, "Task", "", "{{input.n}}", [new("n", "number", true)], new()));
        var prepared = CustomTaskDefinitions.Prepare(settings, Workflow());
        CustomTaskDefinitions.ValidatePrepared(prepared, settings);
    }

    [Fact]
    public void Corrupt_prepared_decimal_is_a_validation_failure_instead_of_a_conversion_exception()
    {
        var settings = new WorkflowCustomTaskSettings("task", new Dictionary<string, JsonElement> { ["n"] = JsonSerializer.SerializeToElement(1) },
            new("task", 1, "Task", "", "{{input.n}}", [new("n", "number", true)], new()));
        var prepared = CustomTaskDefinitions.Prepare(settings, Workflow());
        var invalid = prepared with { Inputs = new Dictionary<string, JsonElement> { ["n"] = JsonDocument.Parse("1e1000").RootElement.Clone() } };
        var error = Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.ValidatePrepared(invalid, settings));
        Assert.Contains("bounded number", error.Message);
    }

    [Fact]
    public void Repeated_large_workflow_values_are_bounded_during_rendering()
    {
        var settings = new WorkflowCustomTaskSettings("task", Snapshot: new("task", 1, "Task", "",
            string.Concat(Enumerable.Repeat("{{workflow.planArtifact}}", 500)), [], new()));
        var workflow = Workflow(); workflow.PlanArtifact = new string('x', 131072);
        var error = Assert.Throws<InvalidOperationException>(() => CustomTaskDefinitions.Prepare(settings, workflow));
        Assert.Contains("131072 UTF-8 bytes", error.Message);
    }

    private static Workflow Workflow() => new() { IssueUrl = "https://github.com/org/repo/issues/1", RepositoryUrl = "https://github.com/org/repo" };
}
