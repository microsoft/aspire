// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Indicates that a resource can be launched by an IDE or extension host so it can be debugged,
/// instead of being started as a plain process by Aspire.
/// </summary>
/// <remarks>
/// Added by <see cref="ResourceBuilderExtensions.WithDebugSupport{T, TLaunchConfiguration}(IResourceBuilder{T}, Func{string, TLaunchConfiguration}, string)"/>
/// (or its asynchronous overload). The
/// annotation is only honored while a debug session is active; use
/// <see cref="DebugSupportExtensions.SupportsDebugging"/> to test for that, and
/// <see cref="DebugSupportExtensions.CreateLaunchConfigurationAsync"/> to inspect the launch configuration
/// the resource will send.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, RequiredExtensionId = {LaunchConfigurationType,nq}")]
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class SupportsDebuggingAnnotation : IResourceAnnotation
{
    private SupportsDebuggingAnnotation(
        string launchConfigurationType,
        Func<Executable, string, CancellationToken, Task> launchConfigurationAnnotator,
        Func<string, CancellationToken, Task<object>> launchConfigurationProducer)
    {
        LaunchConfigurationType = launchConfigurationType;
        LaunchConfigurationAnnotator = launchConfigurationAnnotator;
        LaunchConfigurationProducer = launchConfigurationProducer;
    }

    /// <summary>
    /// Gets the launch configuration type identifier, for example <see cref="KnownLaunchConfigurationTypes.Project"/>.
    /// </summary>
    /// <remarks>
    /// The IDE advertises the launch configuration types it can handle; a resource whose type is not
    /// advertised is started as a plain process instead. 
    /// <para>
    /// Exception: when the active debug session does not
    /// advertise any launch configuration types at all (for example Visual Studio, which does not send a
    /// capability list), <see cref="KnownLaunchConfigurationTypes.Project"/> is treated as implicitly
    /// supported rather than falling back to plain process execution.
    /// </para>
    /// </remarks>
    public string LaunchConfigurationType { get; }

    // Takes the internal DCP Executable object, so it stays internal even though the annotation is public.
    internal Func<Executable, string, CancellationToken, Task> LaunchConfigurationAnnotator { get; }

    // The producer callback passed to WithDebugSupport, with the launch configuration boxed as object.
    // Internal because it hands out an untyped object; DebugSupportExtensions.CreateLaunchConfigurationAsync is
    // the supported way to reach it.
    internal Func<string, CancellationToken, Task<object>> LaunchConfigurationProducer { get; }

    internal static SupportsDebuggingAnnotation Create<T>(string resourceName, string launchConfigurationType, Func<string, CancellationToken, Task<T>> launchProfileProducer)
    {
        // The annotator stays generic over T so the DCP annotation is serialized against the concrete
        // launch configuration type rather than a boxed object, which would change the emitted JSON.
        return new SupportsDebuggingAnnotation(
            launchConfigurationType,
            async (exe, mode, ct) => exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, await ProduceAsync(mode, ct).ConfigureAwait(false)),
            // The suppression is safe because ProduceAsync throws rather than returning null; the
            // compiler cannot see that because T is unconstrained and so may be a nullable type.
            async (mode, ct) => (await ProduceAsync(mode, ct).ConfigureAwait(false))!);

        async Task<T> ProduceAsync(string mode, CancellationToken cancellationToken)
        {
            var launchConfiguration = await launchProfileProducer(mode, cancellationToken).ConfigureAwait(false);
            if (launchConfiguration is null)
            {
                throw new InvalidOperationException(
                    $"The \"{launchConfigurationType}\" launch configuration producer for resource '{resourceName}' returned null. " +
                    $"The producer owns the complete launch configuration, so it must always return one.");
            }

            return launchConfiguration;
        }
    }
}
