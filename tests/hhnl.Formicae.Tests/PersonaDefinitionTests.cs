using hhnl.Formicae.Application.Workflows;
using hhnl.Formicae.Infrastructure.Fakes;

namespace hhnl.Formicae.Tests;

public sealed class PersonaDefinitionTests
{
    private static readonly PersonaSnapshot Forged = new("forged", 99, "Untrusted", "client instructions", "tone", "constraints");
    private static WorkflowDefinitionDocument Document(string? defaultId = null) => new(DefaultWorkflowDefinitions.V1Alpha3Schema, "plan",
        [new("plan", "builtins.plan", "implement"), new("implement", "builtins.implement", "review"),
            new("review", "builtins.address-comments", "pr"), new("pr", "builtins.create-pull-request")], DefaultPersonaId: defaultId);
    private static Persona Custom(string id = "custom", int revision = 3) => new()
    { Id = id, Revision = revision, Name = $"Persona {id}", Instructions = "Inspect the evidence.", Tone = "Concise", OperatingConstraints = "Describe limitations." };
    private static WorkflowDefinitionDocument Change(WorkflowDefinitionDocument document, string id, Func<WorkflowDefinitionStep, WorkflowDefinitionStep> change)
        => document with { Steps = document.Steps.Select(step => step.Id == id ? change(step) : step).ToArray() };

    [Fact]
    public async Task Workflow_inheritance_resolves_each_distinct_catalog_revision_once()
    {
        var store = new Store(Custom());
        store.ChangeRevisionAfterRead = true;
        var result = await PersonaDefinitions.ResolveAsync(Document("custom"), new PersonaService(store), default);
        Assert.True(result.Validation.IsValid);
        Assert.Equal(1, store.Reads["custom"]);
        Assert.All(result.Document.Steps.Where(step => PersonaDefinitions.IsAiTask(step.Uses)), step =>
        {
            Assert.Equal("custom", step.PersonaSnapshot!.Id);
            Assert.Equal(3, step.PersonaSnapshot.Revision);
            Assert.Null(step.PersonaId);
        });
        Assert.Null(result.Document.Steps.Single(step => step.Id == "pr").PersonaSnapshot);
        Assert.True(PersonaDefinitions.ValidateRuntime(result.Document).IsValid);
    }

    [Fact]
    public async Task Explicit_custom_override_and_default_opt_out_take_precedence()
    {
        var document = Change(Document("custom"), "implement", step => step with { PersonaId = "other" });
        document = Change(document, "review", step => step with { PersonaId = "default" });
        var store = new Store(Custom(), Custom("other", 5));
        var result = await PersonaDefinitions.ResolveAsync(document, new PersonaService(store), default);
        Assert.True(result.Validation.IsValid);
        Assert.Equal("custom", result.Document.Steps[0].PersonaSnapshot!.Id);
        Assert.Equal("other", result.Document.Steps[1].PersonaSnapshot!.Id);
        Assert.Equal(5, result.Document.Steps[1].PersonaSnapshot!.Revision);
        Assert.Equal(PersonaService.DefaultSnapshot, result.Document.Steps[2].PersonaSnapshot);
        Assert.Equal(2, store.Reads.Count);
    }

    [Fact]
    public async Task Client_snapshots_are_replaced_and_never_authorize_missing_personas()
    {
        var document = Document("custom") with { Steps = Document("custom").Steps.Select(step => step with { PersonaSnapshot = Forged }).ToArray() };
        document = Change(document, "review", step => step with { PersonaId = "unknown" });
        var result = await PersonaDefinitions.ResolveAsync(document, new PersonaService(new Store(Custom())), default);
        Assert.False(result.Validation.IsValid);
        Assert.Equal("custom", result.Document.Steps[0].PersonaSnapshot!.Id);
        Assert.Null(result.Document.Steps[2].PersonaSnapshot);
        Assert.Equal("unknown", result.Document.Steps[2].PersonaId);
        Assert.Null(result.Document.Steps[3].PersonaSnapshot);
        Assert.Contains(result.Validation.Errors, error => error.NodeId == "review");
    }

    [Fact]
    public async Task Missing_workflow_default_has_workflow_and_inheriting_node_errors_but_resolves_overrides()
    {
        var document = Change(Document("missing"), "implement", step => step with { PersonaId = "custom" });
        var store = new Store(Custom());
        var result = await PersonaDefinitions.ResolveAsync(document, new PersonaService(store), default);
        Assert.Contains(result.Validation.Errors, error => error.Path == "defaultPersonaId" && error.NodeId is null);
        Assert.Contains(result.Validation.Errors, error => error.NodeId == "plan");
        Assert.Contains(result.Validation.Errors, error => error.NodeId == "review");
        Assert.Equal("custom", result.Document.Steps[1].PersonaSnapshot!.Id);
        Assert.Equal("missing", result.Document.DefaultPersonaId);
        Assert.Equal(1, store.Reads["missing"]);
    }

    [Theory]
    [InlineData("builtins.create-pull-request")]
    [InlineData("builtins.trigger")]
    [InlineData("builtins.loop")]
    [InlineData("builtins.parallel")]
    [InlineData("builtins.decision")]
    public async Task Non_ai_nodes_reject_selection_and_strip_submitted_snapshots(string uses)
    {
        var document = Document() with { Steps = [new("node", uses, PersonaId: "default", PersonaSnapshot: Forged)] };
        var result = await PersonaDefinitions.ResolveAsync(document, null, default);
        Assert.Contains(result.Validation.Errors, error => error.NodeId == "node");
        Assert.Null(result.Document.Steps[0].PersonaSnapshot);
        Assert.Equal("default", result.Document.Steps[0].PersonaId);
        Assert.False(PersonaDefinitions.ValidateRuntime(result.Document).IsValid);
    }

    [Fact]
    public async Task Workflow_default_is_allowed_without_ai_nodes_and_still_validated()
    {
        var document = Document("custom") with { Steps = [new("pr", "builtins.create-pull-request")] };
        var result = await PersonaDefinitions.ResolveAsync(document, new PersonaService(new Store(Custom())), default);
        Assert.True(result.Validation.IsValid);
        Assert.Null(result.Document.Steps[0].PersonaSnapshot);
        Assert.True(PersonaDefinitions.ValidateRuntime(result.Document).IsValid);
        Assert.False((await PersonaDefinitions.ResolveAsync(document with { DefaultPersonaId = "missing" }, null, default)).Validation.IsValid);
    }

    [Fact]
    public async Task Enrichment_preserves_graph_settings_and_editor_layout()
    {
        var editor = new WorkflowEditorMetadata(new Dictionary<string, WorkflowEditorPosition> { ["plan"] = new(21, 34) }, new(8, 13, .5));
        var document = Change(Document("custom") with { Editor = editor }, "plan", step => step with
        { DisplayName = "Planning", Model = "cli-model", AiSettingsId = "agent", NextStepPort = "join" });
        var result = await PersonaDefinitions.ResolveAsync(document, new PersonaService(new Store(Custom())), default);
        Assert.Same(editor, result.Document.Editor);
        Assert.Equal(document.StartStepId, result.Document.StartStepId);
        Assert.Equal(document.Steps[0], result.Document.Steps[0] with { PersonaSnapshot = null });
        var persisted = WorkflowDefinitionJson.Deserialize(WorkflowDefinitionJson.Serialize(result.Document))!;
        Assert.Equal(result.Document.Steps[0].PersonaSnapshot, persisted.Steps[0].PersonaSnapshot);
    }

    [Fact]
    public async Task Pinned_snapshot_remains_valid_after_catalog_edit_or_delete()
    {
        var store = new Store(Custom());
        var service = new PersonaService(store);
        var first = await PersonaDefinitions.ResolveAsync(Document("custom"), service, default);
        store.Values["custom"] = Custom(revision: 4) with { Instructions = "Changed guidance" };
        var second = await PersonaDefinitions.ResolveAsync(Document("custom"), service, default);
        Assert.Equal(3, first.Document.Steps[0].PersonaSnapshot!.Revision);
        Assert.Equal(4, second.Document.Steps[0].PersonaSnapshot!.Revision);
        store.Values["custom"] = store.Values["custom"] with { IsDeleted = true };
        Assert.True(PersonaDefinitions.ValidateRuntime(first.Document).IsValid);
        Assert.True(PersonaDefinitions.ValidateRuntime(second.Document).IsValid);
        var deleted = await PersonaDefinitions.ResolveAsync(first.Document, service, default);
        Assert.False(deleted.Validation.IsValid);
        Assert.Null(deleted.Document.Steps[0].PersonaSnapshot);
        Assert.Equal("custom", deleted.Document.DefaultPersonaId);
    }

    [Fact]
    public async Task Version_save_resolves_once_and_pinned_version_executes_after_catalog_deletion()
    {
        var personas = new Store(Custom()) { ChangeRevisionAfterRead = true };
        var workflows = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(workflows, new(), personas: new PersonaService(personas));
        var definition = await service.CreateAsync(new("Pinned persona"), default);
        var first = await service.CreateVersionAsync(definition.Id, new(null, true, false, Document("custom")), default);
        Assert.Equal(1, personas.Reads["custom"]);
        var firstDocument = WorkflowDefinitionJson.Deserialize((await workflows.GetWorkflowDefinitionVersionAsync(first.Id, default))!.DefinitionJson)!;
        Assert.All(firstDocument.Steps.Where(step => PersonaDefinitions.IsAiTask(step.Uses)), step => Assert.Equal(3, step.PersonaSnapshot!.Revision));
        var second = await service.CreateVersionAsync(definition.Id, new(null, true, false, Document("custom")), default);
        var secondDocument = WorkflowDefinitionJson.Deserialize((await workflows.GetWorkflowDefinitionVersionAsync(second.Id, default))!.DefinitionJson)!;
        Assert.Equal(4, secondDocument.Steps[0].PersonaSnapshot!.Revision);
        personas.Values["custom"] = personas.Values["custom"] with { IsDeleted = true };
        Assert.Equal(first.Id, (await service.ResolveForRunAsync(definition.Id, first.Id, default)).Id);
        Assert.Equal(2, personas.Reads["custom"]);
        var normalized = WorkflowNodeDefinitions.Normalize(firstDocument);
        Assert.Equal("custom", normalized.DefaultPersonaId);
        Assert.Equal(firstDocument.Steps[0].PersonaSnapshot, normalized.Steps[0].PersonaSnapshot);
    }

    [Fact]
    public async Task Disabled_unresolved_draft_saves_ids_and_resolved_snapshots_but_enabled_save_fails()
    {
        var workflows = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(workflows, new(), personas: new PersonaService(new Store(Custom())));
        var definition = await service.CreateAsync(new("Incomplete persona"), default);
        var document = Change(Document("custom"), "review", step => step with { PersonaId = "missing", PersonaSnapshot = Forged });
        var saved = await service.CreateVersionAsync(definition.Id, new(null, false, false, document), default);
        var persisted = WorkflowDefinitionJson.Deserialize((await workflows.GetWorkflowDefinitionVersionAsync(saved.Id, default))!.DefinitionJson)!;
        Assert.Equal("custom", persisted.Steps[0].PersonaSnapshot!.Id);
        Assert.Equal("missing", persisted.Steps[2].PersonaId);
        Assert.Null(persisted.Steps[2].PersonaSnapshot);
        var error = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() => service.CreateVersionAsync(definition.Id, new(null, true, false, document), default));
        Assert.Contains(error.Errors, item => item.NodeId == "review");
        Assert.Single(await workflows.ListWorkflowDefinitionVersionsAsync(definition.Id, default));
    }

    [Fact]
    public async Task Nonpersisting_validation_checks_current_catalog_without_creating_versions()
    {
        var workflows = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(workflows, new(), personas: new PersonaService(new Store()));
        var document = Change(Document(), "plan", step => step with { PersonaId = "missing", PersonaSnapshot = Forged });
        var validation = await service.ValidateAsync(document, default);
        Assert.Contains(validation.Errors, item => item.NodeId == "plan");
        Assert.Empty(await workflows.ListWorkflowDefinitionsAsync(default));
    }

    [Fact]
    public async Task Null_step_payload_is_reported_by_save_validation_and_runtime_without_throwing()
    {
        var document = WorkflowDefinitionJson.Deserialize("""{"schema":"formicae.workflow/v1alpha3","startStepId":"plan","steps":[null]}""")!;
        var resolved = await PersonaDefinitions.ResolveAsync(document, null, default);
        Assert.False(resolved.Validation.IsValid);
        Assert.False(PersonaDefinitions.ValidateRuntime(document).IsValid);
        var workflows = new InMemoryWorkflowStore();
        var service = new WorkflowDefinitionService(workflows, new());
        Assert.False((await service.ValidateAsync(document, default)).IsValid);
        var definition = await service.CreateAsync(new("Malformed payload"), default);
        await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() => service.CreateVersionAsync(definition.Id, new(null, true, false, document), default));
        Assert.Empty(await workflows.ListWorkflowDefinitionVersionsAsync(definition.Id, default));
    }

    [Theory]
    [InlineData("formicae.workflow/v1alpha1")]
    [InlineData("formicae.workflow/v1alpha2")]
    [InlineData("formicae.workflow/v1alpha3")]
    public async Task Legacy_default_needs_no_catalog_or_backfill(string schema)
    {
        var document = Document() with { Schema = schema };
        Assert.True(PersonaDefinitions.ValidateRuntime(document).IsValid);
        var resolved = await PersonaDefinitions.ResolveAsync(document, null, default);
        Assert.True(resolved.Validation.IsValid);
        Assert.Equal(PersonaService.DefaultSnapshot, resolved.Document.Steps[0].PersonaSnapshot);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("wrong-id")]
    [InlineData("revision")]
    [InlineData("name")]
    [InlineData("instructions-null")]
    [InlineData("instructions-long")]
    [InlineData("tone-long")]
    [InlineData("constraints-long")]
    public void Explicit_custom_persona_requires_matching_usable_snapshot(string scenario)
    {
        var snapshot = new PersonaSnapshot("custom", 1, "Custom", "", "", "");
        snapshot = scenario switch
        {
            "absent" => null,
            "wrong-id" => snapshot with { Id = "other" },
            "revision" => snapshot with { Revision = 0 },
            "name" => snapshot with { Name = " " },
            "instructions-null" => snapshot with { Instructions = null! },
            "instructions-long" => snapshot with { Instructions = new string('a', 16001) },
            "tone-long" => snapshot with { Tone = new string('a', 1001) },
            _ => snapshot with { OperatingConstraints = new string('a', 8001) }
        };
        var document = Document() with { Steps = [new("plan", "builtins.plan", PersonaId: "custom", PersonaSnapshot: snapshot)] };
        Assert.Contains(PersonaDefinitions.ValidateRuntime(document).Errors, error => error.NodeId == "plan");
    }

    [Fact]
    public void Forged_default_snapshot_and_unreferenced_custom_snapshot_are_rejected()
    {
        foreach (var snapshot in new[] { Forged, PersonaService.DefaultSnapshot with { Instructions = "override" }, PersonaService.DefaultSnapshot with { Revision = 2 } })
            Assert.False(PersonaDefinitions.ValidateRuntime(Document() with
            { Steps = [new("plan", "builtins.plan", PersonaSnapshot: snapshot)] }).IsValid);
    }

    private sealed class Store(params Persona[] initial) : IPersonaStore
    {
        public Dictionary<string, Persona> Values { get; } = initial.ToDictionary(persona => persona.Id, StringComparer.Ordinal);
        public Dictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);
        public bool ChangeRevisionAfterRead { get; set; }
        public Task<Persona?> GetAsync(string id, CancellationToken cancellationToken)
        {
            Reads[id] = Reads.GetValueOrDefault(id) + 1;
            Values.TryGetValue(id, out var value);
            if (value?.IsDeleted == true) value = null;
            if (value is not null && ChangeRevisionAfterRead) Values[id] = value with { Revision = value.Revision + 1 };
            return Task.FromResult(value);
        }
        public Task<IReadOnlyList<Persona>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Persona>>(Values.Values.Where(value => !value.IsDeleted).ToArray());
        public Task<Persona> CreateAsync(Persona persona, CancellationToken cancellationToken) { Values.Add(persona.Id, persona); return Task.FromResult(persona); }
        public Task<bool> TryUpdateAsync(Persona replacement, int expectedRevision, CancellationToken cancellationToken)
        {
            if (!Values.TryGetValue(replacement.Id, out var current) || current.Revision != expectedRevision) return Task.FromResult(false);
            Values[replacement.Id] = replacement;
            return Task.FromResult(true);
        }
    }
}
