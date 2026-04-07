// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.ApplicationModel;

internal class ExpressionResolver(CancellationToken cancellationToken)
{
    async Task<ResolvedValue> EvalExpressionAsync(ReferenceExpression expr, ValueProviderContext context)
    {
        if (expr.IsConditional)
        {
            var conditionResult = await ResolveInternalAsync(expr.Condition!, context).ConfigureAwait(false);
            var branch = string.Equals(conditionResult.Value, expr.MatchValue, StringComparison.OrdinalIgnoreCase) ? expr.WhenTrue! : expr.WhenFalse!;
            return await EvalExpressionAsync(branch, context).ConfigureAwait(false);
        }

        // This logic is similar to ReferenceExpression.GetValueAsync, except that we recurse on
        // our own resolver method
        var args = new object?[expr.ValueProviders.Count];
        var isSensitive = false;

        for (var i = 0; i < expr.ValueProviders.Count; i++)
        {
            var result = await ResolveInternalAsync(expr.ValueProviders[i], context).ConfigureAwait(false);
            args[i] = result?.Value;

            // Apply string format if needed
            if (expr.StringFormats[i] is { } stringFormat && args[i] is string s)
            {
                args[i] = FormattingHelpers.FormatValue(s, stringFormat);
            }

            if (result?.IsSensitive is true)
            {
                isSensitive = true;
            }
        }

        // Identically to ReferenceExpression.GetValueAsync, we return null if the format is empty
        var value = expr.Format.Length == 0 ? null : string.Format(CultureInfo.InvariantCulture, expr.Format, args);

        return new ResolvedValue(value, isSensitive);
    }

    async Task<ResolvedValue> EvalValueProvider(IValueProvider vp, ValueProviderContext context)
    {
        var value = await vp.GetValueAsync(context, cancellationToken).ConfigureAwait(false);
        if (vp is ParameterResource pr)
        {
            return new ResolvedValue(value, pr.Secret);
        }
        return new ResolvedValue(value, false);
    }

    async Task<ResolvedValue> ResolveConnectionStringReferenceAsync(ConnectionStringReference cs, ValueProviderContext context)
    {
        // We are substituting our own logic for ConnectionStringReference's GetValueAsync.
        // However, ConnectionStringReference#GetValueAsync will throw if the connection string is not optional but is not present.
        // so we need to do the same here.
        var value = await ResolveInternalAsync(cs.Resource.ConnectionStringExpression, context).ConfigureAwait(false);

        // Throw if the connection string is required but not present
        if (string.IsNullOrEmpty(value.Value) && !cs.Optional)
        {
            cs.ThrowConnectionStringUnavailableException();
        }

        return value;
    }

    /// <summary>
    /// Resolve an expression. When it is being used from inside a container, endpoints may be evaluated (either in a container-to-container or container-to-exe communication).
    /// </summary>
    async ValueTask<ResolvedValue> ResolveInternalAsync(object? value, ValueProviderContext context)
    {
        return value switch
        {
            ConnectionStringReference cs => await ResolveConnectionStringReferenceAsync(cs, context).ConfigureAwait(false),
            IResourceWithConnectionString cs and not ConnectionStringParameterResource => await ResolveInternalAsync(cs.ConnectionStringExpression, context).ConfigureAwait(false),
            ReferenceExpression ex => await EvalExpressionAsync(ex, context).ConfigureAwait(false),
            IValueProvider vp => await EvalValueProvider(vp, context).ConfigureAwait(false),
            _ => throw new NotImplementedException()
        };
    }

    internal static async ValueTask<ResolvedValue> ResolveAsync(IValueProvider valueProvider, ValueProviderContext context, CancellationToken cancellationToken)
    {
        var resolver = new ExpressionResolver(cancellationToken);
        return await resolver.ResolveInternalAsync(valueProvider, context).ConfigureAwait(false);
    }
}

internal record ResolvedValue(string? Value, bool IsSensitive);
