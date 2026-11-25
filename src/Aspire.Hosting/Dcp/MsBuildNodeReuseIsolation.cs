// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Lifecycle;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Keeps DCP-managed MSBuild workers in a pool associated with the current DCP run.
/// </summary>
internal sealed class MsBuildNodeReuseIsolation
{
    internal const string HandshakeSaltEnvironmentVariable = "MSBUILDNODEHANDSHAKESALT";

    private static readonly StringComparer s_environmentVariableComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private RunState? _runState;

    public void StartDcpRun(IEnumerable<KeyValuePair<string, string>> environment)
    {
        string? inheritedSalt = null;
        foreach (var (name, value) in environment)
        {
            if (s_environmentVariableComparer.Equals(name, HandshakeSaltEnvironmentVariable))
            {
                inheritedSalt = value;
            }
        }

        Volatile.Write(ref _runState, new RunState(inheritedSalt, Guid.NewGuid().ToString("N")));
    }

    public void Apply(IResource resource, ExecutableSpec spec, SecretRedactionHistory secretRedactionHistory)
    {
        if (!DotnetBuildCommandEventingSubscriber.IsAspireManagedDotnetBuild(resource))
        {
            return;
        }

        var runState = Volatile.Read(ref _runState) ??
            throw new InvalidOperationException("The DCP run must start before configuring an Aspire-managed .NET build.");
        var environment = spec.Env ??= [];
        var originalSalt = runState.InheritedSalt;
        foreach (var variable in environment)
        {
            if (s_environmentVariableComparer.Equals(variable.Name, HandshakeSaltEnvironmentVariable))
            {
                originalSalt = variable.Value ?? throw new InvalidOperationException(
                    $"The {HandshakeSaltEnvironmentVariable} environment variable must have a value.");
            }
        }

        environment.RemoveAll(variable => s_environmentVariableComparer.Equals(variable.Name, HandshakeSaltEnvironmentVariable));

        // MSBuild incorporates this undocumented environment variable into its node handshake:
        // https://github.com/dotnet/msbuild/blob/660964a8e2106dbb77329ad7807242b64a7bd86c/src/Framework/BackEnd/Handshake.cs
        // Append the run identity after resolution so callbacks, build properties, and IDE metadata keep their values.
        var effectiveSalt = $"{originalSalt}:{runState.Nonce}";

        // Backchannel redaction matches complete values, and the original salt might contain a secret.
        secretRedactionHistory.AddValues([effectiveSalt]);
        environment.Add(new EnvVar { Name = HandshakeSaltEnvironmentVariable, Value = effectiveSalt });
    }

    private sealed record RunState(string? InheritedSalt, string Nonce);
}
