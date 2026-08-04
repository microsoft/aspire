// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Keycloak.Tests;

public class KeycloakFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public Task Keycloak_WithPersistentLifetime_ReusesContainer()
    {
        return PersistentContainerTestHelpers.AssertResourceReusesContainerAsync(
            testOutputHelper,
            builder => builder.AddKeycloak("resource").WithPersistentLifetime(),
            "resource",
            useTestContainerRegistry: true);
    }
}
