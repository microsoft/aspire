// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.Resources;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a parameter resource.
/// </summary>
public class ParameterResource : Resource, IExpressionValue
{
    private readonly Lazy<string> _lazyValue;
    private readonly Func<ParameterDefault?, string> _valueGetter;
    private string? _configurationKey;

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
        _lazyValue = new Lazy<string>(() => _valueGetter(Default));
        Secret = secret;
    }

    /// <summary>
    /// Gets the value of the parameter.
    /// </summary>
    /// <remarks>
    /// This property is obsolete. Use <see cref="GetValueAsync(CancellationToken)"/> for async access or pass the <see cref="ParameterResource"/> directly to methods that accept it (e.g., environment variables).
    /// </remarks>
    [Obsolete("Use GetValueAsync for async access or pass the ParameterResource directly to methods that accept it (e.g., environment variables).")]
    public string Value => GetValueAsync(default).AsTask().GetAwaiter().GetResult()!;

    internal string ValueInternal
    {
        get
        {
            // If the WaitForValueTcs has a set value then prefer it.
            if (WaitForValueTcs?.Task is { IsCompleted: true } valueTask)
            {
                return valueTask.Result;
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

    /// <summary>
    /// A task completion source that can be used to wait for the value of the parameter to be set.
    /// </summary>
    internal TaskCompletionSource<string>? WaitForValueTcs { get; set; }

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

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    /// <summary>
    /// The prefix added to the input name when creating an interaction input from a parameter resource.
    /// This allows the CLI to distinguish parameter inputs from other pipeline inputs.
    /// </summary>
    internal const string InputNamePrefix = "param-";

    internal InteractionInput CreateInput()
    {
        if (this.TryGetLastAnnotation<InputGeneratorAnnotation>(out var annotation))
        {
            // If the annotation is present, use it to create the input.
            return annotation.InputGenerator(this);
        }

        var input = new InteractionInput
        {
            Name = $"{InputNamePrefix}{Name}",
            InputType = Secret ? InputType.SecretText : InputType.Text,
            Label = Name,
            Description = Description,
            EnableDescriptionMarkdown = EnableDescriptionMarkdown,
            Placeholder = string.Format(CultureInfo.CurrentCulture, InteractionStrings.ParametersInputsParameterPlaceholder, Name)
        };
        return input;
    }
#pragma warning restore ASPIREINTERACTION001
}
