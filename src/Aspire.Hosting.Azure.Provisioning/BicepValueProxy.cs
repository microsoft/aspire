// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Provisioning;

/// <summary>
/// Represents a literal, resource reference, or expression-backed Azure Provisioning value.
/// </summary>
[AspireExport]
public sealed class BicepValueProxy
{
    private readonly IBicepValue _value;
    private readonly Type _valueType;
    private readonly bool _forcedSecure;

    private BicepValueProxy(IBicepValue value, Type valueType, bool forcedSecure)
    {
        _value = value;
        _valueType = valueType;
        _forcedSecure = forcedSecure;
    }

    /// <summary>
    /// Gets whether the value is unset, literal, or expression-backed.
    /// </summary>
    [AspireExport]
    internal BicepValueKind Kind => _value.Kind;

    /// <summary>
    /// Gets whether the value contains secure data.
    /// </summary>
    [AspireExport]
    internal bool IsSecure => _forcedSecure || _value.IsSecure;

    /// <summary>
    /// Creates a proxy for generated provisioning integration code.
    /// </summary>
    /// <typeparam name="T">The literal type represented by the Azure Provisioning value.</typeparam>
    /// <param name="value">The Azure Provisioning value to wrap.</param>
    /// <returns>A proxy that preserves the value's literal, expression, reference, and security metadata.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [AspireExportIgnore(Reason = "Used by generated provisioning proxy code.")]
    public static BicepValueProxy Create<T>(BicepValue<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new(value, typeof(T), forcedSecure: false);
    }

    internal static BicepValueProxy Create<T>(BicepValue<T> value, bool isSecure)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new(value, typeof(T), isSecure);
    }

    internal static BicepValueProxy Create(IBicepValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new(value, value.LiteralValue?.GetType() ?? typeof(object), forcedSecure: false);
    }

    /// <summary>
    /// Assigns this value to a generated Azure Provisioning property.
    /// </summary>
    /// <typeparam name="T">The literal type accepted by the target property.</typeparam>
    /// <param name="target">The target Azure Provisioning value.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [AspireExportIgnore(Reason = "Used by generated provisioning proxy code.")]
    public void AssignTo<T>(BicepValue<T> target)
    {
        ArgumentNullException.ThrowIfNull(target);

        EnsureLiteralType<T>();
        if (_value.Kind == BicepValueKind.Literal)
        {
            var literalValue = new BicepValue<T>((T)_value.LiteralValue!);
            target.Assign(literalValue);

            if (IsSecure && !((IBicepValue)target).IsSecure)
            {
                // Azure Provisioning only copies the typed literal when the source is a BicepValue<T>,
                // but it only propagates secure metadata through IBicepValue.Assign. Apply the typed
                // literal first, then re-apply the secure flag without disturbing the assigned value.
                ((IBicepValue)target).Assign(new SecureBicepValue(literalValue));
            }

            return;
        }

        ((IBicepValue)target).Assign(GetAssignableValue());
    }

    internal void AssignTo(BicepValue<object> target, Type literalType)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(literalType);

        EnsureLiteralType(literalType);
        AssignTo(target);
    }

    /// <summary>
    /// Converts a literal or proxy value to the requested Azure Provisioning value type.
    /// </summary>
    /// <typeparam name="T">The expected literal type.</typeparam>
    /// <param name="value">A literal value or <see cref="BicepValueProxy"/>.</param>
    /// <returns>An Azure Provisioning value that preserves expression and reference metadata.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [AspireExportIgnore(Reason = "Used by generated provisioning proxy code.")]
    public static BicepValue<T> Convert<T>(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value is BicepValueProxy proxy)
        {
            proxy.EnsureLiteralType<T>();

            var converted = new BicepValue<T>((T)default!);
            proxy.AssignTo(converted);
            return converted;
        }

        if (value is T literal)
        {
            return literal;
        }

        throw new ArgumentException($"Expected a {typeof(T).Name} literal or {nameof(BicepValueProxy)}.", nameof(value));
    }

    internal static BicepValueProxy CreateExpression(BicepExpression expression, bool isSecure)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return new(new BicepValue<object>(expression), typeof(object), isSecure);
    }

    internal BicepExpression ToBicepExpression()
    {
        return _value.ToBicepExpression();
    }

    internal BicepValue<object> ToObjectBicepValue()
    {
        return new(ToBicepExpression());
    }

    private void EnsureLiteralType<T>()
    {
        EnsureLiteralType(typeof(T));
    }

    private void EnsureLiteralType(Type targetType)
    {
        if (_value.Kind != BicepValueKind.Literal)
        {
            return;
        }

        if (targetType.IsAssignableFrom(_valueType))
        {
            return;
        }

        var sourceUnderlyingType = Nullable.GetUnderlyingType(_valueType) ?? _valueType;
        var targetUnderlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (sourceUnderlyingType != targetUnderlyingType)
        {
            throw new ArgumentException(
                $"A literal of type {_valueType.Name} cannot be assigned to a BicepValue<{targetType.Name}>.");
        }
    }

    private IBicepValue GetAssignableValue()
    {
        // Azure Provisioning copies secure metadata from the source IBicepValue during assignment.
        // Composing expressions creates a new SDK value that no longer carries that metadata, so
        // expose the propagated state through an adapter without changing the emitted expression.
        return IsSecure && !_value.IsSecure
            ? new SecureBicepValue(_value)
            : _value;
    }

    private sealed class SecureBicepValue(IBicepValue inner) : IBicepValue
    {
        public BicepValueKind Kind => inner.Kind;

        public BicepExpression? Expression
        {
            get => inner.Expression;
            set => inner.Expression = value;
        }

        public object? LiteralValue => inner.LiteralValue;

        public BicepValueReference? Self
        {
            get => inner.Self;
            set => inner.Self = value;
        }

        public BicepValueReference? Source => inner.Source;

        public bool IsOutput => inner.IsOutput;

        public bool IsRequired => inner.IsRequired;

        public bool IsSecure => true;

        public bool IsEmpty => inner.IsEmpty;

        public void Assign(IBicepValue source)
        {
            inner.Assign(source);
        }

        public BicepExpression Compile()
        {
            return inner.Compile();
        }

        public void SetReadOnly()
        {
            inner.SetReadOnly();
        }
    }
}
