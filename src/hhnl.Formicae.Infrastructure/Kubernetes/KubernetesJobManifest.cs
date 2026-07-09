using System.Text;
using hhnl.Formicae.Infrastructure;

namespace hhnl.Formicae.Infrastructure.Kubernetes;

public static class KubernetesJobManifest
{
    public static string Render(RuntimeJobSpec spec)
    {
        var builder = new StringBuilder();
        builder.AppendLine("apiVersion: batch/v1");
        builder.AppendLine("kind: Job");
        builder.AppendLine("metadata:");
        builder.AppendLine($"  name: {spec.Name}");
        builder.AppendLine("spec:");
        builder.AppendLine("  template:");
        builder.AppendLine("    spec:");
        builder.AppendLine("      restartPolicy: Never");
        builder.AppendLine("      containers:");
        builder.AppendLine("        - name: worker");
        builder.AppendLine($"          image: {spec.Image}");
        builder.AppendLine("          env:");
        foreach (var (key, value) in spec.Environment.OrderBy(pair => pair.Key))
        {
            builder.AppendLine($"            - name: {key}");
            builder.AppendLine($"              value: \"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
        }

        builder.AppendLine("          command:");
        foreach (var command in spec.Command)
        {
            builder.AppendLine($"            - \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
        }

        return builder.ToString();
    }
}
