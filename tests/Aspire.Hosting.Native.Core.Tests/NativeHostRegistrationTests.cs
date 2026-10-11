// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias NativeLegacyServer;

using NativeLegacyServer::NativeHosting;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeHostRegistrationTests
{
    [Fact]
    public async Task OnlyAnIssuedRegistrationCanBeConsumedAndItCannotBeReplayed()
    {
        await using var registry = new AtsRegistry();
        var registration = Guid.NewGuid().ToString("N");
        Assert.Throws<UnauthorizedAccessException>(() => registry.ConsumeIntegrationHostRegistration(registration));
        registry.AllowIntegrationHost(registration);
        registry.ConsumeIntegrationHostRegistration(registration);
        Assert.Throws<UnauthorizedAccessException>(() => registry.ConsumeIntegrationHostRegistration(registration));
    }
}
