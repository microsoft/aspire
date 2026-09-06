// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class TerminalViewTests : DashboardTestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnly_InitialAndUpdatedStatePreservesConnection(bool initialReadOnly)
    {
        Services.AddLocalization();
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        init.SetResult(1);
        var update = module.SetupVoid("setReadOnly", _ => true);
        update.SetVoidResult();
        var reconnect = module.Setup<int>("reconnectTerminal", _ => true);
        reconnect.SetResult(2);
        var dispose = module.SetupVoid("disposeTerminal", _ => true);
        dispose.SetVoidResult();

        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=terminal")
            .Add(p => p.ReadOnly, initialReadOnly));

        var options = Assert.IsType<TerminalViewOptions>(Assert.Single(init.Invocations).Arguments[3]);
        Assert.Equal(initialReadOnly, options.ReadOnly);
        Assert.Empty(update.Invocations);

        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, !initialReadOnly));
        cut.WaitForAssertion(() =>
            Assert.Equal(new object?[] { 1, !initialReadOnly }, Assert.Single(update.Invocations).Arguments));

        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, initialReadOnly));
        cut.WaitForAssertion(() =>
        {
            Assert.Collection(update.Invocations,
                invocation => Assert.Equal(new object?[] { 1, !initialReadOnly }, invocation.Arguments),
                invocation => Assert.Equal(new object?[] { 1, initialReadOnly }, invocation.Arguments));
        });

        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, initialReadOnly));
        Assert.Equal(2, update.Invocations.Count);
        Assert.Single(init.Invocations);
        Assert.Empty(reconnect.Invocations);
        Assert.Empty(dispose.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnly_ChangedDuringInitializationAppliesLatestState(bool initialReadOnly)
    {
        Services.AddLocalization();
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        var update = module.SetupVoid("setReadOnly", _ => true);
        update.SetVoidResult();
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();

        var cut = RenderComponent<TerminalView>(builder => builder
            .Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=terminal")
            .Add(p => p.ReadOnly, initialReadOnly));
        Assert.Single(init.Invocations);

        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, !initialReadOnly));
        init.SetResult(1);

        cut.WaitForAssertion(() =>
            Assert.Equal(new object?[] { 1, !initialReadOnly }, Assert.Single(update.Invocations).Arguments));
        Assert.Single(init.Invocations);
    }

    [Fact]
    public void ReadOnly_ChangedDuringUpdateAppliesLatestState()
    {
        Services.AddLocalization();
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        module.Setup<int>("initTerminal", _ => true).SetResult(1);
        var update = module.SetupVoid("setReadOnly", _ => true);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();

        var cut = RenderComponent<TerminalView>(builder =>
            builder.Add(p => p.EndpointPathAndQuery, "/api/apphost-terminal?terminalId=terminal"));
        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, true));
        cut.WaitForAssertion(() => Assert.Single(update.Invocations));

        cut.SetParametersAndRender(builder => builder.Add(p => p.ReadOnly, false));
        Assert.Single(update.Invocations);
        update.SetVoidResult();

        cut.WaitForAssertion(() =>
            Assert.Collection(update.Invocations,
                invocation => Assert.Equal(new object?[] { 1, true }, invocation.Arguments),
                invocation => Assert.Equal(new object?[] { 1, false }, invocation.Arguments)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedInitialization_SameEndpointRenderRetries(bool endpointOnFirstRender)
    {
        Services.AddLocalization();
        var fail = true;
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var failedInit = module.Setup<int>("initTerminal", _ => fail);
        failedInit.SetException(new JSException("Initialization failed"));
        var successfulInit = module.Setup<int>("initTerminal", _ => !fail);
        successfulInit.SetResult(1);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();

        const string Endpoint = "/api/apphost-terminal?terminalId=terminal";
        var cut = RenderComponent<TerminalView>(builder =>
            builder.Add(p => p.EndpointPathAndQuery, endpointOnFirstRender ? Endpoint : null));
        if (!endpointOnFirstRender)
        {
            cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        }

        cut.WaitForAssertion(() => Assert.Equal(endpointOnFirstRender ? 2 : 1, failedInit.Invocations.Count));
        fail = false;
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        cut.WaitForAssertion(() => Assert.Single(successfulInit.Invocations));

        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        Assert.Single(successfulInit.Invocations);
    }

    [Fact]
    public void FailedInitialization_RetryDoesNotReenterWhilePending()
    {
        Services.AddLocalization();
        var fail = true;
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var failedInit = module.Setup<int>("initTerminal", _ => fail);
        failedInit.SetException(new JSException("Initialization failed"));
        var retry = module.Setup<int>("initTerminal", _ => !fail);
        module.SetupVoid("disposeTerminal", _ => true).SetVoidResult();

        const string Endpoint = "/api/apphost-terminal?terminalId=terminal";
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        cut.WaitForAssertion(() => Assert.Equal(2, failedInit.Invocations.Count));
        fail = false;
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        cut.WaitForAssertion(() => Assert.Single(retry.Invocations));
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        Assert.Single(retry.Invocations);
        retry.SetResult(1);
    }

    [Theory]
    [InlineData("/api/apphost-terminal?terminalId=second")]
    [InlineData(null)]
    public void PendingInitializationRetry_ReconcilesChangedEndpoint(string? updatedEndpoint)
    {
        Services.AddLocalization();
        var fail = true;
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var failedInit = module.Setup<int>("initTerminal", _ => fail);
        failedInit.SetException(new JSException("Initialization failed"));
        var retry = module.Setup<int>("initTerminal", _ => !fail);
        var reconnect = module.Setup<int>("reconnectTerminal", _ => true);
        reconnect.SetResult(1);
        var dispose = module.SetupVoid("disposeTerminal", _ => true);
        dispose.SetVoidResult();

        const string Endpoint = "/api/apphost-terminal?terminalId=first";
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        cut.WaitForAssertion(() => Assert.Equal(2, failedInit.Invocations.Count));
        fail = false;
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        cut.WaitForAssertion(() => Assert.Single(retry.Invocations));

        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, updatedEndpoint));
        retry.SetResult(1);
        cut.WaitForAssertion(() =>
        {
            Assert.Single(retry.Invocations);
            if (updatedEndpoint is null)
            {
                Assert.Single(dispose.Invocations);
            }
            else
            {
                var invocation = Assert.Single(reconnect.Invocations);
                Assert.Equal($"ws://localhost{updatedEndpoint}", invocation.Arguments[1]);
            }
        });
    }

    [Fact]
    public void EndpointRemovedDuringInitialization_CanAttachAgain()
    {
        Services.AddLocalization();
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        var init = module.Setup<int>("initTerminal", _ => true);
        var dispose = module.SetupVoid("disposeTerminal", _ => true);
        dispose.SetVoidResult();

        const string Endpoint = "/api/apphost-terminal?terminalId=terminal";
        var cut = RenderComponent<TerminalView>(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, null));
        init.SetResult(1);
        cut.WaitForAssertion(() => Assert.Single(dispose.Invocations));

        cut.SetParametersAndRender(builder => builder.Add(p => p.EndpointPathAndQuery, Endpoint));
        cut.WaitForAssertion(() => Assert.Equal(2, init.Invocations.Count));
    }
}
