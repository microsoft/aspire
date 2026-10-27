// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Aspire.Hosting.RemoteHost;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class EntraIdCodeGenerationTests
{
    [Fact]
    public async Task Exports_GenerateTypedDeclarationsAndDocumentation()
    {
        var result = AtsCapabilityScanner.ScanAssemblies(
            [typeof(DistributedApplication).Assembly, typeof(AzureProvisioningResource).Assembly, typeof(EntraIdApplicationResource).Assembly]);
        Assert.All(result.Diagnostics, d => Assert.Equal(AtsDiagnosticSeverity.Info, d.Severity));
        var capabilities = result.Capabilities.Where(c => c.CapabilityId.StartsWith("Aspire.Hosting.Azure.EntraId/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(capabilities.Length, capabilities.Select(c => c.CapabilityId).Distinct().Count());
        Assert.NotEmpty(capabilities);
        Assert.All(capabilities, c => Assert.NotNull(c.Documentation?.Summary));
        var addApplication = Assert.Single(capabilities, c => c.CapabilityId == "Aspire.Hosting.Azure.EntraId/addEntraIdApplication");
        Assert.Equal("The application registration builder.", addApplication.Documentation?.Returns);
        Assert.NotEmpty(addApplication.Documentation!.Remarks!);

        var model = new TypeScriptApiProjector(result.ToAtsContext()).BuildApiModel(
            new TypeScriptApiPackageIdentity("Aspire.Hosting.Azure.EntraId", "test"),
            ["Aspire.Hosting.Azure.EntraId"],
            CancellationToken.None);
        var declarations = model.Declarations
            .Where(d => d.OwningAssemblyName == "Aspire.Hosting.Azure.EntraId")
            .Select(d => d.Content)
            .ToArray();
        Assert.NotEmpty(declarations);
        await Verify(string.Join("\n\n", declarations), "ts");
    }
}
