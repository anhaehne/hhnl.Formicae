using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace hhnl.Formicae.Tests;

public sealed class FormicaeApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?>? configuration;
    private readonly Dictionary<string, string?> originalEnvironment = [];

    public FormicaeApiFactory(IReadOnlyDictionary<string, string?>? configuration = null)
    {
        this.configuration = configuration;

        if (configuration is null)
        {
            return;
        }

        foreach (var (key, value) in configuration)
        {
            var environmentKey = key.Replace(":", "__", StringComparison.Ordinal);
            originalEnvironment[environmentKey] = Environment.GetEnvironmentVariable(environmentKey);
            Environment.SetEnvironmentVariable(environmentKey, value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UseFakeAdapters"] = "true",
                ["WorkflowDiscovery:Enabled"] = "false"
            });

            if (configuration is not null)
            {
                configBuilder.AddInMemoryCollection(configuration);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var (key, value) in originalEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        base.Dispose(disposing);
    }
}
