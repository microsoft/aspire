// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public interface IDashboardToastService
{
    event Action? OnChange;

    event Action<string>? OnClose;

    IReadOnlyList<DashboardToast> GetToasts();

    void Show(DashboardToast toast, TimeSpan? timeout = null);

    bool Update(string id, DashboardToast toast);

    void Close(string id);
}

public sealed class DashboardToast
{
    public required string Id { get; init; }

    public required string Title { get; set; }

    public string? Details { get; set; }

    public required NotificationIntent Intent { get; set; }

    public bool IsProgress { get; set; }

    public DashboardToastAction? PrimaryAction { get; set; }

    public DashboardToastAction? SecondaryAction { get; set; }
}

public sealed class DashboardToastAction
{
    public required string Text { get; init; }

    public required Func<Task> OnClick { get; init; }
}

internal sealed class DashboardToastService : IDashboardToastService, IDisposable
{
    private const int MaxToastCount = 3;

    private readonly object _lock = new();
    private readonly List<DashboardToast> _toasts = [];
    private readonly Dictionary<string, CancellationTokenSource> _timeoutTokens = [];

    public event Action? OnChange;

    public event Action<string>? OnClose;

    public IReadOnlyList<DashboardToast> GetToasts()
    {
        lock (_lock)
        {
            return _toasts.Select(CloneToast).ToArray();
        }
    }

    public void Show(DashboardToast toast, TimeSpan? timeout = null)
    {
        string? removedId = null;
        lock (_lock)
        {
            var existingIndex = _toasts.FindIndex(t => t.Id == toast.Id);
            if (existingIndex >= 0)
            {
                _toasts.RemoveAt(existingIndex);
                CancelTimeoutCore(toast.Id);
            }

            _toasts.Add(CloneToast(toast));
            if (_toasts.Count > MaxToastCount)
            {
                removedId = _toasts[0].Id;
                _toasts.RemoveAt(0);
                CancelTimeoutCore(removedId);
            }

            if (timeout is { } value)
            {
                var cts = new CancellationTokenSource();
                _timeoutTokens.Add(toast.Id, cts);
                _ = CloseAfterTimeoutAsync(toast.Id, value, cts.Token);
            }
        }

        if (removedId is not null)
        {
            OnClose?.Invoke(removedId);
        }

        OnChange?.Invoke();
    }

    public bool Update(string id, DashboardToast toast)
    {
        var updated = false;
        lock (_lock)
        {
            var index = _toasts.FindIndex(t => t.Id == id);
            if (index >= 0)
            {
                _toasts[index] = CloneToast(toast);
                updated = true;
            }
        }

        if (updated)
        {
            OnChange?.Invoke();
        }

        return updated;
    }

    public void Close(string id)
    {
        var removed = false;
        lock (_lock)
        {
            var index = _toasts.FindIndex(t => t.Id == id);
            if (index >= 0)
            {
                _toasts.RemoveAt(index);
                CancelTimeoutCore(id);
                removed = true;
            }
        }

        if (removed)
        {
            OnClose?.Invoke(id);
            OnChange?.Invoke();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var cts in _timeoutTokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _timeoutTokens.Clear();
            _toasts.Clear();
        }
    }

    private async Task CloseAfterTimeoutAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            Close(id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CancelTimeoutCore(string id)
    {
        if (_timeoutTokens.Remove(id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private static DashboardToast CloneToast(DashboardToast toast)
    {
        return new DashboardToast
        {
            Id = toast.Id,
            Title = toast.Title,
            Details = toast.Details,
            Intent = toast.Intent,
            IsProgress = toast.IsProgress,
            PrimaryAction = toast.PrimaryAction,
            SecondaryAction = toast.SecondaryAction
        };
    }
}
