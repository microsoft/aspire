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
