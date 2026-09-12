// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

internal sealed class AzureSqlServerContainerResource(
    AzureSqlServerResource owner,
    ParameterResource password)
    : SqlServerServerResource(owner.Name, password)
{
    public override ResourceAnnotationCollection Annotations => owner.Annotations;

    internal bool IsConfigured { get; set; }
}

internal sealed class AzureSqlDatabaseContainerResource(
    AzureSqlDatabaseResource owner,
    SqlServerServerResource parent)
    : SqlServerDatabaseResource(owner.Name, owner.DatabaseName, parent)
{
    public override ResourceAnnotationCollection Annotations => owner.Annotations;
}
