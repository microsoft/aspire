// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Represents an executable MSTest project that participates in an Aspire test run.
/// </summary>
/// <remarks>
/// Use <see cref="MsTestHostingExtensions.AddMsTest"/> to configure execution and result collection.
/// The project must enable the Microsoft.Testing.Platform runner and its TRX report extension.
/// This resource supports environment variables, endpoints, references, and dependency waits like
/// other executable resources. It does not participate in coordinated .NET project builds or publishing.
/// </remarks>
[AspireExport(ExposeProperties = true)]
[Experimental(MsTestDiagnostics.DiagnosticId, UrlFormat = MsTestDiagnostics.UrlFormat)]
public class MsTestResource : ExecutableResource, IResourceWithServiceDiscovery
{
    /// <summary>
    /// Initializes a resource representing an MSTest executable.
    /// </summary>
    /// <param name="name">The resource's application model name.</param>
    /// <param name="workingDirectory">The directory containing the test project.</param>
    /// <remarks>The constructor does not configure launch arguments or register a result collector.</remarks>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    /// <exception cref="ArgumentNullException">The name or working directory is null.</exception>
    [Experimental(MsTestDiagnostics.DiagnosticId, UrlFormat = MsTestDiagnostics.UrlFormat)]
    public MsTestResource(string name, string workingDirectory) : base(name, "dotnet", workingDirectory)
    {
    }
}

internal static class MsTestDiagnostics
{
    internal const string DiagnosticId = "ASPIRETESTING001";
    internal const string UrlFormat = "https://aka.ms/aspire/diagnostics/{0}";
}
