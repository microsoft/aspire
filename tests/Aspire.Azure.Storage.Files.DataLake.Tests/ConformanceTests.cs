// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.ConformanceTests;
using Azure.Identity;
using Azure.Storage.Files.DataLake;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.Storage.Files.DataLake.Tests;

public sealed class ConformanceTests : ConformanceTests<DataLakeServiceClient, AzureDataLakeSettings>
{
    private const string ConnectionName = "data-lake";
    private static readonly Lazy<bool> s_canConnectToServer = new(CanConnect);
    public const string ServiceUri = "https://aspirestoragetests.dfs.core.windows.net/";

    protected override ServiceLifetime ServiceLifetime => ServiceLifetime.Singleton;
    protected override string ActivitySourceName => "Azure.Storage.Files.DataLake.DataLakeServiceClient";

    // AzureDataLakeSettings subclassed by AzureDataLakeFileSystemSettings
    protected override bool CheckOptionClassSealed => false;
    protected override string[] RequiredLogCategories => ["Azure.Core", "Azure.Identity"];
    protected override bool SupportsKeyedRegistrations => true;
    protected override bool CanConnectToServer => s_canConnectToServer.Value;
    protected override string ConfigurationSectionName => "Aspire:Azure:Storage:Files:DataLake";
    protected override string ValidJsonConfig
        => """
            {
              "Aspire": {
                "Azure": {
                  "Storage": {
                    "Files": {
                      "DataLake": {
                        "ServiceUri": "http://YOUR_URI",
                        "DisableHealthChecks": true,
                        "DisableTracing": false,
                        "ClientOptions": {
                          "Retry": {
                            "Mode": "Exponential",
                            "Delay": "00:00:03"
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

    protected override (string json, string error)[] InvalidJsonToErrorMessage
        =>
        [
            ("""{"Aspire": { "Azure": { "Storage":{ "Files": { "DataLake": { "ServiceUri": "YOUR_URI"}}}}}}""",
                "Value does not match format \"uri\""),
            ("""{"Aspire": { "Azure": { "Storage":{ "Files": { "DataLake": { "ServiceUri": "http://YOUR_URI", "DisableHealthChecks": "true"}}}}}}""",
                "Value is \"string\" but should be \"boolean\""),
            ("""{"Aspire": { "Azure": { "Storage":{ "Files": { "DataLake": { "ServiceUri": "http://YOUR_URI", "ClientOptions": {"Retry": {"Mode": "Fast"}}}}}}}}""",
                "Value should match one of the values specified by the enum"),
            ("""{"Aspire": { "Azure": { "Storage":{ "Files": { "DataLake": { "ServiceUri": "http://YOUR_URI", "ClientOptions": {"Retry": {"NetworkTimeout": "3S"}}}}}}}}""",
                "The string value is not a match for the indicated regular expression")
        ];

    protected override void RegisterComponent(
        HostApplicationBuilder builder,
        Action<AzureDataLakeSettings>? configure = null,
        string? key = null)
    {
        if (key is null)
        {
            builder.AddAzureDataLakeServiceClient(ConnectionName, ConfigureCredentials);
        }
        else
        {
            builder.AddKeyedAzureDataLakeServiceClient(key, ConfigureCredentials);
        }

        void ConfigureCredentials(AzureDataLakeSettings settings)
        {
            if (CanConnectToServer)
            {
                settings.Credential = new DefaultAzureCredential();
            }

            configure?.Invoke(settings);
        }
    }

    protected override void PopulateConfiguration(ConfigurationManager configuration, string? key = null)
        => configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(
                CreateConfigKey("Aspire:Azure:Storage:Files:DataLake", key, "ServiceUri"),
                ServiceUri),
            new KeyValuePair<string, string?>(
                CreateConfigKey("Aspire:Azure:Storage:Files:DataLake", key, "ClientOptions:Retry:MaxRetries"),
                "0")
        ]);

    protected override void TriggerActivity(DataLakeServiceClient service)
    {
        var fileSystemsName = Guid.NewGuid().ToString();
        service.CreateFileSystem(fileSystemsName);
        service.DeleteFileSystem(fileSystemsName);
    }

    protected override void SetHealthCheck(AzureDataLakeSettings options, bool enabled)
        => options.DisableHealthChecks = !enabled;

    protected override void SetTracing(AzureDataLakeSettings options, bool enabled)
        => options.DisableTracing = !enabled;

    protected override void SetMetrics(AzureDataLakeSettings options, bool enabled)
        => throw new NotImplementedException();

    [Fact]
    public void TracingEnablesTheRightActivitySource()
        => RemoteExecutor.Invoke(() => ActivitySourceTest(key: null)).Dispose();

    [Fact]
    public void TracingEnablesTheRightActivitySource_Keyed()
        => RemoteExecutor.Invoke(() => ActivitySourceTest(key: "key")).Dispose();

    private static bool CanConnect()
    {
        DataLakeClientOptions clientOptions = new()
        {
            Retry =
            {
                MaxRetries = 0 // don't enable retries (test runs few times faster)
            }
        };
        DataLakeServiceClient tableClient = new(new Uri(ServiceUri), new DefaultAzureCredential(), clientOptions);

        try
        {
            _ = tableClient.GetFileSystems().AsPages(pageSizeHint: 1).FirstOrDefault();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
