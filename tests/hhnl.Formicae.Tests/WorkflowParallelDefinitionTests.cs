using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class WorkflowParallelDefinitionTests
{
    private static WorkflowDefinitionDocument Document() => new(
        DefaultWorkflowDefinitions.V1Alpha3Schema, "fork",
        [
            new("fork", WorkflowParallelDefinitions.Uses, "after", Parallel: new(["left", "right"])),
            new("left", "builtins.plan", "left-end"),
            new("left-end", "builtins.plan", "fork", NextStepPort: "join"),
            new("right", "builtins.plan", "fork", NextStepPort: "join"),
            new("after", "builtins.implement")
        ]);

    private static WorkflowDefinitionDocument Change(WorkflowDefinitionDocument document, string id,
        Func<WorkflowDefinitionStep, WorkflowDefinitionStep> change) => document with
        { Steps = document.Steps.Select(step => step.Id == id ? change(step) : step).ToArray() };

    private static void AssertInvalid(WorkflowDefinitionDocument document, string? nodeId = null)
    {
        var result = new WorkflowDefinitionValidator().Validate(document);
        Assert.False(result.IsValid);
        if (nodeId is not null) Assert.Contains(result.Errors, error => error.NodeId == nodeId);
    }

    [Fact]
    public void Disjoint_sequential_planning_branches_validate_and_keep_configured_order()
    {
        var document = Document();
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        var branches = WorkflowParallelDefinitions.Branches(document, document.Steps[0]);
        Assert.Equal(new[] { "left", "left-end" }, branches[0].Select(step => step.Id));
        Assert.Equal(new[] { "right" }, branches[1].Select(step => step.Id));
    }

    [Fact]
    public void Eight_branches_are_supported()
    {
        var entries = Enumerable.Range(0, 8).Select(i => $"branch-{i}").ToArray();
        var document = new WorkflowDefinitionDocument(DefaultWorkflowDefinitions.V1Alpha3Schema, "fork",
            [new("fork", WorkflowParallelDefinitions.Uses, "after", Parallel: new(entries)),
             .. entries.Select(id => new WorkflowDefinitionStep(id, "builtins.plan", "fork", NextStepPort: "join")),
             new("after", "builtins.plan")]);
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
    }

    [Fact]
    public void Loop_after_parallel_group_remains_valid_and_normalizes_separately()
    {
        var document = Change(Document(), "fork", step => step with { NextStepId = "loop" });
        document = document with
        {
            Steps = [.. document.Steps, new("loop", WorkflowNodeDefinitions.LoopUses, "after", Loop: new("body", 2, 2)),
                new("body", "builtins.plan", "loop", NextStepPort: "return")]
        };
        Assert.True(new WorkflowDefinitionValidator().Validate(document).IsValid);
        var normalized = WorkflowNodeDefinitions.Normalize(document);
        Assert.Single(normalized.Loops!);
        Assert.Equal("body", normalized.Steps.Single(step => step.Id == "fork").NextStepId);
        Assert.Equal("join", normalized.Steps.Single(step => step.Id == "right").NextStepPort);
    }

    [Fact]
    public void Normalization_and_json_round_trip_preserve_parallel_node_and_join_links()
    {
        var persisted = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(Document()))!;
        var normalized = WorkflowNodeDefinitions.Normalize(persisted);
        Assert.Equal(DefaultWorkflowDefinitions.V1Alpha2Schema, normalized.Schema);
        Assert.Equal("fork", normalized.StartStepId);
        var group = Assert.Single(normalized.Steps, step => step.Uses == WorkflowParallelDefinitions.Uses);
        Assert.Equal(new[] { "left", "right" }, group.Parallel!.BranchStepIds);
        Assert.Equal("after", group.NextStepId);
        foreach (var id in new[] { "left-end", "right" })
        {
            var terminal = Assert.Single(normalized.Steps, step => step.Id == id);
            Assert.Equal("fork", terminal.NextStepId);
            Assert.Equal("join", terminal.NextStepPort);
        }
        Assert.Equal("left-end", normalized.Steps.Single(step => step.Id == "left").NextStepId);
        Assert.Equal(WorkflowDefinitionJson.Serialize(normalized), WorkflowDefinitionJson.Serialize(WorkflowNodeDefinitions.Normalize(normalized)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void Branch_count_outside_supported_bounds_is_rejected(int count)
        => AssertInvalid(Change(Document(), "fork", step => step with
        { Parallel = new(Enumerable.Range(0, count).Select(i => $"branch-{i}").ToArray()) }), "fork");

    [Theory]
    [InlineData("left", "left")]
    [InlineData("left", "")]
    [InlineData("left", "missing")]
    public void Duplicate_empty_and_missing_branch_entries_are_rejected(string first, string second)
        => AssertInvalid(Change(Document(), "fork", step => step with { Parallel = new([first, second]) }), "fork");

    [Fact]
    public void Shared_branch_suffix_is_rejected()
        => AssertInvalid(Change(Document(), "right", step => step with { NextStepId = "left-end", NextStepPort = null }), "left-end");

    [Theory]
    [InlineData(null, null)]
    [InlineData("after", null)]
    [InlineData("fork", null)]
    [InlineData("fork", "return")]
    [InlineData("missing", "join")]
    public void Every_branch_must_end_at_its_owning_join(string? target, string? port)
        => AssertInvalid(Change(Document(), "right", step => step with { NextStepId = target, NextStepPort = port }), "fork");

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    [InlineData("fork")]
    [InlineData("left")]
    [InlineData("left-end")]
    public void Parallel_next_must_exist_outside_all_branches(string? next)
        => AssertInvalid(Change(Document(), "fork", step => step with { NextStepId = next }), "fork");

    [Theory]
    [InlineData("left")]
    [InlineData("left-end")]
    public void Manual_start_cannot_enter_branch_body(string start)
        => AssertInvalid(Document() with { StartStepId = start }, start);

    [Theory]
    [InlineData("left")]
    [InlineData("left-end")]
    public void External_next_connection_cannot_enter_branch_body(string target)
    {
        var document = Document();
        AssertInvalid(document with { Steps = [.. document.Steps, new("external", "builtins.plan", target)] }, "external");
    }

    [Theory]
    [InlineData("builtins.implement")]
    [InlineData("builtins.create-pull-request")]
    [InlineData("builtins.address-comments")]
    [InlineData("builtins.loop")]
    [InlineData("builtins.parallel")]
    public void Branches_reject_mutating_tasks_and_nested_control_nodes(string uses)
        => AssertInvalid(Change(Document(), "right", step => step with { Uses = uses }), "right");

    [Fact]
    public void Parallel_group_cannot_be_nested_in_loop_body()
    {
        var document = Change(Document(), "after", step => step with { NextStepId = "loop", NextStepPort = "return" });
        AssertInvalid(document with
        {
            StartStepId = "loop",
            Steps = [.. document.Steps, new("loop", WorkflowNodeDefinitions.LoopUses, "finish", Loop: new("fork", 2, 2)), new("finish", "builtins.plan")]
        }, "loop");
    }

    [Fact]
    public void Parallel_settings_on_task_are_rejected()
        => AssertInvalid(Change(Document(), "after", step => step with { Parallel = new(["left", "right"]) }), "after");

    [Fact]
    public void Parallel_node_requires_parallel_settings()
        => AssertInvalid(Change(Document(), "fork", step => step with { Parallel = null }), "fork");

    [Theory]
    [InlineData("ai")]
    [InlineData("model")]
    [InlineData("loop")]
    [InlineData("trigger")]
    [InlineData("port")]
    public void Parallel_node_rejects_unrelated_control_and_agent_settings(string setting)
        => AssertInvalid(Change(Document(), "fork", step => setting switch
        {
            "ai" => step with { AiSettingsId = "configured-agent" },
            "model" => step with { Model = "model" },
            "loop" => step with { Loop = new("left", 2, 2) },
            "trigger" => step with { Trigger = new(default, false, [], null) },
            _ => step with { NextStepPort = "return" }
        }), "fork");

    [Fact]
    public void Join_from_outside_group_is_rejected()
        => AssertInvalid(Change(Document(), "after", step => step with { NextStepId = "fork", NextStepPort = "join" }), "after");

    [Fact]
    public void Branch_cycle_is_rejected_without_hanging()
        => AssertInvalid(Change(Document(), "left-end", step => step with { NextStepId = "left", NextStepPort = null }), "fork");
}
