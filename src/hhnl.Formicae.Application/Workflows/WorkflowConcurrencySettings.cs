namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowConcurrencySettings
{
    public int Id { get; init; } = 1;
    public int? MaxActiveInstances { get; set; }
}

public sealed record WorkflowConcurrencyLimit(int? MaxActiveInstances);
