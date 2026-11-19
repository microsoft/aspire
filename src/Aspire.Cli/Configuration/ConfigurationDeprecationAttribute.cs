// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Configuration;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal sealed class ConfigurationDeprecationAttribute(string message) : Attribute
{
    public string Message { get; } = message;
}
