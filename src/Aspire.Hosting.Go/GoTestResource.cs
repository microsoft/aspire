// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Go;

/// <summary>
/// Represents a finite Go test invocation in the application model.
/// </summary>
/// <param name="name">The resource name.</param>
/// <param name="workingDirectory">The module or workspace directory used by the Go command.</param>
/// <remarks>
/// Create this resource with <see cref="GoTestingExtensions.AddGoTest"/>. It supports environment
/// variables, service references, endpoints, and dependency waits, but not Go application debugging
/// or the application-specific module installation helpers. The Go toolchain manages module downloads.
/// </remarks>
/// <example>
/// <code lang="csharp">
/// builder.AddGoTest("api-tests", "../go-api").WithArgs("-run=TestIntegration");
/// </code>
/// </example>
[AspireExport]
[Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class GoTestResource(string name, string workingDirectory)
    : ExecutableResource(name, "go", workingDirectory), IResourceWithServiceDiscovery;
