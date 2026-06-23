// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Azure.Tests;

public class AzureResourceOptionsTests(ITestOutputHelper output)
{
    /// <summary>
    /// Ensures that an AzureProvisioningOptions can be configured to modify the ProvisioningBuildOptions
    /// used when building the bicep for an Azure resource.
    ///
    /// This uses the Aspire v8.x naming policy, which always calls toLower, appends a unique string with no separator,
    /// and uses a max of 24 characters.
    /// </summary>
    [Fact]
    public async Task AzureResourceOptionsCanBeConfigured()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var outputPath = Path.Combine(tempDir.FullName, "aspire-manifest.json");

        using (var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath, step: "publish-manifest"))
        {
            builder.Services.Configure<AzureProvisioningOptions>(options =>
            {
                options.ProvisioningBuildOptions.InfrastructureResolvers.Insert(0, new AspireV8ResourceNamePropertyResolver());
            });

            var serviceBus = builder.AddAzureServiceBus("sb");

            // ensure that resources with a hyphen still have a hyphen in the bicep name
            var sqlDatabase = builder.AddAzureSqlServer("sql-server")
                .RunAsContainer(x => x.WithPersistentLifetime())
                .AddDatabase("evadexdb").WithDefaultAzureSku();

            using var app = builder.Build();
            await app.StartAsync();
            await app.WaitForShutdownAsync();

            var sbBicep = await File.ReadAllTextAsync(Path.Combine(tempDir.FullName, "sb.module.bicep"));

            var sqlBicep = await File.ReadAllTextAsync(Path.Combine(tempDir.FullName, "sql-server.module.bicep"));

            await Verify(sbBicep, extension: "bicep")
                .AppendContentAsFile(sqlBicep, "bicep");
        }

        try
        {
            tempDir.Delete(recursive: true);
        }
        catch (IOException ex)
        {
            output.WriteLine($"Failed to delete {tempDir.FullName} : {ex.Message}. Ignoring.");
        }
    }
}
