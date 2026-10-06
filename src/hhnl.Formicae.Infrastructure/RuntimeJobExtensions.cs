using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Infrastructure;

internal static class RuntimeJobExtensions
{
    public static void Validate(RuntimeJobSpec spec)
    {
        var configuration = EnvironmentDefinitions.ValidateConfiguration(new EnvironmentConfiguration
        {
            Image = new(spec.Image, spec.ImagePullPolicy, spec.ImagePullSecretNames)
        });
        var references = WorkflowExecutionExtensions.ValidateStep(new WorkflowDefinitionStep("runtime", "builtins.plan", SecretReferences: spec.SecretReferences));
        if (!configuration.IsValid || !references.IsValid)
            throw new InvalidOperationException(string.Join(" ", configuration.Errors.Concat(references.Errors).Select(error => error.Message)));
        foreach (var reference in spec.SecretReferences ?? [])
        {
            if (reference.SecretName == spec.Name + "-api-auth" || reference.SecretName == spec.Name + "-codex-auth")
                throw new InvalidOperationException("Operator Secret references cannot use names reserved for this attempt's managed credentials.");
            if (spec.Environment.ContainsKey(reference.EnvironmentName) || spec.SecretEnvironment?.Data.ContainsKey(reference.EnvironmentName) == true)
                throw new InvalidOperationException($"Selected secret alias '{reference.EnvironmentName}' conflicts with a runtime variable.");
        }
    }

    private static IEnumerable<string> SecretVariants(string value)
    {
        yield return value;
        yield return System.Text.Json.JsonSerializer.Serialize(value)[1..^1];
        yield return Uri.EscapeDataString(value);
        yield return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (line.Length >= 4) yield return line;
    }

    public static string Redact(string text, IEnumerable<string> values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrEmpty(value)).SelectMany(SecretVariants).Distinct().OrderByDescending(value => value.Length))
            text = text.Replace(value, "***", StringComparison.Ordinal);
        return text;
    }
}
