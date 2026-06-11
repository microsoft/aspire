// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Configuration;

/// <summary>
/// Indicates that a property is only applicable to local settings files (.aspire/settings.json)
/// and should not be included in the global settings schema.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal sealed class LocalAspireJsonConfigurationPropertyAttribute : Attribute
{
}
