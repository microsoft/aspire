// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

public class KubernetesDeployTestHelpersTests
{
    [Fact]
    public void GetDeletePodCommand_WaitsForOldPodRemoval()
    {
        Assert.Equal(
            "kubectl delete pod app-statefulset-0 -n test-ns --wait=true",
            KubernetesDeployTestHelpers.GetDeletePodCommand("test-ns", "app-statefulset-0"));
    }

    [Fact]
    public void GetWaitForRecreatedPodReadyCommand_WaitsForCreationBeforeReadiness()
    {
        // Waiting for Ready on a StatefulSet pod that the controller has not recreated yet
        // fails immediately with NotFound, so readiness must be gated on creation.
        Assert.Equal(
            "kubectl wait --for=create pod/app-statefulset-0 -n test-ns --timeout=180s && " +
            "kubectl wait --for=condition=Ready pod/app-statefulset-0 -n test-ns --timeout=180s",
            KubernetesDeployTestHelpers.GetWaitForRecreatedPodReadyCommand("test-ns", "app-statefulset-0"));
    }
}
