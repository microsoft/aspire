// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard;
using Aspire.Shared;

// Native AOT's documentation signature parser requires targets without the leading '~'.
// https://github.com/dotnet/runtime/blob/main/src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/Logging/DocumentationSignatureParser.cs
[assembly: UnconditionalSuppressMessage("Trimming", "IL2110", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Routes.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Blazor's OpenComponent preserves all members of Router and LayoutView, including annotated backing fields and captured layout types. Dashboard component types are rooted by TrimmerRootAssembly. Remove when Blazor's component reflection requirements no longer produce these warnings.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2110", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Controls.GridValue.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Blazor's OpenComponent preserves all members of DynamicComponent, including its annotated Type backing field. Dashboard component types are rooted by TrimmerRootAssembly. Remove when Blazor's component reflection requirements no longer produce this warning.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2111", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Routes.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Blazor's OpenComponent preserves annotated Router and LayoutView setters and layout helpers through reflection. Dashboard page and layout types are rooted by TrimmerRootAssembly. Remove when Blazor's component reflection requirements no longer produce these warnings.")]
[assembly: UnconditionalSuppressMessage("Trimming", "IL2111", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Controls.GridValue.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Blazor's OpenComponent preserves the annotated DynamicComponent.Type setter through reflection. Dashboard dynamic component types are rooted by TrimmerRootAssembly. Remove when Blazor's component reflection requirements no longer produce this warning.")]

// OpenComponent preserves nested enums in FluentDataGrid and Virtualize, including inherited Enum.GetValues(Type).
// Suppress only the affected render methods (including their generated lambdas) and Razor type-inference helpers.
// These components don't call Enum.GetValues(Type). Remove when dependency enums no longer trigger preservation warnings.
// https://github.com/dotnet/aspnetcore/blob/main/src/Components/Components/src/Rendering/RenderTreeBuilder.cs
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.LogViewer.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening Virtualize preserves its nested InitialIndexPhase enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Pages.Metrics.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Controls.ResourceDetails.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Controls.SpanDetails.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Dialogs.ExemplarsDialog.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Dialogs.ManageDataDialog.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Pages.Resources.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Pages.StructuredLogs.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Pages.TraceDetail.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "member",
    Target = "M:Aspire.Dashboard.Components.Pages.Traces.BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder)",
    Justification = "Opening FluentDataGrid preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type",
    Target = "T:__Blazor.Aspire.Dashboard.Components.ChartFilters.TypeInference",
    Justification = "The generated FluentDataGrid factory preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type",
    Target = "T:__Blazor.Aspire.Dashboard.Components.Controls.MetricTable.TypeInference",
    Justification = "The generated FluentDataGrid factory preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type",
    Target = "T:__Blazor.Aspire.Dashboard.Components.Controls.PropertyGrid.TypeInference",
    Justification = "The generated FluentDataGrid factory preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type",
    Target = "T:__Blazor.Aspire.Dashboard.Components.Controls.ResourceDetails.TypeInference",
    Justification = "The generated FluentDataGrid factory preserves its nested ColumnHeaderUiKind enum and inherited Enum.GetValues(Type).")]
[assembly: UnconditionalSuppressMessage("AOT", "IL3050", Scope = "type",
    Target = "T:__Blazor.Aspire.Dashboard.Components.Controls.TextVisualizer.TypeInference",
    Justification = "The generated Virtualize factory preserves its nested InitialIndexPhase enum and inherited Enum.GetValues(Type).")]

// Hold our own lease so CLI updates cannot delete this bundle's files, including static assets,
// while the Dashboard is running, even after the launcher releases its lease.
// No lease is acquired when ASPIRE_BUNDLE_VERSION_DIR is unset (e.g. standalone or development runs).
BundleVersionLease? acquiredBundleLease;
try
{
    acquiredBundleLease = BundleVersionLease.TryAcquireFromEnvironment("aspire-dashboard", args.FirstOrDefault());
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine($"Failed to acquire Aspire bundle lease: {ex.Message}");
    return 1;
}

using var bundleLease = acquiredBundleLease;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};
var app = new DashboardWebApplication(options: options);

using var shutdownCts = new CancellationTokenSource();
var parentWatchdog = ParentProcessWatchdog.Start(shutdownCts);
try
{
    return await app.RunAsync(shutdownCts.Token).ConfigureAwait(false);
}
finally
{
    if (parentWatchdog is not null)
    {
        await parentWatchdog.DisposeAsync().ConfigureAwait(false);
    }
}
