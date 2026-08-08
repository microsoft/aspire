// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Options that control how an <see cref="IDistributedApplicationTestingBuilder"/> is created.
/// </summary>
/// <remarks>
/// These options are applied while the underlying distributed application builder is being constructed, because
/// dashboard services and dashboard authentication are selected during construction and cannot be added afterwards.
/// Settings that remain adjustable at runtime, such as port allocation, interactivity, and dependency waiting, can
/// still be overridden through the returned builder before the application is built.
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
/// var loginUrl = await app.GetDashboardLoginUrlAsync();
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
    /// When the dashboard runs, it listens on authenticated loopback endpoints using dynamically assigned ports so
    /// that concurrent test applications cannot collide or reach each other. Use
    /// <see cref="DistributedApplicationHostingTestingExtensions.GetDashboardLoginUrlAsync"/> to obtain an
    /// authenticated URL for the running dashboard.
    /// </remarks>
    public bool EnableDashboard { get; set; }

    /// <summary>
    /// Gets or sets the default <see cref="WaitBehavior"/> applied when a resource waits on a dependency that has
    /// become unavailable.
    /// </summary>
    /// <value>
    /// The wait behavior to apply, or <see langword="null"/> to use the default.
    /// The default is <see cref="WaitBehavior.StopOnResourceUnavailable"/>, which fails the test run instead of
    /// hanging it.
    /// </value>
    /// <remarks>
    /// Set this to <see cref="WaitBehavior.WaitOnResourceUnavailable"/> when debugging with
    /// <see cref="EnableDashboard"/>, so that a failing dependency keeps the application alive long enough to be
    /// inspected in the dashboard instead of tearing it down. This option has no effect unless the dashboard runs,
    /// because the testing builder already fails fast otherwise.
    /// </remarks>
    public WaitBehavior? DefaultWaitBehavior { get; set; }
}
