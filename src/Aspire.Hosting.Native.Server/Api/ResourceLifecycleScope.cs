// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Api;

/// <summary>Exposes one native-authorized provider phase, never general declaration or credential access.</summary>
[AspireExport]
internal sealed class ResourceLifecycleScope(ResourceLifecycleAuthority authority) : ICapabilityLifetime
{
    public bool IsRevoked => authority.IsRevoked;

    /// <summary>Reads the currently authorized lifecycle phase.</summary>
    [AspireExport]
    public string ReadLifecyclePhase()
    {
        ObjectDisposedException.ThrowIf(IsRevoked, this);

        return authority.Phase;
    }

    /// <summary>Appends a resource log through native redaction and retention.</summary>
    [AspireExport]
    public void WriteLifecycleLog(string stream, string message) => authority.AppendLog(RuntimeStates.ParseLogStream(stream), message);

    /// <summary>Runs a bounded one-shot executable with this resource's captured native symbolic inputs.</summary>
    /// <remarks>Returns only exit success. Resolved credentials, references and command output stay native-owned.</remarks>
    [AspireExport]
    public Task<bool> RunLifecycleExecutable(ExecutableWorkloadLaunch launch, SymbolicLaunchBindings bindings) =>
        authority.RunExecutable(ResourceExecution.CreateExecutablePlan(launch), ResourceExecution.CopyBindings(bindings));
}

internal interface IProviderResourceRetirement
{
    Task RetireAsync();
}
