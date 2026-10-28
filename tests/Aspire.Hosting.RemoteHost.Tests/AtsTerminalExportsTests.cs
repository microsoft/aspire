// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.Tests;
using Aspire.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#pragma warning disable ASPIRETERMINAL001 // Tests for the experimental terminal ATS surface.

namespace Aspire.Hosting.RemoteHost.Tests;

public class AtsTerminalExportsTests
{
    [Fact]
    public async Task Dispatch_TerminalCreationLookupCallbackAndDisposalRoundTrip()
    {
        await using var terminals = new TerminalService(NullLogger<TerminalService>.Instance, new ConfigurationBuilder().Build());
        var interactions = new TestInteractionService();
        await using var services = new ServiceCollection().AddSingleton(terminals).AddSingleton<IInteractionService>(interactions).BuildServiceProvider();
        var handles = new HandleRegistry();
        var ctRegistry = new CancellationTokenRegistry();
        var scanned = AtsCapabilityScanner.ScanAssembly(typeof(DistributedApplication).Assembly);
        var context = new AtsContext
        {
            Capabilities = scanned.Capabilities,
            HandleTypes = scanned.HandleTypes,
            DtoTypes = scanned.DtoTypes,
            EnumTypes = scanned.EnumTypes
        };
        var invoker = new TestCallbackInvoker();
        AtsCallbackProxyFactory? callbacks = null;
        var marshaller = new AtsMarshaller(handles, context, ctRegistry, new Lazy<AtsCallbackProxyFactory>(() => callbacks!));
        callbacks = new AtsCallbackProxyFactory(invoker, handles, ctRegistry, marshaller);
        var dispatcher = new CapabilityDispatcher(handles, marshaller, [typeof(DistributedApplication).Assembly]);
        var providerHandle = handles.Register(services, AtsTypeMapping.DeriveTypeId(typeof(IServiceProvider)));
        var terminalService = dispatcher.Invoke("Aspire.Hosting/getTerminalService",
            new JsonObject { ["serviceProvider"] = new JsonObject { ["$handle"] = providerHandle } });
        var interactionService = dispatcher.Invoke("Aspire.Hosting/getInteractionService",
            new JsonObject { ["serviceProvider"] = new JsonObject { ["$handle"] = providerHandle } });
        var terminal = dispatcher.Invoke("Aspire.Hosting/createTerminal", new JsonObject
        {
            ["terminalService"] = terminalService!.DeepClone(),
            ["options"] = new JsonObject
            {
                ["title"] = "Guest terminal",
                ["executable"] = "must-not-be-started",
                ["placement"] = "Dialog",
                ["arguments"] = new JsonArray("argument"),
                ["environmentVariables"] = new JsonObject { ["GUEST_TEST"] = "value" },
                ["columns"] = 100,
                ["rows"] = 30
            }
        });
        Assert.NotNull(terminal);
        Assert.NotNull(terminal["$handle"]);
        var id = dispatcher.Invoke("Aspire.Hosting.ApplicationModel/AspireTerminal.id", new JsonObject { ["context"] = terminal.DeepClone() })!.GetValue<string>();
        var native = Assert.Single(terminals.ListAll());
        Assert.Equal(id, native.Id);
        var lookedUp = dispatcher.Invoke("Aspire.Hosting/getTerminal",
            new JsonObject { ["terminalService"] = terminalService.DeepClone(), ["terminalId"] = id });
        var unmarshalContext = new AtsMarshaller.UnmarshalContext { CapabilityId = "Aspire.Hosting/getTerminal", ParameterName = "terminal" };
        Assert.Same(marshaller.UnmarshalFromJson(terminal, typeof(AspireTerminal), unmarshalContext),
            marshaller.UnmarshalFromJson(lookedUp, typeof(AspireTerminal), unmarshalContext));

        invoker.OnInvoke = (callbackId, args, _) =>
        {
            Assert.Equal("terminal-work", callbackId);
            var token = dispatcher.Invoke("Aspire.Hosting/TerminalContext.cancellationToken",
                new JsonObject { ["context"] = args!["arg"]!.DeepClone() });
            Assert.NotNull(token);
            // Re-enter the dispatcher while the prompt is waiting on guest work.
            dispatcher.Invoke("Aspire.Hosting/AspireTerminal.show", new JsonObject { ["terminal"] = terminal.DeepClone() });
            return Task.CompletedTask;
        };
        var result = dispatcher.Invoke("Aspire.Hosting/promptTerminal", new JsonObject
        {
            ["interactionService"] = interactionService!.DeepClone(),
            ["message"] = "Working",
            ["terminal"] = terminal.DeepClone(),
            ["options"] = new JsonObject { ["title"] = "Setup", ["work"] = "terminal-work" }
        });
        Assert.False(result!["canceled"]!.GetValue<bool>());
        Assert.True(result["value"]!.GetValue<bool>());
        Assert.Single(invoker.Invocations);
        Assert.True(terminals.TryGetTerminal(id, out _));

        dispatcher.Invoke("Aspire.Hosting/AspireTerminal.dispose", new JsonObject { ["terminal"] = terminal.DeepClone() });
        Assert.False(terminals.TryGetTerminal(id, out _));
        Assert.Null(dispatcher.Invoke("Aspire.Hosting/getTerminal",
            new JsonObject { ["terminalService"] = terminalService.DeepClone(), ["terminalId"] = id }));
    }
}
