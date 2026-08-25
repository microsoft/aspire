// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Provisioning;

internal static class BicepValueFactory
{
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

    [AspireExport]
    internal BicepValueProxy String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(new BicepValue<string>(value));
    }

    [AspireExport]
    internal BicepValueProxy Integer(int value)
    {
        return BicepValueProxy.Create(new BicepValue<int>(value));
    }

    [AspireExport]
    internal BicepValueProxy Boolean(bool value)
    {
        return BicepValueProxy.Create(new BicepValue<bool>(value));
    }

    [AspireExport]
    internal BicepValueProxy Double(double value)
    {
        return BicepValueProxy.Create(new BicepValue<double>(value));
    }

    [AspireExport]
    internal BicepValueProxy Guid(Guid value)
    {
        return BicepValueProxy.Create(new BicepValue<Guid>(value));
    }

    [AspireExport]
    internal BicepValueProxy Uri(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(new BicepValue<Uri>(value));
    }

    [AspireExport]
    internal BicepValueProxy TimeSpan(TimeSpan value)
    {
        return BicepValueProxy.Create(new BicepValue<TimeSpan>(value));
    }

    [AspireExport]
    internal BicepValueProxy Parameter(
        IResourceBuilder<ParameterResource> parameter,
        string? bicepIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        return BicepValueProxy.Create(parameter.AsProvisioningParameter(_infrastructure, bicepIdentifier));
    }

    [AspireExport]
    internal BicepValueProxy ReferenceExpression(
        ReferenceExpression expression,
        string? bicepIdentifier = null,
        bool? isSecure = null)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return BicepValueProxy.Create(expression.AsProvisioningParameter(_infrastructure, bicepIdentifier, isSecure));
    }

    [AspireExport]
    internal BicepValueProxy Identifier(string bicepIdentifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(bicepIdentifier);

        return BicepValueProxy.CreateExpression(new IdentifierExpression(bicepIdentifier), isSecure: false);
    }

    [AspireExport("resourceIdentifier")]
    internal BicepValueProxy Identifier(ProvisionableResourceProxy resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return Identifier(resource.BicepIdentifier);
    }

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

    [AspireExport]
    internal BicepValueProxy Concat(IEnumerable<BicepValueProxy> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values.ToArray();
        return BicepValueProxy.Create(
            BicepFunction.Concat([.. items.Select(static value => BicepValueProxy.Convert<string>(value))]),
            items.Any(static value => value.IsSecure));
    }

    [AspireExport]
    internal BicepValueProxy CreateGuid(IEnumerable<BicepValueProxy> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values.ToArray();
        return BicepValueProxy.Create(
            BicepFunction.CreateGuid([.. items.Select(static value => BicepValueProxy.Convert<string>(value))]),
            items.Any(static value => value.IsSecure));
    }

    [AspireExport]
    internal BicepValueProxy UniqueString(IEnumerable<BicepValueProxy> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values.ToArray();
        return BicepValueProxy.Create(
            BicepFunction.GetUniqueString([.. items.Select(static value => BicepValueProxy.Convert<string>(value))]),
            items.Any(static value => value.IsSecure));
    }

    [AspireExport]
    internal BicepValueProxy SubscriptionResourceId(IEnumerable<BicepValueProxy> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var items = values.ToArray();
        return BicepValueProxy.Create(
            BicepFunction.GetSubscriptionResourceId([.. items.Select(static value => BicepValueProxy.Convert<string>(value))]),
            items.Any(static value => value.IsSecure));
    }

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

    [AspireExport]
    internal BicepValueProxy ToLower(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(BicepFunction.ToLower(value.ToObjectBicepValue()), value.IsSecure);
    }

    [AspireExport]
    internal BicepValueProxy ToUpper(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(BicepFunction.ToUpper(value.ToObjectBicepValue()), value.IsSecure);
    }

    [AspireExport]
    internal BicepValueProxy AsString(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(BicepFunction.AsString(value.ToObjectBicepValue()), value.IsSecure);
    }

    [AspireExport]
    internal BicepValueProxy ParseJson(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BicepValueProxy.Create(BicepFunction.ParseJson(value.ToObjectBicepValue()), value.IsSecure);
    }

    [AspireExport]
    internal BicepValueProxy ResourceGroup()
    {
        return BicepValueProxy.Create(BicepFunction.GetResourceGroup());
    }

    [AspireExport]
    internal BicepValueProxy Subscription()
    {
        return BicepValueProxy.Create(BicepFunction.GetSubscription());
    }

    [AspireExport]
    internal BicepValueProxy Tenant()
    {
        return BicepValueProxy.Create(BicepFunction.GetTenant());
    }

    [AspireExport]
    internal BicepValueProxy Deployment()
    {
        return BicepValueProxy.Create(BicepFunction.GetDeployment());
    }

    [AspireExport]
    internal BicepValueProxy Member(BicepValueProxy value, string member)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrEmpty(member);

        return FromExpression(new MemberExpression(value.ToBicepExpression(), member), value.IsSecure);
    }

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

    [AspireExport]
    internal BicepValueProxy Unary(UnaryBicepOperator @operator, BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return FromExpression(new UnaryExpression(@operator, value.ToBicepExpression()), value.IsSecure);
    }

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
