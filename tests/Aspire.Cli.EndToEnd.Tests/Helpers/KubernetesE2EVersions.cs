// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Single source of truth for the tool versions installed into the E2E test
/// container during Kubernetes scenarios.
/// </summary>
/// <remarks>
/// Each value falls back to an environment variable so CI can pin or bump a
/// version without a code change. The defaults are chosen to satisfy Aspire's
/// own minimums:
/// <list type="bullet">
///   <item>
///     <description>
///       <c>HelmVersion</c> must be at least
///       <c>Aspire.Hosting.Kubernetes.HelmVersionValidator.MinimumHelmVersion</c>
///       (currently <c>4.2.0</c>). The Kubernetes deployment pipeline now
///       fails fast at <c>check-helm-prereqs-{env}</c> for older Helm CLIs,
///       so an older default here would break every <c>DeployK8s*</c> test.
///     </description>
///   </item>
/// </list>
/// </remarks>
internal static class KubernetesE2EVersions
{
    public static string KindVersion => Environment.GetEnvironmentVariable("KIND_VERSION") ?? "v0.31.0";

    public static string HelmVersion => Environment.GetEnvironmentVariable("HELM_VERSION") ?? "v4.2.0";

    public static string KubectlVersion => Environment.GetEnvironmentVariable("KUBECTL_VERSION") ?? "v1.34.3";
}
