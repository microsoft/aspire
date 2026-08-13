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
/// Added by a <c>WithDebugSupport</c> overload on <see cref="ResourceBuilderExtensions"/>.
/// The annotation is only honored while a debug session is active; use
/// <see cref="DebugSupportExtensions.SupportsDebugging"/> to test for that.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, RequiredExtensionId = {LaunchConfigurationType,nq}")]
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class SupportsDebuggingAnnotation : IResourceAnnotation
{
    private SupportsDebuggingAnnotation(
        string launchConfigurationType,
        Func<Executable, LaunchConfigurationCallbackContext, Task> launchConfigurationAnnotator,
        Func<LaunchConfigurationCallbackContext, Task<object>> launchConfigurationProducer)
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
    internal Func<Executable, LaunchConfigurationCallbackContext, Task> LaunchConfigurationAnnotator { get; }

    // The producer callback supplied to WithDebugSupport, with the launch configuration boxed as object.
    // Internal because only Aspire constructs LaunchConfigurationCallbackContext values and because the
    // untyped object is consumed by internal launch-configuration plumbing.
    internal Func<LaunchConfigurationCallbackContext, Task<object>> LaunchConfigurationProducer { get; }

    internal static SupportsDebuggingAnnotation Create<T>(
        string resourceName,
        string launchConfigurationType,
        Func<LaunchConfigurationCallbackContext, Task<T>> launchConfigurationProducer)
    {
        // The annotator stays generic over T so the DCP annotation is serialized against the concrete
        // launch configuration type rather than a boxed object, which would change the emitted JSON.
        return new SupportsDebuggingAnnotation(
            launchConfigurationType,
            async (exe, context) => exe.AnnotateAsObjectList(
                Executable.LaunchConfigurationsAnnotation,
                await ProduceAsync(context).ConfigureAwait(false)),
            // The suppression is safe because ProduceAsync throws rather than returning null; the
            // compiler cannot see that because T is unconstrained and so may be a nullable type.
            async context => (await ProduceAsync(context).ConfigureAwait(false))!);

        async Task<T> ProduceAsync(LaunchConfigurationCallbackContext context)
        {
            var launchConfiguration = await launchConfigurationProducer(context).ConfigureAwait(false);
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
