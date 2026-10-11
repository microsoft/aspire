// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Sessions;
using Aspire.Hosting.Native.Rpc;
using System.Text;

if (RuntimeFeature.IsDynamicCodeSupported)
{
    throw new InvalidOperationException("The kernel smoke test must run as a native executable.");
}

if (args is ["--rpc"])
{
    var token = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_RPC_TOKEN")
        ?? throw new InvalidOperationException("Set ASPIRE_NATIVE_RPC_TOKEN for the native RPC smoke process.");
    using var connection = new NativeRpcConnection(token);
    while (Console.ReadLine() is { } line)
    {
        var response = connection.Process(Encoding.UTF8.GetBytes(line));
        if (response is not null)
        {
            Console.WriteLine(Encoding.UTF8.GetString(response));
        }
    }

    return;
}

using var diagnostics = new DiagnosticCapture(sampleActivities: true);
using var session = new ApplicationSession();
var model = session.StartGeneration();
var web = model.AddResource("web", "example.javascript/Application");
var cache = model.AddResource("cache", "example.redis/Redis");
model.AddDependency(web, cache);
var snapshot = model.Seal();
if (snapshot.Resources.Length != 2 || snapshot.Resources[0].Handle != cache ||
    snapshot.Resources[1].Dependencies.Single() != cache || !ReferenceEquals(snapshot, model.Seal()))
{
    throw new InvalidOperationException("Native declaration composition or snapshot invariants failed.");
}

session.RetireGeneration(model.GenerationId);
var replacement = session.StartGeneration();
if (replacement.GenerationId == model.GenerationId || replacement.Inspect().Resources.Length != 0)
{
    throw new InvalidOperationException("Native generation replacement failed.");
}

if (diagnostics.Activities.IsEmpty || diagnostics.Events.IsEmpty || diagnostics.Measurements.IsEmpty)
{
    throw new InvalidOperationException("Native diagnostic activities, events, or metrics were not emitted.");
}

Console.WriteLine("Native model composition, snapshots, generation replacement, and diagnostics passed.");
