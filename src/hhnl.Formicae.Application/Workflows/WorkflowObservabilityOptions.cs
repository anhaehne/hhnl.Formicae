namespace hhnl.Formicae.Application.Workflows;

public sealed class WorkflowObservabilityOptions
{
    public TimeSpan RunningTaskStuckAfter { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan WorkflowStaleAfter { get; set; } = TimeSpan.FromHours(2);
}
