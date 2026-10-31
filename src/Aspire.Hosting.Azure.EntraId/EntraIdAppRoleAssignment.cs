// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Records an explicit grant to a managed identity or a compute resource's publish-time identity.
/// </summary>
internal sealed record EntraIdAppRoleAssignment(IResource Target, string[] RoleValues);
