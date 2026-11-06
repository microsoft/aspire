// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class MissingParametersPanelTests : DashboardTestContext
{
    [Fact]
    public async Task EditingValue_DoesNotChangeSharedCommandInput()
    {
        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Text, Value = "original" };
        var parameter = CreateParameter(CreateSetCommand(input));

        var cut = RenderPanel(parameter, isReadOnly: false);
        await cut.InvokeAsync(() => cut.FindComponent<FluentTextInput>().Instance.ValueChanged.InvokeAsync("draft"));

        // The input belongs to the resource, which every tab showing it shares.
        Assert.Equal("original", input.Value);
        Assert.Equal("draft", cut.FindComponent<FluentTextInput>().Instance.Value);
    }

    [Fact]
    public void Recording_DisablesEditorsAndSubmit()
    {
        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Text, Value = "value" };

        var cut = RenderPanel(CreateParameter(CreateSetCommand(input)), isReadOnly: true);

        Assert.True(cut.FindComponent<FluentTextInput>().Instance.Disabled);
        Assert.All(cut.FindComponents<FluentButton>(), button => Assert.True(button.Instance.Disabled));
    }

    [Fact]
    public async Task FileInputWithoutInteraction_RunsSetCommand()
    {
        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.File, Required = true };
        var command = CreateSetCommand(input);
        var parameter = CreateParameter(command);
        CommandViewModel? executed = null;

        var cut = RenderPanel(parameter, isReadOnly: false, (_, c) =>
        {
            executed = c;
            return Task.CompletedTask;
        });

        Assert.Empty(cut.FindComponents<FluentInputFile>());
        await cut.InvokeAsync(() => cut.FindComponents<FluentButton>().Single(b => b.Instance.IconStart is not null && b.Markup.Contains("Set", StringComparison.Ordinal) && !b.Markup.Contains("parameter values", StringComparison.Ordinal)).Instance.OnClick.InvokeAsync());

        Assert.Same(command, executed);
    }

    [Fact]
    public async Task InteractionInputWithUpdateStateOnChange_SendsInProgressUpdate()
    {
        var commandInput = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Text };
        var parameter = CreateParameter(CreateSetCommand(commandInput));
        var cut = RenderPanel(parameter, isReadOnly: false);
        var interactionInput = new InteractionInput { Name = parameter.Name, Label = "Value", InputType = InputType.Text, UpdateStateOnChange = true, RelatedResourceName = parameter.Name };
        var updates = new List<bool>();
        var interaction = new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            InputsDialog = new InteractionInputsDialog { InputItems = { interactionInput } }
        };

        await cut.InvokeAsync(() => Assert.True(Services.GetRequiredService<ParameterInteractionState>().TrySetCurrent(new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = string.Empty,
            OnSubmitCallback = (_, update) =>
            {
                updates.Add(update);

                // The AppHost can be gone by the time an input changes. That must not break the page.
                return Task.FromException(new InvalidOperationException("The AppHost is gone."));
            },
            DashboardClient = Services.GetRequiredService<IDashboardClient>()
        })));
        cut.WaitForAssertion(() => Assert.Equal(string.Empty, cut.FindComponent<FluentTextInput>().Instance.Value ?? string.Empty));

        await cut.InvokeAsync(() => cut.FindComponent<FluentTextInput>().Instance.ValueChanged.InvokeAsync("west"));

        Assert.Equal("west", interactionInput.Value);
        Assert.Equal([true], updates);
    }

    [Fact]
    public async Task InteractionInputNamedAfterResolvedParameter_EditsInputOfItsParameter()
    {
        // A dynamically loaded input keeps the name its generator chose, here the name of another parameter that
        // already has a value. The form must edit and submit the prompt's input for the parameter the AppHost says it's
        // for, not a copy of the resolved parameter's command input.
        var region = ModelTestHelpers.CreateResource("region", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.ValueMissing,
            commands: [CreateSetCommand(new InteractionInput { Name = "Value", Label = "Region", InputType = InputType.Text })]);
        var resolved = ModelTestHelpers.CreateResource("Value", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running,
            commands: [CreateSetCommand(new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Text })]);
        var cut = RenderPanel([region, resolved], isReadOnly: false);
        var promptInput = new InteractionInput { Name = "Value", Label = "Region", InputType = InputType.Text, Required = true, RelatedResourceName = region.Name };
        var submits = new List<bool>();

        await cut.InvokeAsync(() => Assert.True(Services.GetRequiredService<ParameterInteractionState>().TrySetCurrent(new InteractionsInputsDialogViewModel
        {
            Interaction = new WatchInteractionsResponseUpdate
            {
                InteractionId = 1,
                InputsDialog = new InteractionInputsDialog { InputItems = { promptInput } }
            },
            Message = string.Empty,
            OnSubmitCallback = (_, update) =>
            {
                submits.Add(update);
                return Task.CompletedTask;
            },
            DashboardClient = Services.GetRequiredService<IDashboardClient>()
        })));

        await cut.InvokeAsync(() => cut.FindComponent<FluentTextInput>().Instance.ValueChanged.InvokeAsync("west"));
        await cut.InvokeAsync(() => cut.FindComponents<FluentButton>().Single(b => b.Instance.Appearance == ButtonAppearance.Primary).Instance.OnClick.InvokeAsync());

        Assert.Equal("west", promptInput.Value);
        Assert.Equal([false], submits);
    }

    private static CommandViewModel CreateSetCommand(InteractionInput input) =>
        new(CommandViewModel.SetParameterCommand, CommandViewModelState.Enabled, "Set", string.Empty, string.Empty, [input], isHighlighted: false, iconName: string.Empty, iconVariant: Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular);

    private static ResourceViewModel CreateParameter(CommandViewModel command) =>
        ModelTestHelpers.CreateResource("param", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.ValueMissing, commands: [command]);

    private IRenderedComponent<MissingParametersPanel> RenderPanel(ResourceViewModel parameter, bool isReadOnly, Func<ResourceViewModel, CommandViewModel, Task>? executeCommand = null) =>
        RenderPanel([parameter], isReadOnly, executeCommand);

    private IRenderedComponent<MissingParametersPanel> RenderPanel(ResourceViewModel[] parameters, bool isReadOnly, Func<ResourceViewModel, CommandViewModel, Task>? executeCommand = null)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: parameters,
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            isReadOnly: isReadOnly));

        return Render<MissingParametersPanel>(builder =>
        {
            builder.Add(p => p.Parameters, parameters);
            builder.Add(p => p.OnExecuteCommandAsync, executeCommand ?? ((_, _) => Task.CompletedTask));
            builder.Add(p => p.OnExecuteCommandWithOptionsAsync, (_, _, _) => Task.CompletedTask);
            builder.Add(p => p.IsCommandExecuting, (_, _) => false);
        });
    }
}
