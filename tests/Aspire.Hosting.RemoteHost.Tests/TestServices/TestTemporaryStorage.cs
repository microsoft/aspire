// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias RemoteHost;

namespace Aspire.Shared.Tests;

/// <summary>
/// Creates the temporary storage used by RemoteHost code under test.
/// </summary>
/// <remarks>
/// Aspire.Hosting and Aspire.Hosting.RemoteHost both compile src/Shared/TemporaryStorage.cs as an
/// internal type visible to this project, so the RemoteHost copy is selected through its extern alias.
/// Shared process tests call this helper so they compile unchanged in every test project.
/// </remarks>
internal static class TestTemporaryStorage
{
    public static RemoteHost::Aspire.Shared.ITemporaryStorage Create() => new RemoteHost::Aspire.Shared.TemporaryStorage();
}
