// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AnalyzeCiFailure;

/// <summary>Determines which artifact results reserve budget and are required to establish an execution&apos;s outcome.</summary>
internal enum TestEvidencePolicy
{
    Required,
    OptionalDiagnostics,
    RequiredForInterruptedExecution,
}

/// <summary>Describes one producing job&apos;s expected artifact, parser format and evidence-availability policy.</summary>
internal sealed record TestArtifactExpectation(
    string JobName,
    string ArtifactName,
    TestArtifactFormat Format,
    TestEvidencePolicy EvidencePolicy)
{
    internal bool ReservesBudget => EvidencePolicy == TestEvidencePolicy.Required;
    internal bool RequiresResults => EvidencePolicy != TestEvidencePolicy.OptionalDiagnostics;
}
