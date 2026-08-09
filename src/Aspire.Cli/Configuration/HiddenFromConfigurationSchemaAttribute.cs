// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Configuration;

/// <summary>
/// Indicates that a configuration property is supported for serialization or migration but should not be advertised in generated schemas.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal sealed class HiddenFromConfigurationSchemaAttribute : Attribute
{
}
