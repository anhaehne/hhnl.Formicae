using hhnl.Formicae.KubernetesE2ETests.Infrastructure;

namespace hhnl.Formicae.KubernetesE2ETests;

public sealed class CommandRunnerTests
{
    [Fact]
    public async Task Successful_command_retains_both_redirected_streams()
    {
        var (file, args) = Shell("echo command-output; echo command-error >&2", "echo command-output & echo command-error 1>&2");
        var result = await CommandRunner.RunRequiredAsync(file, args, Path.GetTempPath(), TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("command-output", result.StandardOutput);
        Assert.Contains("command-error", result.StandardError);
    }

    [Fact]
    public async Task Failed_command_retains_exit_code_and_output()
    {
        var (file, args) = Shell("echo failed-import >&2; exit 7", "echo failed-import 1>&2 & exit /b 7");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CommandRunner.RunRequiredAsync(file, args, Path.GetTempPath(), TimeSpan.FromSeconds(10)));
        Assert.Contains("Command failed (7)", error.Message);
        Assert.Contains("failed-import", error.Message);
    }

    [Fact]
    public async Task Timeout_kills_command_and_retains_elapsed_limit_and_partial_output()
    {
        var (file, args) = SlowCommand();
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            CommandRunner.RunRequiredAsync(file, args, Path.GetTempPath(), TimeSpan.FromSeconds(1)));
        Assert.Contains("Command timed out after", error.Message);
        Assert.Contains("limit 1.0s", error.Message);
        Assert.Contains(Environment.NewLine + "import-started", error.Message);
        Assert.Contains(Environment.NewLine + "import-progress", error.Message);
        Assert.DoesNotContain(Environment.NewLine + "import-completed", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_kills_command_and_preserves_cancellation_semantics()
    {
        var (file, args) = SlowCommand();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandRunner.RunAsync(file, args, Path.GetTempPath(), TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData(null, 15)]
    [InlineData("", 15)]
    [InlineData("1", 1)]
    [InlineData("15", 15)]
    [InlineData("60", 60)]
    public void Image_load_deadline_defaults_or_accepts_bounded_minutes(string? value, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), KubernetesE2EFixture.ParseImageLoadTimeout(value));

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("invalid")]
    [InlineData("1.5")]
    [InlineData("-1")]
    public void Invalid_image_load_deadline_is_rejected(string value)
        => Assert.Contains("FORMICAE_E2E_IMAGE_LOAD_TIMEOUT_MINUTES", Assert.Throws<InvalidOperationException>(() =>
            KubernetesE2EFixture.ParseImageLoadTimeout(value)).Message);

    private static (string, string[]) SlowCommand() => Shell(
        "echo import-started; echo import-progress >&2; sleep 30; echo import-completed",
        "echo import-started & echo import-progress 1>&2 & ping -n 31 127.0.0.1 >nul & echo import-completed");

    private static (string, string[]) Shell(string unix, string windows)
        => OperatingSystem.IsWindows() ? ("cmd.exe", ["/c", windows]) : ("/bin/sh", ["-c", unix]);
}
