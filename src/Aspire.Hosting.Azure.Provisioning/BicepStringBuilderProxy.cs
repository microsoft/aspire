// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Expressions;

namespace Aspire.Hosting.Azure.Provisioning;

/// <summary>
/// Builds an interpolated Bicep string from literal text and provisioning values.
/// </summary>
[AspireExport]
internal sealed class BicepStringBuilderProxy
{
    private readonly BicepStringBuilder _builder = new();
    private bool _isSecure;

    internal BicepStringBuilderProxy()
    {
    }

    [AspireExport]
    internal BicepStringBuilderProxy AppendLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _builder.Append(value);
        return this;
    }

    [AspireExport]
    internal BicepStringBuilderProxy AppendValue(BicepValueProxy value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _builder.Append(value.ToBicepExpression());
        _isSecure |= value.IsSecure;
        return this;
    }

    [AspireExport]
    internal BicepValueProxy Build()
    {
        return BicepValueProxy.Create(_builder.Build(), _isSecure);
    }
}
