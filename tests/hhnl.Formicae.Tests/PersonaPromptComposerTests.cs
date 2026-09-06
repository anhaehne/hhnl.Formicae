using hhnl.Formicae.Application.Workflows;

namespace hhnl.Formicae.Tests;

public sealed class PersonaPromptComposerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Original task\r\nwith exact newlines\n")]
    public void Default_and_legacy_prompts_are_byte_for_byte_unchanged(string prompt)
    {
        Assert.Same(prompt, PersonaPromptComposer.Compose(prompt, null));
        Assert.Same(prompt, PersonaPromptComposer.Compose(prompt, PersonaService.DefaultSnapshot));
    }

    [Fact]
    public void Custom_guidance_is_one_plain_text_section_with_task_preserved()
    {
        var persona = new PersonaSnapshot("custom", 7, "Reviewer", "Inspect {{literal}} and $(not-executed).", "Direct", "Explain uncertainty.");
        var prompt = "Implement the requested task.\r\nKeep existing behavior.";
        var result = PersonaPromptComposer.Compose(prompt, persona);
        Assert.StartsWith(prompt + "\n\n## Persona guidance", result);
        Assert.Equal(1, result.Split("## Persona guidance", StringSplitOptions.None).Length - 1);
        Assert.Contains("Reviewer (revision 7)", result);
        Assert.Contains("Instructions:\nInspect {{literal}} and $(not-executed).", result);
        Assert.Contains("Tone:\nDirect", result);
        Assert.Contains("Operating constraints:\nExplain uncertainty.", result);
        Assert.Equal(result, PersonaPromptComposer.Compose(prompt, persona));
    }

    [Fact]
    public void Empty_optional_context_has_no_empty_field_labels()
    {
        var result = PersonaPromptComposer.Compose("task", new("custom", 1, "Quiet", "", "", ""));
        Assert.Contains("Persona: Quiet (revision 1)", result);
        Assert.DoesNotContain("Instructions:", result);
        Assert.DoesNotContain("Tone:", result);
        Assert.DoesNotContain("Operating constraints:", result);
    }
}
