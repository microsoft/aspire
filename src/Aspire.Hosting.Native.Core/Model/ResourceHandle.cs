// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Model;

/// <summary>Identifies a resource within one application generation.</summary>
internal readonly record struct ResourceHandle(Guid GenerationId, Guid ResourceId);
