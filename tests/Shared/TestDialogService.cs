// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Tests.Shared;

public class TestDialogService : IDialogService
{
    private readonly Func<object, DialogParameters, Task<IDialogReference>>? _onShowDialog;

    public TestDialogService(Func<object, DialogParameters, Task<IDialogReference>>? onShowDialog = null)
    {
        _onShowDialog = onShowDialog;
    }

#pragma warning disable CS0067
    public event Action<IDialogReference, Type?, DialogParameters, object>? OnShow;
    public event Func<IDialogReference, Type?, DialogParameters, object, Task<IDialogReference>>? OnShowAsync;
    public event Action<string, DialogParameters>? OnUpdate;
    public event Func<string, DialogParameters, Task<IDialogReference?>>? OnUpdateAsync;
    public event Action<IDialogReference, DialogResult>? OnDialogCloseRequested;
#pragma warning restore CS0067

    public Task CloseAsync(IDialogReference dialog) => throw new NotImplementedException();
    public Task CloseAsync(IDialogReference dialog, DialogResult result) => throw new NotImplementedException();
    public EventCallback<DialogResult> CreateDialogCallback(object receiver, Func<DialogResult, Task> callback) => throw new NotImplementedException();
    public void ShowConfirmation(object receiver, Func<DialogResult, Task> callback, string message, string primaryText = "Yes", string secondaryText = "No", string? title = null) => throw new NotImplementedException();
    public Task<IDialogReference> ShowConfirmationAsync(object receiver, Func<DialogResult, Task> callback, string message, string primaryText = "Yes", string secondaryText = "No", string? title = null) => throw new NotImplementedException();
    public Task<IDialogReference> ShowConfirmationAsync(string message, string primaryText = "Yes", string secondaryText = "No", string? title = null) => throw new NotImplementedException();
    public void ShowDialog<TDialog, TData>(DialogParameters<TData> parameters) where TDialog : IDialogContentComponent<TData> where TData : class => throw new NotImplementedException();
    public void ShowDialog<TData>(Type dialogComponent, TData data, DialogParameters parameters) where TData : class => throw new NotImplementedException();

    public async Task<IDialogReference> ShowDialogAsync<TData>(Type dialogComponent, TData data, DialogParameters parameters) where TData : class
    {
        return await RunShowDialogCallbackAsync(dialogComponent, data, parameters).ConfigureAwait(false);
    }

    public async Task<IDialogReference> ShowDialogAsync<TDialog>(object data, DialogParameters parameters) where TDialog : IDialogContentComponent
    {
        return await RunShowDialogCallbackAsync(typeof(TDialog), data, parameters).ConfigureAwait(false);
    }

    private async Task<IDialogReference> RunShowDialogCallbackAsync(Type dialogComponent, object data, DialogParameters parameters)
    {
        if (_onShowDialog == null)
        {
            throw new InvalidOperationException("No dialog callback specified.");
        }
        var reference = await _onShowDialog.Invoke(data, parameters).ConfigureAwait(false);
        reference.Instance = new DialogInstance(dialogComponent, parameters, data, previouslyFocusedElement: null);
        return reference;
    }

    public Task<IDialogReference> ShowDialogAsync<TDialog>(DialogParameters parameters) where TDialog : IDialogContentComponent => throw new NotImplementedException();
    public Task<IDialogReference> ShowDialogAsync(RenderFragment renderFragment, DialogParameters dialogParameters) => throw new NotImplementedException();
    public Task<IDialogReference> ShowDialogAsync<TDialog, TData>(DialogParameters<TData> parameters) where TDialog : IDialogContentComponent<TData> where TData : class => throw new NotImplementedException();
    public void ShowError(string message, string? title = null, string? primaryText = null) => throw new NotImplementedException();
    public Task<IDialogReference> ShowErrorAsync(string message, string? title = null, string? primaryText = null) => throw new NotImplementedException();
    public void ShowInfo(string message, string? title = null, string? primaryText = null) => throw new NotImplementedException();
    public Task<IDialogReference> ShowInfoAsync(string message, string? title = null, string? primaryText = null) => throw new NotImplementedException();
    public void ShowMessageBox(DialogParameters<MessageBoxContent> parameters) => throw new NotImplementedException();
    public async Task<IDialogReference> ShowMessageBoxAsync(DialogParameters<MessageBoxContent> parameters)
    {
        if (_onShowDialog == null)
        {
            throw new InvalidOperationException("No dialog callback specified.");
        }
        var reference = await _onShowDialog.Invoke(parameters.Content, parameters).ConfigureAwait(false);
        // Use typeof(object) as a placeholder since this is a test mock and doesn't actually render
        reference.Instance = new DialogInstance(typeof(object), parameters, parameters.Content, previouslyFocusedElement: null);
        return reference;
    }
    public void ShowPanel<TDialog, TData>(DialogParameters<TData> parameters) where TDialog : IDialogContentComponent<TData> where TData : class => throw new NotImplementedException();
    public void ShowPanel<TData>(Type dialogComponent, DialogParameters<TData> parameters) where TData : class => throw new NotImplementedException();
    public Task<IDialogReference> ShowPanelAsync<TData>(Type dialogComponent, TData data, DialogParameters parameters) where TData : class => throw new NotImplementedException();
    public Task<IDialogReference> ShowPanelAsync<TDialog>(object data, DialogParameters parameters) where TDialog : IDialogContentComponent => throw new NotImplementedException();
    public Task<IDialogReference> ShowPanelAsync<TDialog>(DialogParameters parameters) where TDialog : IDialogContentComponent => throw new NotImplementedException();
    public Task<IDialogReference> ShowPanelAsync<TDialog, TData>(DialogParameters<TData> parameters) where TDialog : IDialogContentComponent<TData> where TData : class => throw new NotImplementedException();
    public Task<IDialogReference> ShowPanelAsync<TData>(Type dialogComponent, DialogParameters<TData> parameters) where TData : class => throw new NotImplementedException();
    public void ShowSplashScreen(object receiver, Func<DialogResult, Task> callback, DialogParameters<SplashScreenContent> parameters) => throw new NotImplementedException();
    public void ShowSplashScreen<T>(object receiver, Func<DialogResult, Task> callback, DialogParameters<SplashScreenContent> parameters) where T : IDialogContentComponent<SplashScreenContent> => throw new NotImplementedException();
    public void ShowSplashScreen(Type component, object receiver, Func<DialogResult, Task> callback, DialogParameters<SplashScreenContent> parameters) => throw new NotImplementedException();
    public Task<IDialogReference> ShowSplashScreenAsync(object receiver, Func<DialogResult, Task> callback, DialogParameters<SplashScreenContent> parameters) => throw new NotImplementedException();
    public Task<IDialogReference> ShowSplashScreenAsync(DialogParameters<SplashScreenContent> parameters) => throw new NotImplementedException();
    public Task<IDialogReference> ShowSplashScreenAsync<T>(object receiver, Func<DialogResult, Task> callback, DialogParameters<SplashScreenContent> parameters) where T : IDialogContentComponent<SplashScreenContent> => throw new NotImplementedException();
    public Task<IDialogReference> ShowSplashScreenAsync<T>(DialogParameters<SplashScreenContent> parameters) where T : IDialogContentComponent<SplashScreenContent> => throw new NotImplementedException();
    public Task<IDialogReference> ShowSplashScreenAsync(Type component, object receiver, Func<DialogResult, Task> callback, DialogParameters<SplashScreenContent> parameters) => throw new NotImplementedException();
    public Task<IDialogReference> ShowSplashScreenAsync(Type component, DialogParameters<SplashScreenContent> parameters) => throw new NotImplementedException();
    public void ShowSuccess(string message, string? title = null, string? primaryText = null) => throw new NotImplementedException();
    public Task<IDialogReference> ShowSuccessAsync(string message, string? title = null, string? primaryText = null) => throw new NotImplementedException();
    public void ShowWarning(string message, string? title = null, string? primaryText = null) => throw new NotImplementedException();
    public Task<IDialogReference> ShowWarningAsync(string message, string? title = null, string? primaryText = null) => throw new NotImplementedException();
    public void UpdateDialog<TData>(string id, DialogParameters<TData> parameters) where TData : class => throw new NotImplementedException();
    public Task<IDialogReference?> UpdateDialogAsync<TData>(string id, DialogParameters<TData> parameters) where TData : class => throw new NotImplementedException();
}
