// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace AnalyzeCiFailure;

internal sealed record ValidatedRun(string Scope, string Url, string PrNumbers);
internal sealed record TrustedRunIdentity(string Id, string MetadataId, string Scope, string Url, string PrNumbers);
internal sealed record ProposedRunIdentity(string Id, string Scope, bool HasMainSubjectPr, string? SubjectPrNumber, bool ExplicitNullSubjectPr);
internal sealed record FailedJob(NumericId Id, string SerializedId, string Name);
internal sealed record ClassifiedJob(NumericId Id, string SerializedId, string Classification);
internal sealed record FailedTest(string Name, string Job, string Classification);
internal sealed record TestIdentity(string Name, string Job);
internal sealed record FailureCause(string Id, string FileName, string Type, string TestName, ImmutableArray<NumericId> JobIds);
internal sealed record StoredCauseIdentity(string Type, string TestName);
internal sealed record FailureAnalysis(string Verdict, ImmutableArray<ClassifiedJob> Jobs, ImmutableArray<FailedTest> Tests, ImmutableArray<string> CauseIds);
