// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Foundry.Tests;

public class AddFoundryTests
{
    [Fact]
    public void AddFoundry_ShouldAddResourceToBuilder()
    {
        const string name = "account";
        using var builder = TestDistributedApplicationBuilder.Create();

        var resourceBuilder = builder.AddFoundry(name);

        Assert.NotNull(resourceBuilder);
        Assert.NotNull(resourceBuilder.Resource);
        Assert.Equal(name, resourceBuilder.Resource.Name);
        Assert.IsType<FoundryResource>(resourceBuilder.Resource);
    }
}
