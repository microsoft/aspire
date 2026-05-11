// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Hex1b.Input;
using Hex1b.Theming;
using Hex1b.Widgets;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tui;

/// <summary>
/// TUI shell for <c>aspire terminal attach</c>. Hosts an embedded
/// <see cref="Hex1bTerminal"/> wired to the resource's HMP v1 consumer
/// UDS endpoint and overlays an InfoBar with role/peer/dimension
/// information plus tmux-style chord hotkeys for taking control or
/// detaching.
/// </summary>
/// <remarks>
/// <para>
/// Lifted from Hex1b 0.147.0's <c>WebMuxerDemo.Cli.CliViewerApp</c> and
/// adapted for Aspire conventions (ILogger plumbing for diagnostics, an
/// explicit <c>viewerOnly</c> flag that controls the initial role on
/// connect, and a different InfoBar label set).
/// </para>
/// <para>
/// Renders three modes:
/// <list type="bullet">
/// <item>
/// <c>primary</c> — we hold the role; embed the inner terminal full-
/// screen because the producer's PTY tracks our host dims.
/// </item>
/// <item>
/// <c>viewer-fit</c> — host >= producer dims; embed the inner terminal
/// so the user can see what the primary is doing (read-only).
/// </item>
/// <item>
/// <c>viewer-too-small</c> — host &lt; producer dims; show a centered
/// "doesn't fit" panel offering to take control.
/// </item>
/// </list>
/// Hotkeys use a tmux-style chord prefix (<c>Ctrl+B</c>) to avoid
/// clashing with normal input forwarded to the embedded terminal in
/// primary mode (e.g., Ctrl+C must reach the workload as SIGINT).
/// </para>
/// <para>
/// This class is the textbook "easy path" consumer of Hex1b's HMP1
/// builder extensions: it never types <c>Hmp1WorkloadAdapter</c>. The
/// embedded terminal is constructed via
/// <see cref="Hmp1BuilderExtensions.WithHmp1UdsClient(Hex1bTerminalBuilder, string, Action{Hmp1ClientOptions}?)"/>
/// and the <see cref="IHmp1ConnectionHandle"/> is captured in the
/// <see cref="Hmp1ClientOptions.OnConnected"/> callback.
/// </para>
/// </remarks>
internal sealed class TerminalViewerApp
{
    // Panel background colour matching the dashboard's terminal CSS
    // --aspire-term-panel variable (#161b22). When the producer's grid
    // is smaller than the host TTY, the surrounding framing area is
    // filled with this colour so the terminal's edges are visible
    // against a contrasting backdrop — same visual idiom as the
    // dashboard's terminal card.
    private static readonly Hex1bColor s_panelColor = Hex1bColor.FromRgb(0x16, 0x1b, 0x22);

    // Terminal background, matching the dashboard's --aspire-term-bg
    // CSS variable (#0d1117). Applied to TerminalWidget so cells with
    // default-bg own their surface; without it the surrounding
    // PanelColor bleeds through every blank cell, making the terminal
    // indistinguishable from its frame.
    private static readonly Hex1bColor s_terminalBackground = Hex1bColor.FromRgb(0x0d, 0x11, 0x17);

    private readonly string _socketPath;
    private readonly string _sessionLabel;
    private readonly string _displayName;
    private readonly bool _viewerOnly;
    private readonly ILogger _logger;

    // Captured in OnConnected once the HMP1 handshake completes. Until
    // then the app renders a "Connecting…" placeholder. Subsequent
    // events / hotkey actions guard on null too, so a mid-session
    // disconnect doesn't NRE.
    private IHmp1ConnectionHandle? _connection;

    private Hex1bApp? _app;
    private Hex1bTerminal? _embedded;
    private TerminalWidgetHandle? _handle;
    private CancellationTokenSource? _embeddedCts;

    // Cancels the outer Hex1bApp when the embedded HMP1 transport
    // closes (clean disconnect or fault). Without this the outer app
    // would sit in its read loop until the user hits Ctrl+B D.
    private CancellationTokenSource? _outerCts;

    // Captured if the embedded terminal's RunAsync faults during
    // handshake (typical: connection refused, handshake timeout). We
    // surface it from RunAsync so the caller can print a clean error
    // message instead of leaving the exception unobserved.
    private Exception? _embeddedFault;

    // Locally-tracked inner terminal dimensions. Hex1bTerminal doesn't
    // expose its current grid size, so we track it here. Updated
    // whenever we resize the inner terminal in response to RoleChanged
    // or RemoteResized.
    private int _innerWidth;
    private int _innerHeight;

    // Last host TTY dims we broadcast to the producer while we held
    // primary. Used to detect host SIGWINCH (Windows Terminal resize,
    // tmux pane resize, etc.) and re-broadcast the new dims so the
    // producer's PTY follows. -1 means "no broadcast in flight"; reset
    // whenever we lose the role.
    private int _lastBroadcastWidth = -1;
    private int _lastBroadcastHeight = -1;

    // Single-flight gate so SIGWINCH bursts (typical from a mouse
    // drag-resize) collapse to one in-flight RequestPrimaryAsync at a
    // time. We always remember the most recent target and re-broadcast
    // on the next render if the target moved while we were waiting.
    private int _resizeInFlight; // 0 = idle, 1 = a request is in flight

    public TerminalViewerApp(string socketPath, string sessionLabel, string displayName, bool viewerOnly, ILogger logger)
    {
        _socketPath = socketPath;
        _sessionLabel = sessionLabel;
        _displayName = displayName;
        _viewerOnly = viewerOnly;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // Embedded inner terminal that consumes the HMP1 byte stream.
        // We use the easy-path WithHmp1UdsClient builder extension; the
        // workload adapter is constructed internally and wired up to
        // the terminal's pump. Initial dimensions are an arbitrary
        // opener (80x24) — the embedded terminal supports dynamic
        // Resize() at runtime, so OnConnected snaps it to the
        // producer's actual grid the moment the handshake completes.
        _embedded = Hex1bTerminal.CreateBuilder()
            .WithDimensions(80, 24)
            .WithHmp1UdsClient(_socketPath, opts =>
            {
                opts.DisplayName = _displayName;
                opts.DefaultRole = _viewerOnly ? Hmp1Role.Secondary : Hmp1Role.Primary;

                opts.OnConnected = async (e, ct) =>
                {
                    _connection = e.Connection;
                    EnsureInnerSize(e.Width, e.Height);
                    _logger.LogDebug(
                        "Multi-head Connected: peerId={PeerId} primary={PrimaryPeerId} dims={Width}x{Height} peers={PeerCount}",
                        e.PeerId, e.PrimaryPeerId, e.Width, e.Height, e.Peers.Count);

                    // Backwards-compatible single-head behaviour: when
                    // the user did NOT pass --viewer, immediately
                    // request primary at the host TTY dims so the
                    // producer's PTY snaps to our terminal. Skipped in
                    // viewer mode — the user can always promote later
                    // via Ctrl+B T from the InfoBar.
                    if (!_viewerOnly)
                    {
                        var (cols, rows) = TryGetLocalDimensions();
                        try
                        {
                            await e.Connection.RequestPrimaryAsync(cols, rows, ct).ConfigureAwait(false);
                            _lastBroadcastWidth = cols;
                            _lastBroadcastHeight = rows;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "Multi-head RequestPrimary failed; remaining as secondary.");
                        }
                    }

                    _app?.Invalidate();
                };
                opts.OnRoleChanged = (e, _) => { OnRoleChanged(e); return Task.CompletedTask; };
                opts.OnRemoteResized = (e, _) => { OnRemoteResized(e); return Task.CompletedTask; };
                opts.OnPeerJoined = (e, _) =>
                {
                    _logger.LogDebug("Multi-head PeerJoined: peerId={PeerId} displayName={DisplayName}", e.PeerId, e.DisplayName);
                    _app?.Invalidate();
                    return Task.CompletedTask;
                };
                opts.OnPeerLeft = (e, _) =>
                {
                    _logger.LogDebug("Multi-head PeerLeft: peerId={PeerId}", e.PeerId);
                    _app?.Invalidate();
                    return Task.CompletedTask;
                };
                opts.OnDisconnected = _ => { OnDisconnected(); return Task.CompletedTask; };
            })
            .WithScrollback()
            .WithTerminalWidget(out var handle)
            .Build();
        _handle = handle;

        _embeddedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var embeddedTask = _embedded.RunAsync(_embeddedCts.Token);

        // Observe the embedded terminal for faults so handshake
        // failures (socket connect refused, ClientHello write failure,
        // malformed server Hello) cancel the outer app and bubble out
        // via _embeddedFault rather than disappearing as an unobserved
        // task exception. Without this observer, a connection failure
        // would strand the user inside the alt-screen TUI with no
        // producer bytes ever arriving.
        _ = embeddedTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _embeddedFault = t.Exception?.GetBaseException();
            }
            try { _outerCts?.Cancel(); } catch { /* ignore */ }
        }, TaskScheduler.Default);

        _outerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await using var outer = Hex1bTerminal.CreateBuilder()
                .WithMouse()
                .WithHex1bApp((app, _) =>
                {
                    _app = app;
                    return ctx => Render(ctx);
                })
                .Build();

            try
            {
                await outer.RunAsync(_outerCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_outerCts.IsCancellationRequested)
            {
                // Triggered by the embedded-task observer when the
                // workload disconnects or faults, or by the caller's
                // cancellation token. Swallow here; the post-finally
                // rethrow surfaces _embeddedFault if any, and the
                // top-level catch in TerminalAttachCommand handles
                // user-cancellation cleanly.
                _logger.LogDebug(
                    "Outer Hex1bApp cancelled (embeddedFaulted={EmbeddedFaulted}, callerRequested={CallerRequested}).",
                    _embeddedFault is not null,
                    cancellationToken.IsCancellationRequested);
            }
        }
        finally
        {
            try
            {
                if (_embeddedCts is not null)
                {
                    await _embeddedCts.CancelAsync().ConfigureAwait(false);
                    _embeddedCts.Dispose();
                }
            }
            catch (Exception ex)
            {
                // Cancel/Dispose on a CTS shouldn't normally throw, but a
                // concurrent dispose from another teardown path could
                // surface ObjectDisposedException. Log so we can spot
                // teardown ordering bugs without breaking the outer flow.
                _logger.LogDebug(ex, "Embedded CTS teardown failed ({ExceptionType}).", ex.GetType().FullName);
            }

            try
            {
                if (_embedded is not null)
                {
                    var disposeTask = _embedded.DisposeAsync().AsTask();
                    var timeout = Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                    await Task.WhenAny(disposeTask, timeout).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Surface failures from Hex1bTerminal.DisposeAsync (transport
                // already-disposed races, pump observation faults). Logged
                // at debug because the 2s timeout above also masks "stuck
                // dispose" cases that we don't want to surface as errors.
                _logger.LogDebug(ex, "Embedded terminal dispose failed ({ExceptionType}).", ex.GetType().FullName);
            }

            _outerCts?.Dispose();
        }

        // Surface a handshake / transport failure so the caller can
        // translate it into a clean stderr message after the alt
        // screen has been restored. SocketException, IOException, and
        // OperationCanceledException are the typical shapes; the
        // top-level TerminalAttachCommand catches each.
        if (_embeddedFault is not null)
        {
            throw _embeddedFault;
        }

        return ExitCodeConstants.Success;
    }

    private void OnRoleChanged(RoleChangedEventArgs e)
    {
        _logger.LogDebug(
            "Multi-head RoleChanged: primary={PrimaryPeerId} dims={Width}x{Height} reason={Reason} previously={Previously} now={Now}",
            e.PrimaryPeerId, e.Width, e.Height, e.Reason, e.PreviouslyPrimary, e.NowPrimary);

        // RoleChange always carries the current dims. Resize the inner
        // terminal to match; this is the cleanest signal we get for
        // "producer's PTY is now N x M".
        EnsureInnerSize(e.Width, e.Height);

        // If we no longer hold the primary role, drop the broadcast
        // tracker so a future re-take starts from scratch and
        // immediately resyncs.
        if (_connection is { IsPrimary: false })
        {
            _lastBroadcastWidth = -1;
            _lastBroadcastHeight = -1;
        }

        _app?.Invalidate();
    }

    private void OnRemoteResized(RemoteResizedEventArgs e)
    {
        // Producer's PTY just changed dims (either we requested it as
        // primary or another peer is driving it). Resize the embedded
        // terminal and re-render so viewer-fit / doesn't-fit recomputes.
        EnsureInnerSize(e.Width, e.Height);
        _app?.Invalidate();
    }

    private void OnDisconnected()
    {
        // Producer hung up. Cancel the outer app so RunAsync returns
        // cleanly; without this the user would have to hit Ctrl+B D
        // even though there's nothing left to view.
        try { _outerCts?.Cancel(); } catch { /* ignore */ }
    }

    private void EnsureInnerSize(int width, int height)
    {
        var w = Math.Max(1, width);
        var h = Math.Max(1, height);
        if (_innerWidth == w && _innerHeight == h)
        {
            return;
        }
        _embedded?.Resize(w, h);
        _innerWidth = w;
        _innerHeight = h;
    }

    private Hex1bWidget Render<TParent>(WidgetContext<TParent> ctx)
        where TParent : Hex1bWidget
    {
        // Until the handshake completes we have no dims and no role to
        // render. Show a placeholder; OnConnected calls Invalidate to
        // re-trigger this method as soon as the connection lands.
        if (_connection is not { } connection)
        {
            return new BackgroundPanelWidget(
                s_panelColor,
                ctx.Center(ctx.Text($"  Connecting to {_sessionLabel}…  ")).Fill());
        }

        // Available widget space ~= host TTY minus the InfoBar (1 row).
        // Console.WindowWidth / WindowHeight reflect the live host TTY
        // size including SIGWINCH; the outer Hex1bTerminal is bound to
        // those dims when running interactively.
        var availW = Math.Max(1, Console.WindowWidth);
        var availH = Math.Max(1, Console.WindowHeight - 1);

        var producerW = connection.RemoteWidth;
        var producerH = connection.RemoteHeight;

        var isPrimary = connection.IsPrimary;
        var fits = producerW <= availW && producerH <= availH;
        var showTerminal = isPrimary || fits;

        // While we hold the primary role, follow host SIGWINCH:
        // re-broadcast the new dims so the producer's PTY grows or
        // shrinks with the host terminal (Windows Terminal, iTerm2,
        // tmux pane, ...). Without this a host grow leaves the
        // producer pinned at the original dims and the terminal sits
        // with empty padding around it forever.
        if (isPrimary && (availW != _lastBroadcastWidth || availH != _lastBroadcastHeight))
        {
            BroadcastResize(connection, availW, availH);
        }

        Hex1bWidget body = showTerminal
            ? BuildTerminalView(ctx)
            : BuildDoesntFitView(ctx, producerW, producerH, availW, availH);

        var info = BuildInfoBar(ctx, connection, isPrimary, producerW, producerH);

        // Wrap the body+infobar in a BackgroundPanelWidget so the
        // framing area around a smaller producer grid fills with the
        // panel colour (mirrors the dashboard's terminal card). The
        // InfoBar paints its own background on top, so the visible
        // grey appears only in the empty space around the centred
        // terminal grid.
        var content = new BackgroundPanelWidget(s_panelColor, ctx.VStack(v => [body, info]));

        return content.InputBindings(bindings =>
        {
            // Detach: works in any mode.
            bindings.Ctrl().Key(Hex1bKey.B).Then().Key(Hex1bKey.D)
                .OverridesCapture()
                .Action(_ => _app?.RequestStop(), "Detach");

            // Take control: only when we're not already primary.
            if (!isPrimary)
            {
                bindings.Ctrl().Key(Hex1bKey.B).Then().Key(Hex1bKey.T)
                    .OverridesCapture()
                    .Action(async _ => await TakeControlAsync(connection, availW, availH).ConfigureAwait(false),
                        "Take Control");
            }
        });
    }

    private Hex1bWidget BuildTerminalView<TParent>(WidgetContext<TParent> ctx)
        where TParent : Hex1bWidget
    {
        if (_handle is null)
        {
            return ctx.Align(Alignment.Center, ctx.Text("(initialising terminal)")).Fill();
        }

        // Pin the Terminal to the producer's grid dims so AlignNode
        // can actually centre it. Without FixedWidth/Height,
        // TerminalNode happily claims the full bounded constraint and
        // the Align centring becomes a no-op — the grid just paints
        // at top-left with blank padding around it.
        // Apply an explicit terminal Background so cells with
        // default-bg don't inherit the surrounding PanelColor.
        return ctx.Align(
            Alignment.Center,
            ctx.Terminal(_handle)
                .Background(s_terminalBackground)
                .FixedWidth(Math.Max(1, _innerWidth))
                .FixedHeight(Math.Max(1, _innerHeight))
        ).Fill();
    }

    private static Hex1bWidget BuildDoesntFitView<TParent>(
        WidgetContext<TParent> ctx,
        int producerW, int producerH,
        int availW, int availH)
        where TParent : Hex1bWidget
    {
        // .Fill() on the Center makes VStack hand it all the remaining
        // body space so the panel can centre vertically and the
        // InfoBar is pushed to the actual bottom of the screen.
        return ctx.Center(
            ctx.Border(b =>
            [
                b.VStack(v =>
                [
                    v.Text(""),
                    v.Text($"  Producer terminal:  {producerW}\u00d7{producerH}  "),
                    v.Text($"  Your terminal:      {availW}\u00d7{availH}  "),
                    v.Text(""),
                    v.Text("  Press  Ctrl+B  T  to take control  "),
                    v.Text("  (resizes producer to your terminal)  "),
                    v.Text(""),
                    v.Text("  Press  Ctrl+B  D  to detach  "),
                    v.Text(""),
                ])
            ]).Title(" doesn't fit ")).Fill();
    }

    private Hex1bWidget BuildInfoBar<TParent>(
        WidgetContext<TParent> ctx,
        IHmp1ConnectionHandle connection,
        bool isPrimary,
        int producerW, int producerH)
        where TParent : Hex1bWidget
    {
        var role = isPrimary ? "PRIMARY" : "viewer";
        // +1 to include ourselves in the "peers" total; matches the
        // dashboard chrome's status pill semantics.
        var peers = connection.Peers.Count + 1;
        var dims = $"{producerW}\u00d7{producerH}";

        return ctx.InfoBar(s =>
        [
            s.Section("Ctrl+B T"),
            s.Section(isPrimary ? "(primary)" : "Take"),
            s.Spacer(),
            s.Section("Ctrl+B D"),
            s.Section("Detach"),
            s.Spacer(),
            s.Section(_sessionLabel),
            s.Section(role),
            s.Section($"peers:{peers}"),
            s.Section(dims),
        ]).Divider(" ");
    }

    private async Task TakeControlAsync(IHmp1ConnectionHandle connection, int availW, int availH)
    {
        try
        {
            // Request producer to resize PTY to our available widget
            // area (host TTY minus InfoBar). Producer broadcasts
            // RoleChange + implicit Resize; our RoleChanged handler
            // updates the inner terminal grid + invalidates the app.
            await connection.RequestPrimaryAsync(availW, availH, CancellationToken.None).ConfigureAwait(false);

            // Seed the SIGWINCH tracker so the render-time host-resize
            // poll doesn't immediately re-broadcast the dims we just
            // set.
            _lastBroadcastWidth = availW;
            _lastBroadcastHeight = availH;
        }
        catch (Exception ex)
        {
            // Best-effort; if the producer is gone we'll see
            // OnDisconnected shortly. Don't escalate — Hex1bApp
            // surface should never unwind a binding action with an
            // exception.
            _logger.LogDebug(ex, "Multi-head Take Control failed; remaining as secondary.");
        }
    }

    private void BroadcastResize(IHmp1ConnectionHandle connection, int width, int height)
    {
        // Record the target dims up-front so we don't loop on the next
        // Render(): if multiple SIGWINCH events fire while a request
        // is in flight, only the latest pair persists in
        // _lastBroadcastWidth/H.
        _lastBroadcastWidth = width;
        _lastBroadcastHeight = height;

        // Single-flight: bail if a broadcast is already in flight.
        // Future renders will re-detect drift if the host kept
        // resizing while the request was in flight (because the
        // in-flight call captured an older target) and trigger a
        // fresh broadcast then.
        if (Interlocked.CompareExchange(ref _resizeInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await connection.RequestPrimaryAsync(width, height, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort; producer may have gone away mid-resize.
                _logger.LogDebug(ex, "Multi-head SIGWINCH re-broadcast failed.");
            }
            finally
            {
                Volatile.Write(ref _resizeInFlight, 0);
                _app?.Invalidate();
            }
        });
    }

    private static (int Cols, int Rows) TryGetLocalDimensions()
    {
        // Prefer the live console size when available. Fall back to
        // the producer's default 80x24 if the CLI is being invoked in
        // a non-console context — in that case the request still
        // succeeds and the producer keeps its current size if both
        // dimensions match.
        try
        {
            var cols = Console.WindowWidth;
            var rows = Console.WindowHeight;
            if (cols > 0 && rows > 0)
            {
                return (cols, rows);
            }
        }
        catch (IOException)
        {
        }
        return (80, 24);
    }
}
