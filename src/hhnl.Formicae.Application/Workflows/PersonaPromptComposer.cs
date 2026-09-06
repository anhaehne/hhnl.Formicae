namespace hhnl.Formicae.Application.Workflows;

public static class PersonaPromptComposer
{
    public static string Compose(string prompt, PersonaSnapshot? persona)
    {
        if (persona is null || persona.Id == PersonaService.DefaultPersonaId) return prompt;
        var sections = new List<string> { "## Persona guidance", $"Persona: {persona.Name} (revision {persona.Revision})" };
        if (!string.IsNullOrWhiteSpace(persona.Instructions)) sections.Add($"Instructions:\n{persona.Instructions}");
        if (!string.IsNullOrWhiteSpace(persona.Tone)) sections.Add($"Tone:\n{persona.Tone}");
        if (!string.IsNullOrWhiteSpace(persona.OperatingConstraints)) sections.Add($"Operating constraints:\n{persona.OperatingConstraints}");
        return prompt + "\n\n" + string.Join("\n\n", sections);
    }
}
