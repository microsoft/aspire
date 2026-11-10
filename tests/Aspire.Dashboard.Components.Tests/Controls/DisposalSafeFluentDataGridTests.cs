// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public sealed class DisposalSafeFluentDataGridTests : DashboardTestContext
{
    private const string DataGridModulePath = "./_content/Microsoft.FluentUI.AspNetCore.Components/Components/DataGrid/FluentDataGrid.razor.js";
    private const string InitializeGrid = "Microsoft.FluentUI.Blazor.DataGrid.Initialize";
    private const string EnableColumnResizing = "Microsoft.FluentUI.Blazor.DataGrid.EnableColumnResizing";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedWhileInitializing_IgnoresTheLateCallInsteadOfEndingTheCircuit(bool aspireGrid)
    {
        var (module, controller) = aspireGrid
            ? await RemoveGridWhileItInitializesAsync<AspireFluentDataGrid<string>>(resizableColumns: true)
            : await RemoveGridWhileItInitializesAsync<DisposalSafeFluentDataGrid<string>>(resizableColumns: true);

        // The grid resumed after disposal and reached its released module, which rejected the call. The grid ignored
        // the rejection, so nothing reached the renderer, which would have ended the circuit. The controller that
        // arrived after disposal was stopped and released.
        Assert.Equal([EnableColumnResizing], module.RejectedInvocations);
        Assert.False(Renderer.UnhandledException.IsCompleted);
        AssertStoppedAndReleased(controller);
    }

    [Fact]
    public async Task RemovedWhileInitializing_StopsTheGridControllerThatArrivesAfterDisposal()
    {
        // Without resizable or reorderable columns, the render that resumes after disposal makes no other call and
        // completes without an error, but the controller still arrives after Fluent UI's cleanup ran.
        var (module, controller) = await RemoveGridWhileItInitializesAsync<DisposalSafeFluentDataGrid<string>>(resizableColumns: false);

        Assert.Empty(module.RejectedInvocations);
        Assert.False(Renderer.UnhandledException.IsCompleted);
        AssertStoppedAndReleased(controller);
    }

    // Fluent UI bug https://github.com/microsoft/fluentui-blazor/issues/5580, which DisposalSafeFluentDataGrid works
    // around. This test fails once the Fluent UI version used here fixes it: then remove DisposalSafeFluentDataGrid, as
    // its remarks describe, and this file.
    [Fact]
    public async Task FluentDataGrid_StillCallsIntoItsDisposedModuleWhenRemovedWhileInitializing()
    {
        var (module, controller) = await RemoveGridWhileItInitializesAsync<FluentDataGrid<string>>(resizableColumns: true);

        Assert.Equal([EnableColumnResizing], module.RejectedInvocations);
        Assert.IsType<ObjectDisposedException>(await Renderer.UnhandledException.DefaultTimeout());
        // Fluent UI also leaves the controller that arrived after disposal running.
        Assert.Empty(controller.Invocations);
        Assert.Equal(0, controller.DisposeCount);
    }

    private static void AssertStoppedAndReleased(TestJSObjectReference controller)
    {
        Assert.Equal(["stop"], controller.Invocations.Select(invocation => invocation.Identifier));
        Assert.Equal(1, controller.DisposeCount);
    }

    // Renders a grid, removes it while its first OnAfterRenderAsync awaits the grid's JavaScript initialization, and
    // then lets the initialization complete, returning the grid's controller after disposal. This follows the order seen
    // in the dashboard: the grid renders its rows while its JavaScript module is still loading, which leaves the setup of
    // resizable columns for later, and is removed after the module arrives but before the grid's initialization returns.
    private async Task<(TestJSObjectReference Module, TestJSObjectReference Controller)> RemoveGridWhileItInitializesAsync<TGrid>(bool resizableColumns)
        where TGrid : FluentDataGrid<string>
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        var initializeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialize = new TaskCompletionSource();
        var controller = new TestJSObjectReference();
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = async identifier =>
            {
                if (identifier == InitializeGrid)
                {
                    initializeStarted.TrySetResult();
                    await releaseInitialize.Task;
                }
            },
            GetResult = identifier => identifier == InitializeGrid ? controller : null
        };
        var import = TestJSObjectReference.SetupImport(this, DataGridModulePath);

        var cut = Render<TGrid>(builder => builder
            .Add(grid => grid.ItemsProvider, EnumerableGridItemsProvider.Create(() => new[] { "value" }))
            .Add(grid => grid.ResizableColumns, resizableColumns)
            .Add(grid => grid.ChildContent, RenderColumn));

        // The render that shows the rows comes before the module, so it leaves the column setup for later.
        cut.WaitForAssertion(() => Assert.Equal("value", cut.Find("[role=gridcell]").TextContent));
        await Renderer.Dispatcher.InvokeAsync(() => { });
        Assert.Empty(module.Invocations);

        import.SetResult(module);
        await initializeStarted.Task.DefaultTimeout();

        // Removing the grid disposes it, and its module, while the initialization is pending. This test resumes on the
        // thread pool while the dispatcher may still be finishing the grid's work item, so the removal runs on the
        // dispatcher: from another thread, DisposeComponentsAsync queues the removal there but clears its list of
        // rendered components right away, and the queued removal can then find nothing to remove.
        await Renderer.Dispatcher.InvokeAsync(DisposeComponentsAsync);
        Assert.Equal(1, module.DisposeCount);

        // The initialization completes after disposal, so the grid's OnAfterRenderAsync resumes. Its continuation is
        // posted to the renderer's dispatcher, and runs before this empty work item queued after it.
        releaseInitialize.SetResult();
        await Renderer.Dispatcher.InvokeAsync(() => { });
        return (module, controller);

        static void RenderColumn(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PropertyColumn<string, string>>(0);
            builder.AddComponentParameter(1, nameof(PropertyColumn<string, string>.Title), "Value");
            builder.AddComponentParameter(2, nameof(PropertyColumn<string, string>.Property), (Expression<Func<string, string>>)(value => value));
            builder.CloseComponent();
        }
    }
}
