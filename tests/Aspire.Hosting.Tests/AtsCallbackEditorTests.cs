// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AtsCallbackEditorTests
{
    [Fact]
    public void CommandLineArgsEditor_Add_StoresTheResourceOfAResourceBuilder()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var parameter = builder.AddParameter("p", "value");
        var args = new List<object>();
        var editor = new CommandLineArgsEditor(args);

        editor.Add("--flag");
        editor.Add(parameter);

        Assert.Collection(args,
            arg => Assert.Equal("--flag", arg),
            arg => Assert.Same(parameter.Resource, arg));
    }

    [Fact]
    public void EnvironmentEditor_Set_StoresTheResourceOfAResourceBuilder()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var parameter = builder.AddParameter("p", "value");
        var environmentVariables = new Dictionary<string, object>();
        var editor = new EnvironmentEditor(environmentVariables);

        editor.Set("PLAIN", "text");
        editor.Set("FROM_PARAMETER", parameter);

        Assert.Equal("text", environmentVariables["PLAIN"]);
        Assert.Same(parameter.Resource, environmentVariables["FROM_PARAMETER"]);
    }
}
