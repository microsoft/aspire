#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a specified executable process.
/// </summary>
/// <remarks>
/// You can run any executable command using its full path.
/// As a security feature, Aspire doesn't run executable unless the command is located in a path listed in the PATH environment variable.
/// <para/> 
/// To run an executable file that's in the current directory, specify the full path or use the relative path <c>./</c> to represent the current directory.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, Command = {Command}")]
public class ExecutableResource : Resource, IResourceWithEnvironment, IResourceWithArgs, IResourceWithEndpoints, IResourceWithWaitSupport, IResourceWithProbes,
    IComputeResource
{
    /// <param name="name">The name of the resource.</param>
    /// <param name="command">The command to execute.</param>
    /// <param name="workingDirectory">The working directory of the executable. Can be empty.</param>
    public ExecutableResource(string name, string command, string workingDirectory) : base(name)
    {
        Annotations.Add(new ExecutableAnnotation
        {
            Command = ThrowIfNullOrEmpty(command),
            WorkingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory)),
        });
    }

    /// <summary>
    /// Gets the command associated with this executable resource.
    /// </summary>
    public string Command => GetAnnotation().Command;

    /// <summary>
    /// Gets the working directory for the executable resource.
    /// </summary>
    public string WorkingDirectory => GetAnnotation().WorkingDirectory;

    private ExecutableAnnotation GetAnnotation() => Annotations.OfType<ExecutableAnnotation>().LastOrDefault()
        ?? throw new InvalidOperationException("Unable to find ExecutableAnnotation on resource.");

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }
}
