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
/// Added by <see cref="ResourceBuilderExtensions.WithDebugSupport{T, TLaunchConfiguration}(IResourceBuilder{T}, Func{LaunchConfigurationCallbackContext, Task{TLaunchConfiguration}}, string, Action{CommandLineArgsCallbackContext})"/>.
/// The annotation is only honored while a debug session is active; use
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
        Func<Executable, LaunchConfigurationCallbackContext, Task> launchConfigurationAnnotator,
        Func<LaunchConfigurationCallbackContext, Task<object>> launchConfigurationProducer,
        CommandLineArgsCallbackAnnotation? debugCommandLineArgsCallbackAnnotation,
        bool rewritesArgumentsForDebugging)
    {
        LaunchConfigurationType = launchConfigurationType;
        LaunchConfigurationAnnotator = launchConfigurationAnnotator;
        LaunchConfigurationProducer = launchConfigurationProducer;
        DebugCommandLineArgsCallbackAnnotation = debugCommandLineArgsCallbackAnnotation;
        RewritesArgumentsForDebugging = rewritesArgumentsForDebugging;
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

    // The producer callback passed to WithDebugSupport, with the launch configuration boxed as object.
    // Internal because it hands out an untyped object; DebugSupportExtensions.CreateLaunchConfigurationAsync is
    // the supported way to reach it.
    internal Func<LaunchConfigurationCallbackContext, Task<object>> LaunchConfigurationProducer { get; }

    internal CommandLineArgsCallbackAnnotation? DebugCommandLineArgsCallbackAnnotation { get; }

    /// <summary>
    /// Indicates that the debug support rewrites the resource's command-line arguments while a debug
    /// session is active (via the <c>argsCallback</c> passed to <c>WithDebugSupport</c>).
    /// </summary>
    /// <remarks>
    /// Integrations such as Go and Python strip the process entrypoint tokens 
    /// (e.g. <c>go run &lt;pkg&gt;</c>, <c>python -m &lt;mod&gt;</c>)
    /// so the IDE debugger can own them, which leaves the executable's <c>Spec.Args</c> valid 
    /// only for IDE execution. When this is <see langword="true"/>, a Process fallback 
    /// (either the DCP-level <c>FallbackExecutionTypes</c> or the in-process fallback when the launch configuration fails) 
    /// would attempt to run <c>ExecutablePath + Args</c> with the entrypoint stripped — a broken command — 
    /// so a process fallback must NOT be offered.
    /// <para>
    /// This is set based purely on the presence of an <c>argsCallback</c> in <c>WithDebugSupport</c>,
    /// not on whether that callback actually rewrites anything for a given resource configuration. This is a
    /// deliberate, conservative rule: a resource that supplies an args callback forgoes the process fallback
    /// even when the callback happens to be a no-op (e.g. a Python "Executable" entrypoint), keeping the rule
    /// simple and predictable.
    /// </para>
    /// </remarks>
    public bool RewritesArgumentsForDebugging { get; }

    internal static SupportsDebuggingAnnotation Create<T>(
        string resourceName,
        string launchConfigurationType,
        Func<LaunchConfigurationCallbackContext, Task<T>> launchConfigurationProducer,
        CommandLineArgsCallbackAnnotation? debugCommandLineArgsCallbackAnnotation = null,
        bool rewritesArgumentsForDebugging = false)
    {
        // The annotator stays generic over T so the DCP annotation is serialized against the concrete
        // launch configuration type rather than a boxed object, which would change the emitted JSON.
        return new SupportsDebuggingAnnotation(
            launchConfigurationType,
            async (exe, context) =>
                exe.AnnotateAsObjectList(
                    Executable.LaunchConfigurationsAnnotation,
                    await ProduceAsync(context).ConfigureAwait(false)),
            // The suppression is safe because ProduceAsync throws rather than returning null; the
            // compiler cannot see that because T is unconstrained and so may be a nullable type.
            async context => (await ProduceAsync(context).ConfigureAwait(false))!,
            debugCommandLineArgsCallbackAnnotation,
            rewritesArgumentsForDebugging);

        async Task<T> ProduceAsync(LaunchConfigurationCallbackContext context)
        {
            Task<T>? producerTask;
            try
            {
                producerTask = launchConfigurationProducer(context);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
            {
                throw CreateProducerException(exception);
            }

            if (producerTask is null)
            {
                throw new InvalidOperationException(
                    $"The \"{launchConfigurationType}\" launch configuration producer for resource '{resourceName}' returned a null task. " +
                    "The producer must return a task that produces the complete launch configuration.");
            }

            T launchConfiguration;
            try
            {
                launchConfiguration = await producerTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
            {
                throw CreateProducerException(exception);
            }

            if (launchConfiguration is null)
            {
                throw new InvalidOperationException(
                    $"The \"{launchConfigurationType}\" launch configuration producer for resource '{resourceName}' returned null. " +
                    $"The producer owns the complete launch configuration, so it must always return one.");
            }

            return launchConfiguration;
        }

        InvalidOperationException CreateProducerException(Exception innerException)
        {
            return new InvalidOperationException(
                $"The \"{launchConfigurationType}\" launch configuration producer for resource '{resourceName}' failed.",
                innerException);
        }
    }
}
