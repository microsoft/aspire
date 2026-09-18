// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZUREHEALTH001

using Aspire.Hosting.Azure;
using Aspire.Hosting.RemoteHost;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class HealthModelExportsTests
{
    [Fact]
    public async Task PublishingApisGenerateTypedResourceAndEndpointSignatures()
    {
        var result = AtsCapabilityScanner.ScanAssemblies(
            [typeof(DistributedApplication).Assembly, typeof(AzureHealthModelResource).Assembly]);
        var exports = result.Capabilities
            .Where(capability => capability.CapabilityId.StartsWith("Aspire.Hosting.Azure.HealthModels/", StringComparison.Ordinal))
            .OrderBy(capability => capability.MethodName).ToArray();

        Assert.Collection(exports,
            capability => Assert.Equal("addAzureContainerAppsHealthModelCollector", capability.MethodName),
            capability => Assert.Equal("addAzureHealthModel", capability.MethodName));

        var files = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(result.ToAtsContext());
        var signatures = files["aspire.mts"].Split('\n').Where(line =>
            line.Contains("addAzureHealthModel(", StringComparison.Ordinal) ||
            line.Contains("addAzureContainerAppsHealthModelCollector(", StringComparison.Ordinal));

        await Verify(string.Join('\n', signatures), "ts");
    }
}
