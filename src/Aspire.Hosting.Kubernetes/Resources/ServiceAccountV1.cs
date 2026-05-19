// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Kubernetes ServiceAccount resource.
/// </summary>
[YamlSerializable]
public sealed class ServiceAccountV1() : BaseKubernetesResource("v1", "ServiceAccount")
{
}
