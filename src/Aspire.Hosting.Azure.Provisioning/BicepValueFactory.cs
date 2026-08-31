// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Provisioning;

internal static class BicepValueFactory
{
    /// <summary>
    /// Creates a factory for composing Bicep values and expressions.
    /// </summary>
    /// <param name="infrastructure">The infrastructure being configured.</param>
    /// <returns>A factory for composing Bicep values and expressions.</returns>
    [AspireExport("bicep")]
    internal static BicepValueFactoryProxy Create(this AzureResourceInfrastructure infrastructure)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);

        return new(infrastructure);
    }
}

/// <summary>
/// Creates and composes Azure Provisioning values for generated infrastructure proxies.
/// </summary>
[AspireExport]
#pragma warning disable CA1822 // ATS uses this shared handle as the target for expression factory methods.
internal sealed class BicepValueFactoryProxy
{
    private readonly AzureResourceInfrastructure _infrastructure;

    internal BicepValueFactoryProxy(AzureResourceInfrastructure infrastructure)
    {
        _infrastructure = infrastructure;
    }

    /// <summary>
    /// Creates a string literal.
    /// </summary>
    /// <param name="value">The string value.</param>
    /// <returns>A Bicep string value.</returns>
    [AspireExport]
    internal BicepValueProxy String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(new BicepValue<string>(value));
    }

    /// <summary>
    /// Creates an integer literal.
    /// </summary>
    /// <param name="value">The integer value.</param>
    /// <returns>A Bicep integer value.</returns>
    [AspireExport]
    internal BicepValueProxy Integer(int value)
    {
        return BicepValueProxy.Create(new BicepValue<int>(value));
    }

    /// <summary>
    /// Creates a Boolean literal.
    /// </summary>
    /// <param name="value">The Boolean value.</param>
    /// <returns>A Bicep Boolean value.</returns>
    [AspireExport]
    internal BicepValueProxy Boolean(bool value)
    {
        return BicepValueProxy.Create(new BicepValue<bool>(value));
    }

    /// <summary>
    /// Creates a double-precision numeric literal.
    /// </summary>
    /// <param name="value">The numeric value.</param>
    /// <returns>A Bicep numeric value.</returns>
    [AspireExport]
    internal BicepValueProxy Double(double value)
    {
        return BicepValueProxy.Create(new BicepValue<double>(value));
    }

    /// <summary>
    /// Creates a GUID literal.
    /// </summary>
    /// <param name="value">The GUID value.</param>
    /// <returns>A Bicep GUID value.</returns>
    [AspireExport]
    internal BicepValueProxy Guid(Guid value)
    {
        return BicepValueProxy.Create(new BicepValue<Guid>(value));
    }

    /// <summary>
    /// Creates a URI literal.
    /// </summary>
    /// <param name="value">The URI value.</param>
    /// <returns>A Bicep URI value.</returns>
    [AspireExport]
    internal BicepValueProxy Uri(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(new BicepValue<Uri>(value));
    }

    /// <summary>
    /// Creates an Azure location literal.
    /// </summary>
    /// <param name="name">The Azure location name.</param>
    /// <returns>A Bicep Azure location value.</returns>
    [AspireExport]
    internal BicepValueProxy Location(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return BicepValueProxy.Create(new BicepValue<AzureLocation>(new AzureLocation(name)));
    }

    /// <summary>
    /// Creates a time-span literal.
    /// </summary>
    /// <param name="value">The time-span value.</param>
    /// <returns>A Bicep time-span value.</returns>
    [AspireExport]
    internal BicepValueProxy TimeSpan(TimeSpan value)
    {
        return BicepValueProxy.Create(new BicepValue<TimeSpan>(value));
    }

    /// <summary>
    /// Creates a Bicep parameter reference for an Aspire parameter.
    /// </summary>
    /// <param name="parameter">The Aspire parameter resource.</param>
    /// <param name="bicepIdentifier">The optional Bicep parameter identifier.</param>
    /// <returns>A Bicep parameter reference.</returns>
    [AspireExport]
    internal BicepValueProxy Parameter(
        IResourceBuilder<ParameterResource> parameter,
        string? bicepIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        return BicepValueProxy.Create<string>(parameter.AsProvisioningParameter(_infrastructure, bicepIdentifier));
    }

    /// <summary>
    /// Creates a Bicep parameter reference for an Aspire reference expression.
    /// </summary>
    /// <param name="expression">The Aspire reference expression.</param>
    /// <param name="bicepIdentifier">The optional Bicep parameter identifier.</param>
    /// <param name="isSecure">Whether the parameter contains secure data.</param>
    /// <returns>A Bicep parameter reference.</returns>
    [AspireExport]
    internal BicepValueProxy ReferenceExpression(
        ReferenceExpression expression,
        string? bicepIdentifier = null,
        bool? isSecure = null)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return BicepValueProxy.Create<string>(expression.AsProvisioningParameter(_infrastructure, bicepIdentifier, isSecure));
    }

    /// <summary>
    /// Creates a Bicep identifier expression.
    /// </summary>
    /// <param name="bicepIdentifier">The Bicep identifier.</param>
    /// <returns>A Bicep identifier expression.</returns>
    [AspireExport]
    internal BicepValueProxy Identifier(string bicepIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(bicepIdentifier);

        return BicepValueProxy.CreateExpression(new IdentifierExpression(bicepIdentifier), isSecure: false);
    }

    /// <summary>
    /// Creates a Bicep identifier expression for a provisionable resource.
    /// </summary>
    /// <param name="resource">The provisionable resource.</param>
    /// <returns>A Bicep resource identifier expression.</returns>
    [AspireExport("resourceIdentifier")]
    internal BicepValueProxy Identifier(ProvisionableResourceProxy resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return Identifier(resource.BicepIdentifier);
    }

    /// <summary>
    /// Creates a Bicep function-call expression.
    /// </summary>
    /// <param name="name">The Bicep function name.</param>
    /// <param name="args">The function arguments.</param>
    /// <returns>A Bicep function-call expression.</returns>
    [AspireExport]
    internal BicepValueProxy Function(string name, IEnumerable<BicepValueProxy> args)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(args);

        var values = args.ToArray();
        return BicepValueProxy.CreateExpression(
            new FunctionCallExpression(
                new IdentifierExpression(name),
                [.. values.Select(static value => value.ToBicepExpression())]),
            values.Any(static value => value.IsSecure));
    }

    /// <summary>
    /// Creates a Bicep expression that concatenates string values.
    /// </summary>
    /// <param name="values">The values to concatenate.</param>
    /// <returns>A Bicep string concatenation expression.</returns>
    [AspireExport]
    internal BicepValueProxy Concat(IEnumerable<BicepValueProxy> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values.ToArray();
        return BicepValueProxy.Create(
            BicepFunction.Concat([.. items.Select(static value => BicepValueProxy.Convert<string>(value))]),
            items.Any(static value => value.IsSecure));
    }

    /// <summary>
    /// Creates a deterministic GUID from the supplied values.
    /// </summary>
    /// <param name="values">The values used to create the GUID.</param>
    /// <returns>A Bicep GUID expression.</returns>
    [AspireExport]
    internal BicepValueProxy CreateGuid(IEnumerable<BicepValueProxy> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values.ToArray();
        return BicepValueProxy.Create(
            BicepFunction.CreateGuid([.. items.Select(static value => BicepValueProxy.Convert<string>(value))]),
            items.Any(static value => value.IsSecure));
    }

    /// <summary>
    /// Creates a deterministic unique string from the supplied values.
    /// </summary>
    /// <param name="values">The values used to create the unique string.</param>
    /// <returns>A Bicep unique-string expression.</returns>
    [AspireExport]
    internal BicepValueProxy UniqueString(IEnumerable<BicepValueProxy> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values.ToArray();
        return BicepValueProxy.Create(
            BicepFunction.GetUniqueString([.. items.Select(static value => BicepValueProxy.Convert<string>(value))]),
            items.Any(static value => value.IsSecure));
    }

    /// <summary>
    /// Creates a subscription-scoped resource identifier.
    /// </summary>
    /// <param name="values">The resource type and name segments.</param>
    /// <returns>A Bicep subscription resource identifier expression.</returns>
    [AspireExport]
    internal BicepValueProxy SubscriptionResourceId(IEnumerable<BicepValueProxy> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values.ToArray();
        return BicepValueProxy.Create(
            BicepFunction.GetSubscriptionResourceId([.. items.Select(static value => BicepValueProxy.Convert<string>(value))]),
            items.Any(static value => value.IsSecure));
    }

    /// <summary>
    /// Creates a Bicep expression that takes characters from the start of a string.
    /// </summary>
    /// <param name="value">The string value.</param>
    /// <param name="count">The number of characters to take.</param>
    /// <returns>A Bicep string expression.</returns>
    [AspireExport]
    internal BicepValueProxy Take(
        [AspireUnion(typeof(string), typeof(BicepValueProxy))] object value,
        [AspireUnion(typeof(int), typeof(BicepValueProxy))] object count)
    {
        return BicepValueProxy.Create(
            BicepFunction.Take(
                BicepValueProxy.Convert<string>(value),
                BicepValueProxy.Convert<int>(count)),
            IsSecure(value) || IsSecure(count));
    }

    /// <summary>
    /// Creates a lowercase string expression.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>A lowercase Bicep string expression.</returns>
    [AspireExport]
    internal BicepValueProxy ToLower(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(BicepFunction.ToLower(value.ToObjectBicepValue()), value.IsSecure);
    }

    /// <summary>
    /// Creates an uppercase string expression.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>An uppercase Bicep string expression.</returns>
    [AspireExport]
    internal BicepValueProxy ToUpper(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(BicepFunction.ToUpper(value.ToObjectBicepValue()), value.IsSecure);
    }

    /// <summary>
    /// Creates an explicitly typed Bicep string expression.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>A Bicep string expression.</returns>
    [AspireExport]
    internal BicepValueProxy AsString(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(BicepFunction.AsString(value.ToObjectBicepValue()), value.IsSecure);
    }

    /// <summary>
    /// Creates an expression that parses a JSON string.
    /// </summary>
    /// <param name="value">The JSON string value.</param>
    /// <returns>A Bicep JSON parsing expression.</returns>
    [AspireExport]
    internal BicepValueProxy ParseJson(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(BicepFunction.ParseJson(value.ToObjectBicepValue()), value.IsSecure);
    }

    /// <summary>
    /// Creates an expression for the current resource group.
    /// </summary>
    /// <returns>A Bicep resource-group expression.</returns>
    [AspireExport]
    internal BicepValueProxy ResourceGroup()
    {
        return BicepValueProxy.Create(BicepFunction.GetResourceGroup());
    }

    /// <summary>
    /// Creates an expression for the current subscription.
    /// </summary>
    /// <returns>A Bicep subscription expression.</returns>
    [AspireExport]
    internal BicepValueProxy Subscription()
    {
        return BicepValueProxy.Create(BicepFunction.GetSubscription());
    }

    /// <summary>
    /// Creates an expression for the current tenant.
    /// </summary>
    /// <returns>A Bicep tenant expression.</returns>
    [AspireExport]
    internal BicepValueProxy Tenant()
    {
        return BicepValueProxy.Create(BicepFunction.GetTenant());
    }

    /// <summary>
    /// Creates an expression for the current deployment.
    /// </summary>
    /// <returns>A Bicep deployment expression.</returns>
    [AspireExport]
    internal BicepValueProxy Deployment()
    {
        return BicepValueProxy.Create(BicepFunction.GetDeployment());
    }

    /// <summary>
    /// Creates an expression that accesses a named member.
    /// </summary>
    /// <param name="value">The value containing the member.</param>
    /// <param name="member">The member name.</param>
    /// <returns>A Bicep member-access expression.</returns>
    [AspireExport]
    internal BicepValueProxy Member(BicepValueProxy value, string member)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrEmpty(member);

        return FromExpression(new MemberExpression(value.ToBicepExpression(), member), value.IsSecure);
    }

    /// <summary>
    /// Creates an expression that accesses an indexed value.
    /// </summary>
    /// <param name="value">The value to index.</param>
    /// <param name="index">The string or integer index.</param>
    /// <returns>A Bicep index expression.</returns>
    [AspireExport]
    internal BicepValueProxy Index(
        BicepValueProxy value,
        [AspireUnion(typeof(string), typeof(int), typeof(BicepValueProxy))] object index)
    {
        ArgumentNullException.ThrowIfNull(value);

        return FromExpression(
            new IndexExpression(value.ToBicepExpression(), ToExpression(index)),
            value.IsSecure || IsSecure(index));
    }

    /// <summary>
    /// Creates a binary Bicep expression.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="operator">The binary operator.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>A binary Bicep expression.</returns>
    [AspireExport]
    internal BicepValueProxy Binary(
        BicepValueProxy left,
        BinaryBicepOperator @operator,
        BicepValueProxy right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return FromExpression(
            new BinaryExpression(left.ToBicepExpression(), @operator, right.ToBicepExpression()),
            left.IsSecure || right.IsSecure);
    }

    /// <summary>
    /// Creates a unary Bicep expression.
    /// </summary>
    /// <param name="operator">The unary operator.</param>
    /// <param name="value">The operand.</param>
    /// <returns>A unary Bicep expression.</returns>
    [AspireExport]
    internal BicepValueProxy Unary(UnaryBicepOperator @operator, BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return FromExpression(new UnaryExpression(@operator, value.ToBicepExpression()), value.IsSecure);
    }

    /// <summary>
    /// Creates a conditional Bicep expression.
    /// </summary>
    /// <param name="condition">The condition expression.</param>
    /// <param name="consequent">The value used when the condition is true.</param>
    /// <param name="alternate">The value used when the condition is false.</param>
    /// <returns>A conditional Bicep expression.</returns>
    [AspireExport]
    internal BicepValueProxy Conditional(
        BicepValueProxy condition,
        BicepValueProxy consequent,
        BicepValueProxy alternate)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(consequent);
        ArgumentNullException.ThrowIfNull(alternate);

        return FromExpression(
            new ConditionalExpression(
                condition.ToBicepExpression(),
                consequent.ToBicepExpression(),
                alternate.ToBicepExpression()),
            condition.IsSecure || consequent.IsSecure || alternate.IsSecure);
    }

    /// <summary>
    /// Creates a builder for an interpolated Bicep string.
    /// </summary>
    /// <returns>A Bicep string builder.</returns>
    [AspireExport]
    internal BicepStringBuilderProxy CreateStringBuilder()
    {
        return new();
    }
#pragma warning restore CA1822

    private static BicepValueProxy FromExpression(BicepExpression expression, bool isSecure)
    {
        return BicepValueProxy.CreateExpression(expression, isSecure);
    }

    private static BicepExpression ToExpression(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            string literal => literal,
            int literal => literal,
            BicepValueProxy proxy => proxy.ToBicepExpression(),
            _ => throw new ArgumentException($"Expected a string, integer, or {nameof(BicepValueProxy)}.", nameof(value))
        };
    }

    private static bool IsSecure(object value)
    {
        return value is BicepValueProxy proxy && proxy.IsSecure;
    }
}
