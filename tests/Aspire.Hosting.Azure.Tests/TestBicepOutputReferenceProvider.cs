// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // IBicepOutputReferenceProvider is experimental.

namespace Aspire.Hosting.Azure.Tests;

internal sealed class TestBicepOutputReferenceProvider(BicepOutputReference? output) : IBicepOutputReferenceProvider
{
    public BicepOutputReference? GetOutputReference() => output;
}
