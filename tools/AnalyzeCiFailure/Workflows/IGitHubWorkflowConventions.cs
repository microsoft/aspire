// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace AnalyzeCiFailure;

/// <summary>Defines trusted workflow recognition, job selection and structured-result artifact expectations.</summary>
internal interface IGitHubWorkflowConventions
{
    string WorkflowPath { get; }
    bool IsAggregationJob(JsonObject job);
    IReadOnlyList<TestArtifactExpectation> DescribeTestArtifacts(JsonObject job, int runAttempt);
}
