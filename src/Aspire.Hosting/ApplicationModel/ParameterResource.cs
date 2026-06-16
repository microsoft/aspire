// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Aspire.Hosting.Resources;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a parameter resource.
/// </summary>
[AspireExport]
public class ParameterResource : Resource, IExpressionValue
{
    private readonly Lazy<string?> _lazyValue;
    private readonly Func<ParameterDefault?, string> _valueGetter;
    private readonly object _valueTaskLock = new();
    private string? _configurationKey;
    private long _valueChangeVersion;

    /// <summary>
    /// Initializes a new instance of <see cref="ParameterResource"/>.
    /// </summary>
    /// <param name="name">The name of the parameter resource.</param>
    /// <param name="callback">The callback function to retrieve the value of the parameter.</param>
    /// <param name="secret">A flag indicating whether the parameter is secret.</param>
    public ParameterResource(string name, Func<ParameterDefault?, string> callback, bool secret = false) : base(name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(callback);

        _valueGetter = callback;
        _lazyValue = new Lazy<string?>(GetValue);
        Secret = secret;
    }

    private string? GetValue()
    {
        try
        {
            return _valueGetter(Default);
        }
        catch (MissingParameterValueException) when (!Required)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the value of the parameter.
    /// </summary>
    /// <remarks>
    /// This property is obsolete. Use <see cref="GetValueAsync(CancellationToken)"/> for async access or pass the <see cref="ParameterResource"/> directly to methods that accept it (e.g., environment variables).
    /// </remarks>
    [Obsolete("Use GetValueAsync for async access or pass the ParameterResource directly to methods that accept it (e.g., environment variables).")]
    public string Value => GetValueAsync(default).AsTask().GetAwaiter().GetResult() ?? string.Empty;

    internal string? ValueInternal
    {
        get
        {
            // If the WaitForValueTcs has a set value then prefer it.
            var waitForValueTcs = WaitForValueTcs;
            if (waitForValueTcs?.Task is { IsCompleted: true } valueTask)
            {
                return valueTask.GetAwaiter().GetResult();
            }

            return _lazyValue.Value;
        }
    }

    /// <summary>
    /// Represents how the default value of the parameter should be retrieved.
    /// </summary>
    public ParameterDefault? Default { get; set; }

    /// <summary>
    /// Gets a value indicating whether the parameter is secret.
    /// </summary>
    public bool Secret { get; }

    /// <summary>
    /// Gets or sets a value indicating whether this parameter must have a value before it can be resolved.
    /// </summary>
    /// <remarks>
    /// Optional parameters that do not have values resolve to <see langword="null"/> from <see cref="GetValueAsync(CancellationToken)"/>.
    /// The obsolete <see cref="Value"/> property returns an empty string for unset optional parameters because its signature is non-nullable.
    /// </remarks>
    public bool Required { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the parameter is a connection string.
    /// </summary>
    public bool IsConnectionString { get; set; }

    /// <summary>
    /// Gets the expression used in the manifest to reference the value of the parameter.
    /// </summary>
    public string ValueExpression => $"{{{Name}.value}}";

    /// <summary>
    /// The configuration key for this parameter. The default format is "ConnectionStrings:{Name}" if the parameter is a connection string,
    /// otherwise it is "Parameters:{Name}".
    /// </summary>
    internal string ConfigurationKey
    {
        get => _configurationKey ?? (IsConnectionString ? $"ConnectionStrings:{Name}" : $"Parameters:{Name}");
        set => _configurationKey = value;
    }

    internal TaskCompletionSource<string?>? WaitForValueTcs { get; set; }

    internal long ValueChangeVersion
    {
        get
        {
            lock (_valueTaskLock)
            {
                return _valueChangeVersion;
            }
        }
    }

    internal event Func<ParameterResource, ParameterResourceValueChangedEventArgs, CancellationToken, Task>? ValueChanged;

    /// <summary>
    /// Attempts to get the current value for this parameter without waiting for unresolved input.
    /// </summary>
    /// <param name="value">When this method returns <see langword="true"/>, contains the current parameter value.</param>
    /// <returns><see langword="true"/> if the parameter currently has a non-empty value; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This method returns <see langword="false"/> for unresolved parameters, parameters with initialization errors, and optional
    /// parameters that resolved without a value.
    /// </remarks>
    [AspireExportIgnore(Reason = "Uses out parameter which is not ATS-compatible.")]
    public bool TryGetCurrentValue([NotNullWhen(true)] out string? value)
    {
        var waitForValueTcs = WaitForValueTcs;
        if (waitForValueTcs is not null)
        {
            var valueTask = waitForValueTcs.Task;
            if (!valueTask.IsCompletedSuccessfully)
            {
                value = null;
                return false;
            }

            value = valueTask.Result;
            return !string.IsNullOrEmpty(value);
        }

        try
        {
            value = _lazyValue.Value;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception)
        {
            // Try-pattern callers use this for command state and input defaults; value resolution
            // failures mean there is no usable current value, not that the command should fail.
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Gets the current value for this parameter without waiting for unresolved input.
    /// </summary>
    /// <returns>The current parameter value, or <see langword="null"/> if the parameter does not currently have a non-empty value.</returns>
    /// <remarks>
    /// This method returns <see langword="null"/> for unresolved parameters, parameters with initialization errors, and optional
    /// parameters that resolved without a value.
    /// </remarks>
    [AspireExport("ParameterResource.tryGetCurrentValue", MethodName = "tryGetCurrentValue")]
    internal string? TryGetCurrentValue()
    {
        return TryGetCurrentValue(out var value) ? value : null;
    }

    /// <summary>
    /// Attempts to complete this parameter with the specified value.
    /// </summary>
    /// <param name="value">The value to use for this parameter.</param>
    /// <returns><see langword="true"/> if the parameter was completed with <paramref name="value"/> or a missing-value exception; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This method follows <see cref="TaskCompletionSource{TResult}.TrySetResult(TResult)"/> semantics and returns
    /// <see langword="false"/> when the parameter has already completed. Required parameters completed with <see langword="null"/>
    /// or an empty string are faulted with <see cref="MissingParameterValueException"/>. Use <see cref="SetValueAsync(string?, CancellationToken)"/>
    /// when the value should replace an existing value and update dashboard state. Use <c>ParameterProcessor.SetValueAsync</c>
    /// when the value should also be saved to deployment state.
    /// </remarks>
    public bool TrySetValue(string? value)
    {
        ParameterResourceValueChangedEventArgs? eventArgs;

        lock (_valueTaskLock)
        {
            var waitForValueTcs = GetOrCreateWaitForValueTcs();
            var missingValueException = CreateMissingValueException(value);
            var valueWasSet = missingValueException is null
                ? waitForValueTcs.TrySetResult(value)
                : waitForValueTcs.TrySetException(missingValueException);

            if (!valueWasSet)
            {
                return false;
            }

            eventArgs = missingValueException is null
                ? CreateValueChangedEventArgs(value)
                : CreateValueChangedEventArgs(missingValueException);
        }

        NotifyValueChangedInBackground(eventArgs);
        return true;
    }

    /// <summary>
    /// Attempts to complete this parameter with the specified exception.
    /// </summary>
    /// <param name="exception">The exception used to fault this parameter.</param>
    /// <returns><see langword="true"/> if the parameter was completed with <paramref name="exception"/>; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exception"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// This method follows <see cref="TaskCompletionSource{TResult}.TrySetException(Exception)"/> semantics and returns
    /// <see langword="false"/> when the parameter has already completed.
    /// </remarks>
    public bool TrySetException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        ParameterResourceValueChangedEventArgs? eventArgs;

        lock (_valueTaskLock)
        {
            var waitForValueTcs = GetOrCreateWaitForValueTcs();
            if (!waitForValueTcs.TrySetException(exception))
            {
                return false;
            }

            eventArgs = CreateValueChangedEventArgs(exception);
        }

        NotifyValueChangedInBackground(eventArgs);
        return true;
    }

    internal void EnsureValueTask()
    {
        lock (_valueTaskLock)
        {
            _ = GetOrCreateWaitForValueTcs();
        }
    }

    /// <summary>
    /// Sets or replaces the value for this parameter.
    /// </summary>
    /// <param name="value">The value to use for this parameter.</param>
    /// <param name="cancellationToken">The cancellation token to observe while applying the value.</param>
    /// <returns>A task that completes when the value has been applied and observers have processed the change.</returns>
    /// <remarks>
    /// Setting a value directly on the resource updates dashboard state after the resource has been observed by the host.
    /// It does not save the value to deployment state. Required parameters set to <see langword="null"/> or an empty string
    /// are faulted with <see cref="MissingParameterValueException"/>, while optional parameters can be cleared to <see langword="null"/>.
    /// </remarks>
    [AspireExport("ParameterResource.setValueAsync", MethodName = "setValueAsync")]
    public Task SetValueAsync(string? value = null, CancellationToken cancellationToken = default)
    {
        ParameterResourceValueChangedEventArgs eventArgs;

        lock (_valueTaskLock)
        {
            var waitForValueTcs = GetOrCreateResettableWaitForValueTcs();
            var missingValueException = CreateMissingValueException(value);
            if (missingValueException is null)
            {
                waitForValueTcs.SetResult(value);
                eventArgs = CreateValueChangedEventArgs(value);
            }
            else
            {
                waitForValueTcs.SetException(missingValueException);
                eventArgs = CreateValueChangedEventArgs(missingValueException);
            }
        }

        return NotifyValueChangedAsync(eventArgs, cancellationToken);
    }

    /// <summary>
    /// Sets or replaces the exception for this parameter.
    /// </summary>
    /// <param name="exception">The exception used to fault this parameter.</param>
    /// <param name="cancellationToken">The cancellation token to observe while applying the exception.</param>
    /// <returns>A task that completes when the exception has been applied and observers have processed the change.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exception"/> is <see langword="null"/>.</exception>
    public Task SetExceptionAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        ParameterResourceValueChangedEventArgs eventArgs;

        lock (_valueTaskLock)
        {
            GetOrCreateResettableWaitForValueTcs().SetException(exception);
            eventArgs = CreateValueChangedEventArgs(exception);
        }

        return NotifyValueChangedAsync(eventArgs, cancellationToken);
    }

    private ParameterResourceValueChangedEventArgs CreateValueChangedEventArgs(string? value) =>
        new(++_valueChangeVersion, value, exception: null);

    private ParameterResourceValueChangedEventArgs CreateValueChangedEventArgs(Exception exception) =>
        new(++_valueChangeVersion, value: null, exception);

    private void NotifyValueChangedInBackground(ParameterResourceValueChangedEventArgs eventArgs)
    {
        if (ValueChanged is null)
        {
            return;
        }

        _ = ObserveValueChangedAsync(NotifyValueChangedAsync(eventArgs, CancellationToken.None));
    }

    private static async Task ObserveValueChangedAsync(Task notificationTask)
    {
        try
        {
            await notificationTask.ConfigureAwait(false);
        }
        catch
        {
            // TrySet* is synchronous and cannot surface asynchronous observer failures.
            // Await SetValueAsync or SetExceptionAsync when callers need those failures.
        }
    }

    private async Task NotifyValueChangedAsync(ParameterResourceValueChangedEventArgs eventArgs, CancellationToken cancellationToken)
    {
        var valueChanged = ValueChanged;
        if (valueChanged is null)
        {
            return;
        }

        var handlers = valueChanged.GetInvocationList();
        List<Exception>? exceptions = null;
        for (var i = 0; i < handlers.Length; i++)
        {
            var handler = (Func<ParameterResource, ParameterResourceValueChangedEventArgs, CancellationToken, Task>)handlers[i];
            try
            {
                await handler(this, eventArgs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Notify every observer even if one throws so a single faulty subscriber cannot stop the
                // others (for example the dashboard updater) from observing the change. Failures are collected
                // and surfaced after all observers have run: awaited SetValueAsync/SetExceptionAsync callers
                // see them, while TrySet* swallows them via ObserveValueChangedAsync.
                (exceptions ??= []).Add(ex);
            }
        }

        if (exceptions is { Count: 1 })
        {
            // Preserve the original exception type and stack for the common single-observer case.
            ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        }
        else if (exceptions is { Count: > 1 })
        {
            throw new AggregateException(exceptions);
        }
    }

    private TaskCompletionSource<string?> GetOrCreateWaitForValueTcs()
    {
        return WaitForValueTcs ??= CreateWaitForValueTcs();
    }

    private TaskCompletionSource<string?> GetOrCreateResettableWaitForValueTcs()
    {
        var waitForValueTcs = GetOrCreateWaitForValueTcs();
        if (waitForValueTcs.Task.IsCompleted)
        {
            WaitForValueTcs = waitForValueTcs = CreateWaitForValueTcs();
        }

        return waitForValueTcs;
    }

    private static TaskCompletionSource<string?> CreateWaitForValueTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private MissingParameterValueException? CreateMissingValueException(string? value)
    {
        if (!Required || !string.IsNullOrEmpty(value))
        {
            return null;
        }

        return new MissingParameterValueException($"Parameter resource '{Name}' requires a value.");
    }

    /// <summary>
    /// Gets the value of the parameter asynchronously, waiting if necessary for the value to be set.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to observe while waiting for the value.</param>
    /// <returns>A task that represents the asynchronous operation, containing the value of the parameter.</returns>
    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken)
    {
        if (WaitForValueTcs is not null)
        {
            // Wait for the value to be set if the task completion source is available.
            return await WaitForValueTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // In publish mode, there's no WaitForValueTcs set.
        return ValueInternal;
    }

    /// <summary>
    /// Gets the value of the parameter asynchronously, waiting if necessary for the value to be set.
    /// </summary>
    public ValueTask<string?> GetValueAsync(ValueProviderContext _, CancellationToken cancellationToken)
    {
        // It might look like this does not provide any additional functionality over GetValueAsync,
        // but we need to ensure that types derived from ParameterResource implement IValueProvider.GetValueAsync(ValueProviderContext, CancellationToken)
        // using WaitForValueTcs, and not via an interface default implementation.
        return GetValueAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a description of the parameter resource.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the description should be rendered as Markdown.
    /// </summary>
    public bool EnableDescriptionMarkdown { get; set; }

    internal InteractionInput CreateInput(string? name = null, bool? required = null, InputLoadOptions? dynamicLoading = null)
    {
        if (this.TryGetLastAnnotation<InputGeneratorAnnotation>(out var annotation))
        {
            var generatedInput = annotation.InputGenerator(this);
            if (name is not null)
            {
                generatedInput.SetName(name);
            }

            // An explicit caller-provided value always wins. Otherwise only force the generated input to be
            // optional when the parameter itself is optional, so optional parameters are never presented as
            // required. For required parameters we preserve whatever the custom input generator chose (which
            // may be false, e.g. when the generator supplies a default value), so WithCustomInput behavior is
            // not silently changed.
            if (required is not null)
            {
                generatedInput.SetRequired(required.Value);
            }
            else if (!Required)
            {
                generatedInput.SetRequired(false);
            }

            if (dynamicLoading is not null)
            {
                generatedInput.SetDynamicLoading(dynamicLoading);
            }

            return generatedInput;
        }

        var input = new InteractionInput
        {
            Name = name ?? Name,
            InputType = Secret ? InputType.SecretText : InputType.Text,
            Label = Name,
            Description = Description,
            EnableDescriptionMarkdown = EnableDescriptionMarkdown,
            Required = required ?? Required,
            DynamicLoading = dynamicLoading,
            Placeholder = string.Format(CultureInfo.CurrentCulture, InteractionStrings.ParametersInputsParameterPlaceholder, Name)
        };
        return input;
    }
}

internal sealed class ParameterResourceValueChangedEventArgs(long version, string? value, Exception? exception)
{
    public long Version { get; } = version;

    public string? Value { get; } = value;

    public Exception? Exception { get; } = exception;
}
