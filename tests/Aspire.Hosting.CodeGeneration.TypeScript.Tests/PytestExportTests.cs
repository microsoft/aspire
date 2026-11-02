// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.Python;
using Aspire.Hosting.RemoteHost;

#pragma warning disable ASPIRETESTING001 // Verifies the experimental pytest generated API.

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class PytestExportTests
{
    [Fact]
    public async Task GeneratesExperimentalAddPytestWithPythonResourceCapabilities()
    {
        var scan = AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(PythonAppResource).Assembly]);
        var capability = Assert.Single(scan.Capabilities, c => c.CapabilityId == "Aspire.Hosting.Python/addPytest");
        Assert.True(capability.IsExperimental);
        Assert.Equal("Aspire.Hosting.Python/Aspire.Hosting.Python.PythonAppResource", capability.ReturnType!.TypeId);
        var code = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(scan.ToAtsContext())["aspire.mts"];
        var method = Regex.Match(code, @"(?m)^    addPytest\([^\r\n]*;").Value;
        Assert.NotEmpty(method);

        await Verify(method);
    }
}
