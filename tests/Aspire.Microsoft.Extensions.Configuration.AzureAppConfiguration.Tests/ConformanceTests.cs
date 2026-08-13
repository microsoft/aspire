// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using Xunit;

namespace Aspire.Microsoft.Extensions.Configuration.AzureAppConfiguration.Tests;

public class ConformanceTests : ConformanceTests<IConfigurationRefresherProvider, AzureAppConfigurationSettings>
{
    public const string Endpoint = "https://aspiretests.azconfig.io/";

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "Microsoft.Extensions.Configuration.AzureAppConfiguration";

    protected override RequiredLogCategory[] RequiredLogCategories => [new("Microsoft.Extensions.Configuration.AzureAppConfiguration.Refresh")];

    protected override bool SupportsKeyedRegistrations => false;

    protected override bool IsComponentBuiltBeforeHost => true;

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Azure": {
              "AppConfiguration": {
                "Endpoint": "http://YOUR_URI",
                "Optional": true,
                "DisableTracing": false
              }
            }
          }
        }
        """;

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[]
        {
            new(CreateConfigKey("Aspire:Microsoft:Extensions:Configuration:AzureAppConfiguration", null, "Endpoint"), Endpoint)
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureAppConfigurationSettings>? configure = null, string? key = null)
    {
        builder.AddAzureAppConfiguration(
            "appconfig",
            settings =>
            {
                configure?.Invoke(settings);
                settings.Credential = new EmptyTokenCredential();
                settings.Optional = true;
            },
            options =>
            {
                // AzureAppConfigurationOptions.MinBackoffDuration is internal, use reflection to set it to 1 second to facilitate testing.
                var minBackoffDurationProperty = options.GetType().GetProperty("MinBackoffDuration", BindingFlags.Instance | BindingFlags.NonPublic);
                minBackoffDurationProperty?.SetValue(options, TimeSpan.FromSeconds(1));

                options.ConfigureRefresh(refreshOptions =>
                {
                    refreshOptions.Register("sentinel")
                        .SetRefreshInterval(TimeSpan.FromSeconds(1));
                });
                options.ConfigureStartupOptions(startupOptions =>
                {
                    startupOptions.Timeout = TimeSpan.FromSeconds(1);
                });
                options.ConfigureClientOptions(clientOptions => clientOptions.Retry.MaxRetries = 0);
            });
    }

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Microsoft": { "Extensions": { "Configuration": { "AzureAppConfiguration": { "Endpoint": "YOUR_URI"}}}}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "Microsoft": { "Extensions": { "Configuration": { "AzureAppConfiguration": { "Endpoint": "http://YOUR_URI", "Optional": "true"}}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Microsoft": { "Extensions": { "Configuration": { "AzureAppConfiguration": { "Endpoint": "http://YOUR_URI", "DisableTracing": "true"}}}}}}""", "Value is \"string\" but should be \"boolean\""),
        };

    protected override void SetHealthCheck(AzureAppConfigurationSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetMetrics(AzureAppConfigurationSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(AzureAppConfigurationSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void TriggerActivity(IConfigurationRefresherProvider service)
    {
        // wait for 1 seconds to ensure the refresh interval is exceeded
        Task.Delay(1000).ConfigureAwait(false).GetAwaiter().GetResult();
        service.Refreshers.First().RefreshAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public ConformanceTests(ITestOutputHelper? output = null) : base(output)
    {
    }

    [Fact]
    public void TracingEnablesTheRightActivitySource()
        => RemoteInvokeWithLogging(() => new ConformanceTests().ActivitySourceTest(key: null), Output);

    internal sealed class EmptyTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken(string.Empty, DateTimeOffset.MaxValue);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new ValueTask<AccessToken>(new AccessToken(string.Empty, DateTimeOffset.MaxValue));
        }
    }
}
