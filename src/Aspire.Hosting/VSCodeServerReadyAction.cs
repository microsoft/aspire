// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using DcpModel = Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting;

/// <summary>
/// Represents VS Code's <c>serverReadyAction</c> debug configuration for a .NET project resource.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="ProjectResourceBuilderExtensions.WithVSCodeServerReadyAction{TProjectResource}(Aspire.Hosting.ApplicationModel.IResourceBuilder{TProjectResource})"/>
/// or <see cref="ProjectResourceBuilderExtensions.WithVSCodeServerReadyAction{TProjectResource}(Aspire.Hosting.ApplicationModel.IResourceBuilder{TProjectResource}, VSCodeServerReadyAction)"/>
/// to apply this configuration to a <see cref="ApplicationModel.ProjectResource"/>.
/// </para>
/// <para>
/// This configuration only affects IDE debugging sessions launched through the Aspire VS Code extension.
/// It does not affect normal process execution, published deployments, or non-project resources.
/// </para>
/// <para>
/// The default values match ASP.NET Core's standard <c>Now listening on:</c> output and open the exact runtime URL
/// emitted by the application, including any port selected dynamically by Aspire.
/// </para>
/// </remarks>
/// <example>
/// Add the default browser-launch behavior for a project resource:
/// <code lang="csharp">
/// var builder = DistributedApplication.CreateBuilder(args);
///
/// builder.AddProject&lt;Projects.ApiService&gt;("api")
///     .WithVSCodeServerReadyAction();
/// </code>
/// </example>
/// <example>
/// Start a follow-up debug configuration after the server is ready:
/// <code lang="csharp">
/// var builder = DistributedApplication.CreateBuilder(args);
///
/// builder.AddProject&lt;Projects.ApiService&gt;("api")
///     .WithVSCodeServerReadyAction(new VSCodeServerReadyAction
///     {
///         Action = "startDebugging",
///         Pattern = @"\bNow listening on:\s+(https?://\S+)",
///         Name = "Attach browser"
///     });
/// </code>
/// </example>
public sealed class VSCodeServerReadyAction
{
    /// <summary>
    /// Gets or sets the action to take when the server is ready.
    /// </summary>
    /// <remarks>
    /// Common values include <c>openExternally</c>, <c>debugWithChrome</c>, <c>debugWithEdge</c>, and <c>startDebugging</c>.
    /// The default is <c>openExternally</c>.
    /// </remarks>
    public string Action { get; set; } = "openExternally";

    /// <summary>
    /// Gets or sets the pattern used to detect the server-ready message in the application output.
    /// </summary>
    /// <remarks>
    /// The default pattern matches ASP.NET Core's standard <c>Now listening on:</c> log output.
    /// </remarks>
    public string Pattern { get; set; } = @"\bNow listening on:\s+(https?://\S+)";

    /// <summary>
    /// Gets or sets the URI format to launch when the server is ready.
    /// </summary>
    /// <remarks>
    /// The default value is <c>%s</c>, which tells VS Code to open the exact URL captured from the application output.
    /// </remarks>
    public string? UriFormat { get; set; } = "%s";

    /// <summary>
    /// Gets or sets the web root used to resolve source maps and client-side files for browser-based debugging.
    /// </summary>
    public string? WebRoot { get; set; }

    /// <summary>
    /// Gets or sets the name of the debug configuration to start when <see cref="Action"/> is <c>startDebugging</c>.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets an inline debug configuration to start when <see cref="Action"/> is <c>startDebugging</c>.
    /// </summary>
    /// <remarks>
    /// The schema for this object depends on the debug adapter being used, so it is represented as arbitrary JSON.
    /// </remarks>
    public JsonObject? Config { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the browser session should be closed when the server stops.
    /// </summary>
    public bool? KillOnServerStop { get; set; }

    internal DcpModel.ServerReadyAction ToDcpServerReadyAction()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(Pattern);

        return new DcpModel.ServerReadyAction
        {
            Action = new DcpModel.ServerReadyActionAction(Action),
            Pattern = Pattern,
            UriFormat = UriFormat,
            WebRoot = WebRoot,
            Name = Name,
            Config = Config,
            KillOnServerStop = KillOnServerStop
        };
    }
}
