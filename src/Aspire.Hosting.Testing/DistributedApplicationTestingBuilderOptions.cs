// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Options that control how an <see cref="IDistributedApplicationTestingBuilder"/> is created.
/// </summary>
/// <remarks>
/// These options are applied while the underlying distributed application builder is being constructed, because the
/// dashboard services themselves are selected during construction and cannot be added afterwards. The settings those
/// services read - port allocation, interactivity, and the generated browser token - remain
/// adjustable through the returned builder until the application is built.
/// </remarks>
/// <example>
/// The following example creates a testing builder that runs the dashboard:
/// <code lang="csharp">
/// var options = new DistributedApplicationTestingBuilderOptions
/// {
///     EnableDashboard = true
/// };
///
/// var builder = await DistributedApplicationTestingBuilder.CreateAsync&lt;Projects.MyAppHost_AppHost&gt;(options, []);
/// await using var app = await builder.BuildAsync();
/// await app.StartAsync();
///
/// // Open this in a browser to inspect the running application.
/// var dashboardUrl = await app.GetDashboardUrlAsync();
/// </code>
/// </example>
public sealed class DistributedApplicationTestingBuilderOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the Aspire dashboard runs alongside the application under test.
    /// </summary>
    /// <value>
    /// <see langword="true"/> to run the dashboard; otherwise, <see langword="false"/>.
    /// The default is <see langword="false"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// When the dashboard runs, it listens on loopback endpoints using dynamically assigned ports, so concurrent
    /// test applications do not compete for a fixed port. Each application generates its own browser token instead
    /// of reusing an ambient dashboard credential. Dashboard instances in the same browser still share the dashboard
    /// authentication cookie, so use separate browser contexts when tests require isolation between dashboards. Use
    /// <see cref="DistributedApplicationHostingTestingExtensions.GetDashboardUrlAsync"/> to obtain a browser-ready
    /// URL for the running dashboard.
    /// </para>
    /// <para>
    /// Enabling the dashboard sets <see cref="ResourceNotificationServiceOptions.DefaultWaitBehavior"/> to
    /// <see cref="WaitBehavior.StopOnResourceUnavailable"/> after AppHost configuration. To use a different default
    /// for <c>WaitFor</c>, configure <see cref="ResourceNotificationServiceOptions"/> through the returned builder
    /// before building the application.
    /// </para>
    /// <para>
    /// The dashboard process remains reachable when a debugger pauses the test process, but resource state changes,
    /// logs, and commands flow through the paused process and do not update until execution resumes.
    /// </para>
    /// </remarks>
    public bool EnableDashboard { get; set; }
}
