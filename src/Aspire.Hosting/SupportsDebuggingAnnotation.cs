// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation that specifies that the resource can be debugged by the Aspire Extension.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, RequiredExtensionId = {LaunchConfigurationType,nq}")]
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
internal sealed class SupportsDebuggingAnnotation : IResourceAnnotation
{
    private SupportsDebuggingAnnotation(string launchConfigurationType, Action<Executable, string> launchConfigurationAnnotator, bool rewritesArgumentsForDebugging)
    {
        LaunchConfigurationType = launchConfigurationType;
        LaunchConfigurationAnnotator = launchConfigurationAnnotator;
        RewritesArgumentsForDebugging = rewritesArgumentsForDebugging;
    }

    public string LaunchConfigurationType { get; }
    public Action<Executable, string> LaunchConfigurationAnnotator { get; }

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

    internal static SupportsDebuggingAnnotation Create<T>(string launchConfigurationType, Func<string, T> launchProfileProducer, bool rewritesArgumentsForDebugging = false)
    {
        return new SupportsDebuggingAnnotation(launchConfigurationType, (exe, mode) =>
        {
            exe.AnnotateAsObjectList(Executable.LaunchConfigurationsAnnotation, launchProfileProducer(mode));
        }, rewritesArgumentsForDebugging);
    }
}
