namespace hhnl.Formicae.Application.Workflows;

/// <summary>A transient transport failure left the outcome of a durable launch unknown.</summary>
public sealed class AgentLaunchUncertainException(string message, Exception innerException)
    : Exception(message, innerException);
