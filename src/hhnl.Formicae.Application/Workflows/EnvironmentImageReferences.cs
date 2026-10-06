using System.Text.RegularExpressions;

namespace hhnl.Formicae.Application.Workflows;

/// <summary>Docker reference syntax with lowercase repository names and SHA-256 digests.</summary>
internal static class EnvironmentImageReferences
{
    // Follows distribution/reference's domain, path component, separator, and tag grammar:
    // https://github.com/distribution/reference/blob/main/regexp.go
    private const string Domain = @"(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)*|\[[a-f0-9:]+\])(?::[0-9]+)?";
    private const string Component = @"[a-z0-9]+(?:(?:[._]|__|[-]+)[a-z0-9]+)*";
    private static readonly Regex Reference = new(@"\A(?<name>(?:" + Domain + "/)?" + Component + "(?:/" + Component
        + @")*)(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(?:@sha256:[a-fA-F0-9]{64})?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsValid(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 512) return false;
        var match = Reference.Match(reference);
        return match.Success && match.Groups["name"].Length <= 255;
    }
}
