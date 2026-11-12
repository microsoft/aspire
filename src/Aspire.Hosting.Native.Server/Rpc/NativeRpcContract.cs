// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Names the native-only entry points shared by dispatch and provider validation.</summary>
internal static class NativeRpcContract
{
    public const string ApplicationServerTypeId = "Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.NativeApplicationServer";
    public const string LifecycleScopeTypeId = "Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.ResourceLifecycleScope";
}
