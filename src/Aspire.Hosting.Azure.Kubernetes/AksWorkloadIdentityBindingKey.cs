// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Uniquely identifies a workload identity binding without relying on ambiguous concatenated resource names.
/// </summary>
internal readonly record struct AksWorkloadIdentityBindingKey(string WorkloadName, string? VolumeName);
