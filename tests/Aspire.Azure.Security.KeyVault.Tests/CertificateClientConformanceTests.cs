// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Security.KeyVault.Tests;
public class CertificateClientConformanceTests : ConformanceTests<CertificateClient, AzureSecurityKeyVaultSettings>
{
    // Roles: Key Vault Certificate User (pending)
    private const string VaultUri = ConformanceConstants.VaultUri;

    private static readonly Lazy<bool> s_canConnectToServer = new(GetCanConnect);

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;

    protected override string ActivitySourceName => "Azure.Security.KeyVault.Certificates.CertificateClient";

    protected override string[] RequiredLogCategories => new string[] { "Azure.Core" };

    protected override bool SupportsKeyedRegistrations => true;

    protected override bool CanConnectToServer => s_canConnectToServer.Value;

    protected override string ValidJsonConfig => """
        {
          "Aspire": {
            "Azure": {
              "Security": {
                "KeyVault": {
                  "VaultUri": "http://YOUR_URI",
                  "DisableHealthChecks": true,
                  "DisableTracing": false,
                  "ClientOptions": {
                    "DisableChallengeResourceVerification": true,
                    "Retry": {
                      "Mode": "Exponential",
                      "Delay": "00:03"
                    }
                  }
                }
              }
            }
          }
        }
        """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage => new[]
        {
            ("""{"Aspire": { "Azure": { "Security":{ "KeyVault": { "VaultUri": "YOUR_URI"}}}}}""", "Value does not match format \"uri\""),
            ("""{"Aspire": { "Azure": { "Security":{ "KeyVault": { "VaultUri": "http://YOUR_URI", "DisableHealthChecks": "true"}}}}}""", "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Azure": { "Security":{ "KeyVault": { "VaultUri": "http://YOUR_URI", "ClientOptions": {"Retry": {"Mode": "Fast"}}}}}}}""", "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "Azure": { "Security":{ "KeyVault": { "VaultUri": "http://YOUR_URI", "ClientOptions": {"Retry": {"NetworkTimeout": "3S"}}}}}}}""", "The string value is not a match for the indicated regular expression")
        };

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(new KeyValuePair<string, string?>[]
        {
            new(CreateConfigKey("Aspire:Azure:Security:KeyVault", key, "VaultUri"), VaultUri),
            new(CreateConfigKey("Aspire:Azure:Security:KeyVault", key, "ClientOptions:Retry:MaxRetries"), "0")
        });

    protected override void RegisterComponent(HostApplicationBuilder builder, Action<AzureSecurityKeyVaultSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureKeyVaultCertificateClient("certificates", ConfigureCredentials);
        }
        else
        {
            builder.AddKeyedAzureKeyVaultCertificateClient(key, ConfigureCredentials);
        }

        void ConfigureCredentials(AzureSecurityKeyVaultSettings settings)
        {
            if (CanConnectToServer)
            {
                settings.Credential = new DefaultAzureCredential();
            }
            configure?.Invoke(settings);
        }
    }

    protected override void SetHealthCheck(AzureSecurityKeyVaultSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetMetrics(AzureSecurityKeyVaultSettings options, bool enabled)
        => throw new NotImplementedException();

    protected override void SetTracing(AzureSecurityKeyVaultSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void TriggerActivity(CertificateClient service)
        => service.GetCertificate("IsAlive");

    [Fact]
    public void TracingEnablesTheRightActivitySource()
        => RemoteExecutor.Invoke(() => ActivitySourceTest(key: null)).Dispose();

    [Fact]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteExecutor.Invoke(() => ActivitySourceTest(key: "key")).Dispose();

    private static bool GetCanConnect()
    {
        CertificateClientOptions clientOptions = new();
        clientOptions.Retry.MaxRetries = 0; // don't enable retries (test runs few times faster)
        CertificateClient certClient = new(new Uri(VaultUri), new DefaultAzureCredential(), clientOptions);

        try
        {
            return certClient.GetCertificate("IsAlive").Value.Name.Equals("IsAlive", StringComparison.CurrentCultureIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
