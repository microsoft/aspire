// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.CompilerServices;
using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.Agents;

public class AgentEnvironmentScanContextTests
{
    [Fact]
    public void Context_ContainsOnlyRequiredInitOnlyDirectoryInputs()
    {
        var properties = typeof(AgentEnvironmentScanContext).GetProperties();

        Assert.Equal(
            [nameof(AgentEnvironmentScanContext.RepositoryRoot), nameof(AgentEnvironmentScanContext.WorkingDirectory)],
            properties.Select(static property => property.Name).Order());
        Assert.All(properties, property =>
        {
            Assert.Equal(typeof(DirectoryInfo), property.PropertyType);
            Assert.NotNull(property.GetCustomAttribute<RequiredMemberAttribute>());
            Assert.NotNull(property.SetMethod);
            Assert.Equal([typeof(IsExternalInit)], property.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
        });
    }
}
