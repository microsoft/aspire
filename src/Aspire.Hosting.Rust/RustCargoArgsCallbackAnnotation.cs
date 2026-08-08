// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Rust;

/// <summary>
/// Represents a callback annotation for cargo-level arguments.
/// </summary>
/// <param name="callback">The callback that populates cargo arguments.</param>
public sealed class RustCargoArgsCallbackAnnotation(Func<RustCargoArgsCallbackContext, Task> callback) : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RustCargoArgsCallbackAnnotation"/> class.
    /// </summary>
    /// <param name="callback">The callback action to be executed.</param>
    public RustCargoArgsCallbackAnnotation(Action<IList<string>> callback)
        : this(context =>
        {
            callback(context.Args);
            return Task.CompletedTask;
        })
    {
        ArgumentNullException.ThrowIfNull(callback);
    }

    /// <summary>
    /// Gets the callback action that is executed to populate cargo-level arguments.
    /// </summary>
    public Func<RustCargoArgsCallbackContext, Task> Callback { get; } = callback ?? throw new ArgumentNullException(nameof(callback));
}

/// <summary>
/// Represents callback context for cargo-level command-line arguments.
/// </summary>
/// <param name="args">The command-line arguments collection.</param>
/// <param name="cancellationToken">The cancellation token associated with this callback context.</param>
/// <remarks>
/// Unlike program arguments, cargo arguments are plain strings rather than <see cref="object"/>.
/// They select build behaviour (<c>--release</c>, <c>--features</c>, <c>--bin</c>) before the program
/// starts, so there is nothing for a deferred value such as an endpoint reference to resolve against;
/// those belong after the <c>--</c> separator and are added with <c>WithArgs</c>.
/// </remarks>
public sealed class RustCargoArgsCallbackContext(IList<string> args, CancellationToken cancellationToken = default)
{
    /// <summary>
    /// Gets the list of command-line arguments.
    /// </summary>
    public IList<string> Args { get; } = args ?? throw new ArgumentNullException(nameof(args));

    /// <summary>
    /// Gets the cancellation token associated with the callback context.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;
}
