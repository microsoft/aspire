// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.Diagnostics;

internal static class ShutdownDiagnostics
{
    private static readonly AsyncLocal<CaptureState?> s_capture = new();

    internal static IDisposable Capture(Action<string> write)
    {
        var previous = s_capture.Value;
        s_capture.Value = new CaptureState(write);
        return new RestoreCapture(previous);
    }

    internal static void Mark(string stage, CancellationToken token = default)
    {
        s_capture.Value?.Write(stage, token);
    }

    internal static IDisposable? Observe(string stage, CancellationToken token)
    {
        return s_capture.Value is { } capture ? new Observation(capture, stage, token) : null;
    }

    private sealed class CaptureState(Action<string> write)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly Dictionary<CancellationToken, int> _tokens = new();

        public void Write(string stage, CancellationToken token)
        {
            lock (_tokens)
            {
                if (!_tokens.TryGetValue(token, out var identity))
                {
                    identity = _tokens.Count + 1;
                    _tokens.Add(token, identity);
                }

                write($"Shutdown causal: utc={DateTimeOffset.UtcNow:O}; elapsed={_elapsed.Elapsed}; stage={stage}; token={identity}; canCancel={token.CanBeCanceled}; cancelled={token.IsCancellationRequested}");
            }
        }
    }

    private sealed class RestoreCapture(CaptureState? previous) : IDisposable
    {
        public void Dispose() => s_capture.Value = previous;
    }

    private sealed class Observation : IDisposable
    {
        private readonly CaptureState _capture;
        private readonly string _stage;
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _registration;

        public Observation(CaptureState capture, string stage, CancellationToken token)
        {
            _capture = capture;
            _stage = stage;
            _token = token;
            capture.Write($"{stage}.enter", token);
            _registration = token.Register(() => capture.Write($"{stage}.token-cancelled", token));
        }

        public void Dispose()
        {
            _registration.Dispose();
            // Exit is a finally boundary, not a success claim; task outcomes are logged separately.
            _capture.Write($"{_stage}.exit", _token);
        }
    }
}
