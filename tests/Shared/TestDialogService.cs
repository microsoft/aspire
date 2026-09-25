// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Collections.Concurrent;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Tests.Shared;

public sealed class TestDialogService : DialogService
{
    private static readonly ConstructorInfo s_eventArgsConstructor = typeof(DialogEventArgs)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(IDialogInstance), typeof(DialogState)])!;
    private static readonly ConstructorInfo s_instanceConstructor = typeof(DialogInstance)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(IDialogService), typeof(Type), typeof(DialogOptions)])!;

    private readonly Func<object?, DialogParameters, Task>? _onShowDialog;
    private readonly ConcurrentDictionary<string, IDialogInstance> _instances;

    public TestDialogService(Func<object?, DialogParameters, Task>? onShowDialog = null)
        : base(CreateServiceProvider(), localizer: null)
    {
        _onShowDialog = onShowDialog;

        var service = (IFluentServiceBase<IDialogInstance>)this;
        var serviceType = typeof(IFluentServiceBase<IDialogInstance>);
        serviceType.GetProperty(nameof(service.ProviderId))!.SetValue(service, "Test");
        _instances = (ConcurrentDictionary<string, IDialogInstance>)serviceType
            .GetProperty("Items", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
    }

    public IDialogInstance? LastInstance { get; private set; }

    public override async Task<DialogResult> ShowDialogAsync(Type componentType, DialogOptions options)
    {
        var instance = (IDialogInstance)s_instanceConstructor.Invoke([this, componentType, options]);
        _instances.TryAdd(instance.Id, instance);
        LastInstance = instance;
        if (_onShowDialog is not null)
        {
            instance.Options.Parameters.TryGetValue("Content", out var content);
            var parameters = (DialogParameters)instance.Options.Data!;
            await _onShowDialog(content, parameters);
        }

        var eventArgs = (DialogEventArgs)s_eventArgsConstructor.Invoke([instance, DialogState.Opening]);
        instance.Options.OnStateChange?.Invoke(eventArgs);

        return await instance.Result.ConfigureAwait(false);
    }

    private static IServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJSRuntime, TestJSRuntime>();
        services.AddSingleton(new LibraryConfiguration { UseGlobalOverlay = false });
        return services.BuildServiceProvider();
    }
}

public sealed class TestJSRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        return ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        return ValueTask.FromResult(default(TValue)!);
    }
}
