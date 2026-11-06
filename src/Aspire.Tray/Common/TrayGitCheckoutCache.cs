// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;

namespace Aspire.Tray;

/// <summary>Refreshes optional checkout metadata without blocking AppHost actions or publication.</summary>
internal sealed class TrayGitCheckoutCache : IAsyncDisposable
{
    internal static TimeSpan RefreshInterval => TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, TrayGitCheckout?> _read;
    private readonly Dictionary<string, TrayGitCheckout?> _entries = new(TrayAppHostPath.Comparer);
    private readonly HashSet<string> _reads = new(TrayAppHostPath.Comparer);
    private HashSet<string> _paths = new(TrayAppHostPath.Comparer);
    private readonly Channel<byte> _refresh = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private Task? _invalidator;
    private Task? _disposeTask;
    private bool _disposed;

    public TrayGitCheckoutCache(TimeProvider timeProvider) : this(timeProvider, TrayGitCheckoutReader.Read)
    {
    }

    internal TrayGitCheckoutCache(TimeProvider timeProvider, Func<string, TrayGitCheckout?> read)
    {
        _timeProvider = timeProvider;
        _read = read;
    }

    // Invoked outside the cache lock: consumers may publish state and read the cache again.
    public event Action? Changed;

    public TrayGitCheckout? Get(string appHostPath)
    {
        var path = TrayAppHostPath.Normalize(appHostPath);
        lock (_gate)
        {
            return _entries.GetValueOrDefault(path);
        }
    }

    public void SetPaths(IEnumerable<string> appHostPaths)
    {
        var paths = appHostPaths.Select(TrayAppHostPath.Normalize).ToHashSet(TrayAppHostPath.Comparer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_paths.SetEquals(paths))
            {
                return;
            }
            _paths = paths;
            // Removal invalidates metadata immediately, even if the path is re-added
            // before the worker runs. Keep in-flight reads reserved until they finish.
            foreach (var path in _entries.Keys.Where(path => !paths.Contains(path)).ToArray())
            {
                _entries.Remove(path);
            }
            _refresh.Writer.TryWrite(0);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
            {
                throw new InvalidOperationException("The checkout cache is already started.");
            }
            _worker = Task.Run(RefreshAsync);
            _invalidator = InvalidateAsync();
        }
    }

    private async Task InvalidateAsync()
    {
        // Ref changes need not produce discovery updates, especially for offline entries.
        using var timer = new PeriodicTimer(RefreshInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                _refresh.Writer.TryWrite(0);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync()
    {
        var cancellationToken = _shutdown.Token;
        try
        {
            while (await _refresh.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_refresh.Reader.TryRead(out _))
                {
                }
                string[] paths;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    // One in-flight read per path, even across removal and re-addition.
                    // Refresh ticks cannot accumulate duplicate IO against a stalled share.
                    paths = _paths.Where(_reads.Add).ToArray();
                }
                foreach (var path in paths)
                {
                    // Publish independently. A stalled path cannot hold up other checkouts or
                    // disposal; reads may outlive shutdown but cannot update a disposed cache.
                    _ = Task.Run(() => RefreshPath(path, cancellationToken));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RefreshPath(string path, CancellationToken cancellationToken)
    {
        TrayGitCheckout? checkout;
        try
        {
            checkout = cancellationToken.IsCancellationRequested ? null : _read(path);
        }
        catch (Exception ex)
        {
            // Optional enrichment must not terminate discovery or expose local paths.
            Console.Error.WriteLine($"Git metadata refresh failed ({ex.GetType().Name}).");
            checkout = null;
        }
        lock (_gate)
        {
            _reads.Remove(path);
            if (_disposed || !_paths.Contains(path) || _entries.GetValueOrDefault(path) == checkout)
            {
                return;
            }
            _entries[path] = checkout;
        }
        Changed?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_worker ?? Task.CompletedTask, _invalidator ?? Task.CompletedTask).ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
