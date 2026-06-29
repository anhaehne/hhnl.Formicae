namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowDefinitionService(
    IWorkflowStore store,
    WorkflowDefinitionValidator validator,
    IClock? clock = null)
{
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task EnsureDefaultWorkflowDefinitionAsync(CancellationToken cancellationToken)
    {
        if (await store.GetDefaultEnabledWorkflowDefinitionVersionAsync(cancellationToken) is not null)
        {
            return;
        }

        var (definition, version) = DefaultWorkflowDefinitions.CreateMvp(clock.UtcNow);
        var result = validator.Validate(WorkflowDefinitionJson.Deserialize(version.DefinitionJson));
        if (!result.IsValid)
        {
            throw new InvalidOperationException("Default MVP workflow definition is invalid.");
        }

        await store.EnsureDefaultWorkflowDefinitionAsync(definition, version, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowDefinitionResponse>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultWorkflowDefinitionAsync(cancellationToken);
        var definitions = await store.ListWorkflowDefinitionsAsync(cancellationToken);
        var responses = new List<WorkflowDefinitionResponse>();
        foreach (var definition in definitions)
        {
            var versions = await store.ListWorkflowDefinitionVersionsAsync(definition.Id, cancellationToken);
            responses.Add(definition.ToResponse(versions));
        }

        return responses;
    }

    public async Task<WorkflowDefinitionResponse?> GetAsync(Guid definitionId, CancellationToken cancellationToken)
    {
        await EnsureDefaultWorkflowDefinitionAsync(cancellationToken);
        var definition = await store.GetWorkflowDefinitionAsync(definitionId, cancellationToken);
        if (definition is null)
        {
            return null;
        }

        var versions = await store.ListWorkflowDefinitionVersionsAsync(definitionId, cancellationToken);
        return definition.ToResponse(versions);
    }

    public async Task<WorkflowDefinitionResponse> CreateAsync(CreateWorkflowDefinitionRequest request, CancellationToken cancellationToken)
    {
        var validation = validator.ValidateDefinitionName(request.Name);
        if (!validation.IsValid)
        {
            throw new WorkflowDefinitionValidationException(validation.Errors);
        }

        var now = clock.UtcNow;
        var definition = new WorkflowDefinition
        {
            Name = request.Name.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        await store.CreateWorkflowDefinitionAsync(definition, cancellationToken);
        return definition.ToResponse([]);
    }

    public async Task<WorkflowDefinitionVersionResponse> CreateVersionAsync(
        Guid definitionId,
        CreateWorkflowDefinitionVersionRequest request,
        CancellationToken cancellationToken)
    {
        var definition = await store.GetWorkflowDefinitionAsync(definitionId, cancellationToken);
        if (definition is null)
        {
            throw new WorkflowDefinitionNotFoundException($"Workflow definition '{definitionId}' was not found.");
        }

        if (request.IsEnabled)
        {
            var validation = validator.Validate(request.Definition);
            if (!validation.IsValid)
            {
                throw new WorkflowDefinitionValidationException(validation.Errors);
            }
        }

        var latest = await store.GetLatestWorkflowDefinitionVersionAsync(definitionId, cancellationToken);
        var versionNumber = request.Version ?? ((latest?.Version ?? 0) + 1);
        var version = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = definitionId,
            Version = versionNumber,
            DslSchemaVersion = request.Definition.Schema,
            IsEnabled = request.IsEnabled,
            IsDefault = request.IsDefault,
            DefinitionJson = WorkflowDefinitionJson.Serialize(request.Definition),
            CreatedAt = clock.UtcNow
        };

        await store.CreateWorkflowDefinitionVersionAsync(version, cancellationToken);
        return version.ToResponse();
    }

    public async Task<WorkflowDefinitionVersion> ResolveForRunAsync(
        Guid? definitionId,
        Guid? versionId,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultWorkflowDefinitionAsync(cancellationToken);
        WorkflowDefinitionVersion? version;
        if (versionId.HasValue)
        {
            version = await store.GetWorkflowDefinitionVersionAsync(versionId.Value, cancellationToken);
            if (version is null)
            {
                throw new WorkflowDefinitionNotFoundException($"Workflow definition version '{versionId}' was not found.");
            }

            if (definitionId.HasValue && version.WorkflowDefinitionId != definitionId.Value)
            {
                throw new WorkflowDefinitionValidationException([
                    new WorkflowDefinitionValidationError("definition.version.mismatch", "Requested workflow definition version does not belong to the requested definition.")
                ]);
            }
        }
        else if (definitionId.HasValue)
        {
            version = await store.GetLatestEnabledWorkflowDefinitionVersionAsync(definitionId.Value, cancellationToken);
            if (version is null)
            {
                throw new WorkflowDefinitionNotFoundException($"No enabled workflow definition version was found for definition '{definitionId}'.");
            }
        }
        else
        {
            version = await store.GetDefaultEnabledWorkflowDefinitionVersionAsync(cancellationToken);
            if (version is null)
            {
                throw new WorkflowDefinitionNotFoundException("No default workflow definition version was found.");
            }
        }

        if (!version.IsEnabled)
        {
            throw new WorkflowDefinitionValidationException([
                new WorkflowDefinitionValidationError("definition.version.disabled", "Requested workflow definition version is disabled.")
            ]);
        }

        var validation = validator.Validate(WorkflowDefinitionJson.Deserialize(version.DefinitionJson));
        if (!validation.IsValid)
        {
            throw new WorkflowDefinitionValidationException(validation.Errors);
        }

        return version;
    }
}

public sealed class WorkflowDefinitionValidationException(IReadOnlyList<WorkflowDefinitionValidationError> errors)
    : Exception("Workflow definition validation failed.")
{
    public IReadOnlyList<WorkflowDefinitionValidationError> Errors { get; } = errors;
}

public sealed class WorkflowDefinitionNotFoundException(string message) : Exception(message);
