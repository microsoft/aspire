// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Xunit;

namespace Aspire.Hosting.Azure.Provisioning.Tests;

public class BicepValueProxyTests
{
    [Fact]
    public void AssemblyUsesProvisioningExperimentalDiagnostic()
    {
        var attribute = Assert.Single(
            typeof(BicepValueProxy).Assembly.GetCustomAttributes(typeof(ExperimentalAttribute), inherit: false)
                .Cast<ExperimentalAttribute>());

        Assert.Equal("ASPIREAZUREPROVISIONING001", attribute.DiagnosticId);
        Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", attribute.UrlFormat);
    }

    [Fact]
    public void AssignToPreservesLiteralValue()
    {
        var proxy = BicepValueProxy.Create(new BicepValue<string>("value"));
        var target = new BicepValue<string>("initial");

        proxy.AssignTo(target);

        var assigned = (IBicepValue)target;
        Assert.Equal(BicepValueKind.Literal, assigned.Kind);
        Assert.Equal("value", assigned.LiteralValue);
    }

    [Fact]
    public void AssignToPreservesSecureLiteralMetadata()
    {
        var proxy = BicepValueProxy.Create(new BicepValue<string>("secret"), isSecure: true);
        var target = new BicepValue<string>("initial");

        proxy.AssignTo(target);

        var assigned = (IBicepValue)target;
        Assert.True(assigned.IsSecure);
        Assert.Equal(BicepValueKind.Literal, assigned.Kind);
        Assert.Equal("secret", assigned.LiteralValue);
    }

    [Fact]
    public void AssignToPreservesExpression()
    {
        var proxy = BicepValueProxy.Create(
            new BicepValue<string>(new IdentifierExpression("resourceName")));
        var target = new BicepValue<string>("initial");

        proxy.AssignTo(target);

        Assert.Equal(BicepValueKind.Expression, ((IBicepValue)target).Kind);
        Assert.Equal("resourceName", target.ToBicepExpression().ToString());
    }

    [Fact]
    public void AssignToPreservesSecureMetadata()
    {
        var proxy = BicepValueProxy.Create(
            new BicepValue<string>(new IdentifierExpression("secretValue")),
            isSecure: true);
        var target = new BicepValue<string>("initial");

        proxy.AssignTo(target);

        var assigned = (IBicepValue)target;
        Assert.True(assigned.IsSecure);
        Assert.Equal(BicepValueKind.Expression, assigned.Kind);
        Assert.Equal("secretValue", target.ToBicepExpression().ToString());
    }

    [Fact]
    public void AssignToAllowsCompatibleNullableLiteralTarget()
    {
        var proxy = BicepValueProxy.Create(new BicepValue<int>(42));
        var target = new BicepValue<int?>(null);

        proxy.AssignTo(target);

        var assigned = (IBicepValue)target;
        Assert.Equal(BicepValueKind.Literal, assigned.Kind);
        Assert.Equal(42, assigned.LiteralValue);
        Assert.Equal(42, target.Value);
    }

    [Fact]
    public void ConvertPreservesExpression()
    {
        var proxy = BicepValueProxy.Create(
            new BicepValue<int>(new BinaryExpression(20, BinaryBicepOperator.Add, 10)));

        var converted = BicepValueProxy.Convert<int>(proxy);

        Assert.Equal(BicepValueKind.Expression, ((IBicepValue)converted).Kind);
        Assert.Equal("(20 + 10)", converted.ToBicepExpression().ToString());
    }

    [Fact]
    public void ConvertPreservesSecureLiteralMetadata()
    {
        var proxy = BicepValueProxy.Create(new BicepValue<string>("secret"), isSecure: true);

        var converted = BicepValueProxy.Convert<string>(proxy);

        var assigned = (IBicepValue)converted;
        Assert.True(assigned.IsSecure);
        Assert.Equal(BicepValueKind.Literal, assigned.Kind);
        Assert.Equal("secret", assigned.LiteralValue);
    }

    [Fact]
    public void AssignToPreservesAzureLocationLiteral()
    {
        var proxy = BicepValueProxy.Create(new BicepValue<AzureLocation>(AzureLocation.WestUS2));
        var target = new BicepValue<AzureLocation>(AzureLocation.EastUS);

        proxy.AssignTo(target);

        var assigned = (IBicepValue)target;
        Assert.Equal(BicepValueKind.Literal, assigned.Kind);
        Assert.Equal(AzureLocation.WestUS2, assigned.LiteralValue);
    }

    [Fact]
    public void ConvertRejectsIncompatibleLiteralType()
    {
        var proxy = BicepValueProxy.Create(new BicepValue<int>(42));

        var exception = Assert.Throws<ArgumentException>(() => BicepValueProxy.Convert<string>(proxy));

        Assert.Equal(
            "A literal of type Int32 cannot be assigned to a BicepValue<String>.",
            exception.Message);
    }

    [Fact]
    public void InterpolatedValuePreservesExpressionAndSecurity()
    {
        var secureValue = BicepValueProxy.Create(
            new BicepValue<string>(new IdentifierExpression("secretValue")),
            isSecure: true);
        var builder = new BicepStringBuilderProxy()
            .AppendLiteral("prefix-")
            .AppendValue(secureValue);

        var result = builder.Build();
        var target = new BicepValue<string>("initial");
        result.AssignTo(target);

        var assigned = (IBicepValue)target;
        Assert.True(assigned.IsSecure);
        Assert.Equal(BicepValueKind.Expression, assigned.Kind);
        Assert.Equal("'prefix-${secretValue}'", target.ToBicepExpression().ToString());
    }

    [Fact]
    public void ParameterFactoryCreatesSecureParameterReference()
    {
        var builder = DistributedApplication.CreateBuilder();
        var parameter = builder.AddParameter("secret", "value", secret: true);
        var target = new BicepValue<string>("initial");

        CreateInfrastructure()
            .Create()
            .Parameter(parameter, "secretParam")
            .AssignTo(target);

        var assigned = (IBicepValue)target;
        Assert.True(assigned.IsSecure);
        Assert.Equal(BicepValueKind.Expression, assigned.Kind);
        Assert.Equal("secretParam", target.ToBicepExpression().ToString());
    }

    [Fact]
    public void ReferenceExpressionFactoryCreatesSecureParameterReference()
    {
        var target = new BicepValue<string>("initial");

        CreateInfrastructure()
            .Create()
            .ReferenceExpression(ReferenceExpression.Create($"secretValue"), "secretParam", isSecure: true)
            .AssignTo(target);

        var assigned = (IBicepValue)target;
        Assert.True(assigned.IsSecure);
        Assert.Equal(BicepValueKind.Expression, assigned.Kind);
        Assert.Equal("secretParam", target.ToBicepExpression().ToString());
    }

    private static AzureResourceInfrastructure CreateInfrastructure()
    {
        var constructor = typeof(AzureResourceInfrastructure).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(AzureProvisioningResource), typeof(string)],
            modifiers: null);

        Assert.NotNull(constructor);

        return (AzureResourceInfrastructure)constructor.Invoke([new AzureProvisioningResource("test", _ => { }), "test"]);
    }
}
