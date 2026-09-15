// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Net;
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

    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    public void ConvertParsesIPAddressAndRoundTripsThroughProxy(string address)
    {
        var converted = BicepValueProxy.Convert<IPAddress>(address);
        var proxy = BicepValueProxy.Create(converted, isSecure: true);
        var roundTripped = BicepValueProxy.Convert<IPAddress>(proxy);

        var assigned = (IBicepValue)roundTripped;
        Assert.Equal(BicepValueKind.Literal, assigned.Kind);
        Assert.Equal(IPAddress.Parse(address), assigned.LiteralValue);
        Assert.True(assigned.IsSecure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("192.0.2.999")]
    [InlineData("2001:db8::invalid")]
    public void ConvertRejectsInvalidIPAddress(string address)
    {
        Assert.Throws<FormatException>(() => BicepValueProxy.Convert<IPAddress>(address));
    }

    [Fact]
    public void ConvertPreservesIPAddressExpressionAndSecurity()
    {
        var proxy = BicepValueProxy.Create(
            new BicepValue<IPAddress>(new IdentifierExpression("ipAddress")),
            isSecure: true);

        var converted = BicepValueProxy.Convert<IPAddress>(proxy);
        var assigned = (IBicepValue)converted;
        Assert.Equal(BicepValueKind.Expression, assigned.Kind);
        Assert.Equal("ipAddress", converted.ToBicepExpression().ToString());
        Assert.True(assigned.IsSecure);
    }

    [Fact]
    public void ConvertAllowsUntypedExpressionForIPAddress()
    {
        var proxy = BicepValueProxy.CreateExpression(new IdentifierExpression("ipAddress"), isSecure: false);

        var converted = BicepValueProxy.Convert<IPAddress>(proxy);

        Assert.Equal(BicepValueKind.Expression, ((IBicepValue)converted).Kind);
        Assert.Equal("ipAddress", converted.ToBicepExpression().ToString());
    }

    [Fact]
    public void ConvertRejectsIncompatibleIPAddressProxy()
    {
        var proxy = BicepValueProxy.Create(new BicepValue<int>(42));

        var exception = Assert.Throws<ArgumentException>(() => BicepValueProxy.Convert<IPAddress>(proxy));

        Assert.Equal("A literal of type Int32 cannot be assigned to a BicepValue<IPAddress>.", exception.Message);
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
    public void DeclarationProxiesValidateConfiguredLiteralType()
    {
        var infrastructure = CreateInfrastructure();
        var parameter = infrastructure.AddBicepParameter("parameter", ProvisioningValueType.Integer);
        var output = infrastructure.AddBicepOutput("output", ProvisioningValueType.Integer);
        var variable = infrastructure.AddBicepVariable("variable", ProvisioningValueType.Integer);

        AssertDeclarationTypeValidation(
            value => parameter.Value = value,
            () => parameter.Inner.Value);
        AssertDeclarationTypeValidation(
            value => output.Value = value,
            () => output.Inner.Value);
        AssertDeclarationTypeValidation(
            value => variable.Value = value,
            () => variable.Inner.Value);
    }

    [Fact]
    public void DeclarationProxiesPreserveConfiguredTypeWhenRead()
    {
        var infrastructure = CreateInfrastructure();
        var parameter = infrastructure.AddBicepParameter("parameter", ProvisioningValueType.Integer);
        var output = infrastructure.AddBicepOutput("output", ProvisioningValueType.Integer);
        var variable = infrastructure.AddBicepVariable("variable", ProvisioningValueType.Integer);

        AssertDeclarationTypeRoundTrips(
            value => parameter.Value = value,
            () => parameter.Value);
        AssertDeclarationTypeRoundTrips(
            value => output.Value = value,
            () => output.Value);
        AssertDeclarationTypeRoundTrips(
            value => variable.Value = value,
            () => variable.Value);
    }

    [Theory]
    [InlineData(nameof(ProvisioningValueType.Boolean))]
    [InlineData(nameof(ProvisioningValueType.Integer))]
    public void BicepParametersRejectUnsupportedSecureTypes(string typeName)
    {
        var infrastructure = CreateInfrastructure();
        var type = Enum.Parse<ProvisioningValueType>(typeName);
        var originalResources = infrastructure.GetProvisionableResources().ToArray();

        var creationException = Assert.Throws<ArgumentException>(
            () => infrastructure.AddBicepParameter("parameter", type, isSecure: true));

        Assert.Equal("isSecure", creationException.ParamName);
        Assert.Equal(
            "Only string, object, and GUID Bicep parameters can be secure. (Parameter 'isSecure')",
            creationException.Message);
        Assert.Equal(originalResources, infrastructure.GetProvisionableResources());

        var parameter = infrastructure.AddBicepParameter("parameter", type, isSecure: false);
        Assert.False(parameter.IsSecure);

        var mutationException = Assert.Throws<ArgumentException>(() => parameter.IsSecure = true);

        Assert.Equal("value", mutationException.ParamName);
        Assert.False(parameter.IsSecure);
        Assert.False(parameter.Inner.IsSecure);

        parameter.IsSecure = false;
        Assert.False(parameter.IsSecure);
    }

    [Theory]
    [InlineData(nameof(ProvisioningValueType.String))]
    [InlineData(nameof(ProvisioningValueType.Object))]
    [InlineData(nameof(ProvisioningValueType.Guid))]
    public void BicepParametersSupportSecureTypes(string typeName)
    {
        var infrastructure = CreateInfrastructure();
        var type = Enum.Parse<ProvisioningValueType>(typeName);
        var parameter = infrastructure.AddBicepParameter("parameter", type, isSecure: true);

        Assert.True(parameter.IsSecure);
        Assert.True(parameter.Inner.IsSecure);

        parameter.IsSecure = false;
        Assert.False(parameter.IsSecure);
        Assert.False(parameter.Inner.IsSecure);

        parameter.IsSecure = true;
        Assert.True(parameter.IsSecure);
        Assert.True(parameter.Inner.IsSecure);
    }

    [Theory]
    [InlineData(nameof(ProvisioningValueType.Boolean), true)]
    [InlineData(nameof(ProvisioningValueType.Integer), 42)]
    public void BicepParametersRejectUnsupportedSecureValues(string typeName, object literal)
    {
        var parameter = CreateInfrastructure().AddBicepParameter(
            "parameter", Enum.Parse<ProvisioningValueType>(typeName));
        IBicepValue initialValue = new BicepValue<object>(literal);
        parameter.Value = BicepValueProxy.Create(initialValue);
        var secureValue = literal switch
        {
            bool value => BicepValueProxy.Create(new BicepValue<bool>(value), isSecure: true),
            int value => BicepValueProxy.Create(new BicepValue<int>(value), isSecure: true),
            _ => throw new ArgumentException("Expected a Boolean or integer literal.", nameof(literal))
        };

        var exception = Assert.Throws<ArgumentException>(() => parameter.Value = secureValue);

        Assert.Equal("value", exception.ParamName);
        Assert.Equal(literal, ((IBicepValue)parameter.Inner.Value).LiteralValue);
        Assert.False(parameter.IsSecure);
        Assert.False(parameter.Value.IsSecure);
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

    private static void AssertDeclarationTypeValidation(
        Action<BicepValueProxy> assign,
        Func<IBicepValue> getAssignedValue)
    {
        assign(BicepValueProxy.Create(new BicepValue<int>(42)));
        Assert.Equal(42, getAssignedValue().LiteralValue);

        var exception = Assert.Throws<ArgumentException>(
            () => assign(BicepValueProxy.Create(new BicepValue<string>("text"))));

        Assert.Equal(
            "A literal of type String cannot be assigned to a BicepValue<Int32>.",
            exception.Message);
        Assert.Equal(42, getAssignedValue().LiteralValue);
    }

    private static void AssertDeclarationTypeRoundTrips(
        Action<BicepValueProxy> assign,
        Func<BicepValueProxy> read)
    {
        assign(BicepValueProxy.Create(new BicepValue<int>(42)));

        var literalTarget = new BicepValue<int>(0);
        read().AssignTo(literalTarget);
        Assert.Equal(42, literalTarget.Value);

        assign(BicepValueProxy.Create(
            new BicepValue<int>(new IdentifierExpression("integerValue"))));

        var expressionTarget = new BicepValue<int>(0);
        read().AssignTo(expressionTarget);
        Assert.Equal(BicepValueKind.Expression, ((IBicepValue)expressionTarget).Kind);
        Assert.Equal("integerValue", expressionTarget.ToBicepExpression().ToString());

        var exception = Assert.Throws<ArgumentException>(
            () => read().AssignTo(new BicepValue<string>("initial")));

        Assert.Equal(
            "A value of type Int32 cannot be assigned to a BicepValue<String>.",
            exception.Message);
    }
}
