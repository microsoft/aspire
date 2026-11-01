// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.RemoteHost;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public class CargoTestingExportTests
{
    [Fact]
    public async Task GeneratesExperimentalCargoTesting()
    {
        var scan = AtsCapabilityScanner.ScanAssemblies([typeof(DistributedApplication).Assembly, typeof(CargoTestingExtensions).Assembly]);
        var capability = Assert.Single(scan.Capabilities, c => c.CapabilityId == "Aspire.Hosting.Rust/withCargoTesting");
        Assert.True(capability.IsExperimental);
        var code = new AtsTypeScriptCodeGenerator().GenerateDistributedApplication(scan.ToAtsContext())["aspire.mts"];
        var executable = Regex.Match(code, @"(?ms)^export interface ExecutableResourcePromise\b.*?^}").Value;
        Assert.NotEmpty(executable);
        var method = Regex.Match(executable, @"(?m)^    withCargoTesting\([^\r\n]*;").Value;
        Assert.NotEmpty(method);
        await Verify(new { Method = method });
    }
}
