// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

using IEnvCallbackAnnotation = ICallbackResourceAnnotation<EnvironmentCallbackContext, Dictionary<string, object>>;

/// <summary>
/// Represents an annotation that provides a callback to modify the environment variables of an application.
/// </summary>
[DebuggerDisplay("{DebuggerToString(),nq}")]
public class EnvironmentCallbackAnnotation : IResourceAnnotation, IEnvCallbackAnnotation
{
    private readonly string? _name;
    private Task<Dictionary<string, object>>? _callbackTask;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentCallbackAnnotation"/> class with the specified name and callback function.
    /// </summary>
    /// <param name="name">The name of the environment variable to set.</param>
    /// <param name="callback">The callback function that returns the value to set the environment variable to.</param>
    public EnvironmentCallbackAnnotation(string name, Func<string> callback)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(callback);

        _name = name;
        Callback = (c) =>
        {
            c.EnvironmentVariables[name] = callback();
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentCallbackAnnotation"/> class with the specified callback action.
    /// </summary>
    /// <param name="callback">The callback action to be executed.</param>
    public EnvironmentCallbackAnnotation(Action<Dictionary<string, object>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Callback = (c) =>
        {
            callback(c.EnvironmentVariables);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentCallbackAnnotation"/> class with the specified callback.
    /// </summary>
    /// <param name="callback">The callback to be invoked.</param>
    public EnvironmentCallbackAnnotation(Action<EnvironmentCallbackContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Callback = c =>
        {
            callback(c);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentCallbackAnnotation"/> class with the specified callback.
    /// </summary>
    /// <param name="callback">The callback to be invoked.</param>
    public EnvironmentCallbackAnnotation(Func<EnvironmentCallbackContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Callback = callback;
    }

    /// <summary>
    /// Gets or sets the callback action to be executed when the environment is being built.
    /// </summary>
    public Func<EnvironmentCallbackContext, Task> Callback { get; private set; }

    internal IEnvCallbackAnnotation AsCallbackAnnotation() => this;

    Task<Dictionary<string, object>> IEnvCallbackAnnotation.EvaluateOnceAsync(EnvironmentCallbackContext context)
    {
        lock(_lock)
        {
            if (_callbackTask is null)
            {
                _callbackTask = ExecuteCallbackAsync(context);
            }
            return _callbackTask;
        }
    }

    void IEnvCallbackAnnotation.ForgetCachedResult()
    {
        lock(_lock)
        {
            _callbackTask = null;
        }
    }

    private async Task<Dictionary<string, object>> ExecuteCallbackAsync(EnvironmentCallbackContext context)
    {
        await Callback(context).ConfigureAwait(false);
        var result = new Dictionary<string, object>(context.EnvironmentVariables);
        return result;
    }

    private string DebuggerToString()
    {
        var text = $@"Type = {GetType().Name}";
        if (_name != null)
        {
            text += $@", Name = ""{_name}""";
        }
        return text;
    }
}
