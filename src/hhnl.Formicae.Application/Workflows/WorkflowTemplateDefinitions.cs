using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Scriban;
using Scriban.Runtime;

namespace hhnl.Formicae.Application.Workflows;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowTemplateSettings(string Template, IReadOnlyList<CustomTaskInputDefinition> Inputs,
    IReadOnlyDictionary<string, CustomTaskInputBinding>? Bindings = null);
public sealed record PreparedTemplateExecution(IReadOnlyDictionary<string, JsonElement> Inputs,
    IReadOnlyDictionary<string, CustomTaskInputProvenance> Provenance);

public static class WorkflowTemplateDefinitions
{
    public const string Uses = "builtins.template";
    public static IReadOnlyList<CustomTaskOutputDefinition> Outputs { get; } = [new("text", "string", true)];

    public static IReadOnlyList<WorkflowDefinitionValidationError> ValidateStep(WorkflowDefinitionStep step)
    {
        var errors = new List<WorkflowDefinitionValidationError>();
        void Error(string message) => errors.Add(new("definition.template.invalid", message, "steps[].template", step.Id));
        if (step.Uses != Uses)
        {
            if (step.Template is not null) Error("Only Template nodes may carry template settings.");
            return errors;
        }
        if (step.AiSettingsId is not null || step.Model is not null || step.PersonaId is not null || step.PersonaSnapshot is not null
            || step.EnvironmentId is not null || step.EnvironmentSnapshot is not null || step.ImageSelection is not null || step.ImageSnapshot is not null
            || step.Capabilities is not null || step.SecretReferences is not null || step.CustomTask is not null || step.Script is not null
            || step.IssueComment is not null || step.Wait is not null || step.Event is not null || step.Trigger is not null
            || step.Decision is not null || step.Loop is not null || step.Parallel is not null)
            Error("Template nodes cannot select worker, agent or other node settings.");
        if (step.Template is not { } settings) { Error("Template settings are required."); return errors; }
        if (settings.Template is null || settings.Template.Length > 16000) Error("Template must be at most 16000 characters.");
        else
        {
            var parsed = Template.Parse(settings.Template);
            if (parsed.HasErrors) Error($"Invalid Scriban template: {parsed.Messages}");
        }
        if (settings.Inputs is null) { Error("Template inputs are required (an empty list is allowed)."); return errors; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in settings.Inputs)
        {
            if (input is null || input.Name is null || !Regex.IsMatch(input.Name, "^[A-Za-z][A-Za-z0-9_]{0,63}$") || !names.Add(input.Name))
            { Error("Template input names must be unique identifiers of at most 64 characters."); continue; }
            if (input.ValueType is not ("string" or "number" or "boolean")) Error($"Input '{input.Name}' has an unsupported scalar type.");
            if (!input.Required || input.DefaultValue is not null) Error($"Input '{input.Name}' must require a connection and cannot have a default.");
            if (settings.Bindings?.TryGetValue(input.Name, out var binding) != true || binding is null
                || string.IsNullOrWhiteSpace(binding.StepId) || string.IsNullOrWhiteSpace(binding.OutputName))
                Error($"Input '{input.Name}' requires a data connection.");
        }
        foreach (var name in settings.Bindings?.Keys ?? [])
            if (!names.Contains(name)) Error($"Unknown template input '{name}'.");
        return errors;
    }

    public static string Render(WorkflowTemplateSettings settings, IReadOnlyDictionary<string, JsonElement> values, CancellationToken token = default)
    {
        var parsed = Template.Parse(settings.Template);
        if (parsed.HasErrors) throw new InvalidOperationException($"Invalid Scriban template: {parsed.Messages}");
        var inputs = new ScriptObject();
        foreach (var input in settings.Inputs)
        {
            if (!values.TryGetValue(input.Name, out var value) || !CustomTaskDefinitions.ValidScalar(value, input.ValueType))
                throw new InvalidOperationException($"Template input '{input.Name}' is missing or has an invalid {input.ValueType} value.");
            object scalar = input.ValueType switch { "number" => value.GetDecimal(), "boolean" => value.GetBoolean(), _ => value.GetString()! };
            inputs.SetValue(input.Name, scalar, true);
        }
        var context = new TemplateContext
        {
            StrictVariables = true, EnableRelaxedMemberAccess = false, LoopLimit = 1000, RecursiveLimit = 32,
            CancellationToken = token, LimitToString = 16000, RegexTimeOut = TimeSpan.FromSeconds(1),
            OnStringLimit = ScriptLimitBehavior.Throw, OnOutputLimit = ScriptLimitBehavior.Throw
        };
        context.PushCulture(CultureInfo.InvariantCulture);
        var globals = new ScriptObject(); globals.SetValue("inputs", inputs, true); context.PushGlobal(globals);
        try { return parsed.Render(context); }
        catch (Scriban.Syntax.ScriptRuntimeException exception) { throw new InvalidOperationException($"Template rendering failed: {exception.Message}", exception); }
    }

}
