// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Aspire.Hosting.RemoteHost.Tests.TestServices;

internal static class TestThreadPoolHeadroom
{
    // xunit's conservative scheduler raises the worker floor to max(4, maxParallelThreads) and
    // can run that many synchronous tests on pool threads at once. Several tests in this assembly
    // (API export, hosting-assembly scans) are long, CPU-bound and synchronous, so they can occupy
    // every worker at the floor. Hill climbing does not inject more threads while those tests keep
    // completing, which left async JSON-RPC continuations queued for 4-11s on Windows CI (#21612).
    internal static int SynchronousTestSlots => Math.Max(4, Environment.ProcessorCount);

    internal static int MinimumWorkerThreads => SynchronousTestSlots * 2;

    [ModuleInitializer]
    internal static void Initialize()
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        if (workerThreads < MinimumWorkerThreads)
        {
            ThreadPool.SetMinThreads(MinimumWorkerThreads, completionPortThreads);
        }
    }
}
