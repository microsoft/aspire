// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Dcp.Model;
using k8s;
using Microsoft.DotNet.RemoteExecutor;
using V2 = Aspire.Hosting.Dcp.Model.V2;

namespace Aspire.Hosting.Tests.Dcp;

public class DcpV2ModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaInitializationIsIndependentOfFirstVersionUsed(bool v2First)
    {
        RemoteExecutor.Invoke(static value =>
        {
            if (bool.Parse(value))
            {
                V2.Namespace.Create("app");
            }
            else
            {
                Container.Create("container", "image");
            }

            Assert.True(Aspire.Hosting.Dcp.Model.Dcp.Schema.TryGet<V2.Namespace>(out var v2));
            Assert.Equal("usvc-dev.developer.microsoft.com/v2", v2.GroupVersion.ToString());
            Assert.False(v2.IsNamespaced);
            Assert.True(Aspire.Hosting.Dcp.Model.Dcp.Schema.TryGet<Container>(out var v1));
            Assert.Equal("usvc-dev.developer.microsoft.com/v1", v1.GroupVersion.ToString());
        }, v2First.ToString()).Dispose();
    }

    // Payloads follow the structs at microsoft/dcp c5edc40a, including embedded Go structs,
    // numeric file modes, nullable exit codes and fixed-width metav1.MicroTime timestamps.
    public static TheoryData<string, string> Payloads => new()
    {
        { "Namespace", """
            {
              "status": {
                "phase": "Active",
                "conditions": [{"type":"Ready","status":"True","observedGeneration":1,"lastTransitionTime":"2026-11-09T12:00:00Z","reason":"NamespaceActive","message":"Ready"}]
              }
            }
            """ },
        { "LogicalService", """
            {
              "spec": {"protocol":"UDP","address":"::1","port":8080,"addressAllocationMode":"IPv6ZeroOne"},
              "status": {
                "phase":"Ready",
                "effectiveAddresses":[{"address":"127.0.0.1","port":8080},{"address":"::1","port":8080}],
                "proxylessEndpointRef":"app/backend",
                "conditions":[{"type":"Ready","status":"True","observedGeneration":1,"lastTransitionTime":"2026-11-09T12:00:00Z","reason":"ServiceReady","message":"Ready"}]
              }
            }
            """ },
        { "ServiceEndpoint", """
            {
              "spec": {"serviceRef":"app/service","addresses":[{"address":"127.0.0.1","port":8081},{"address":"::1","port":8081}]}
            }
            """ },
        { "LogicalContainerNetwork", """
            {
              "spec": {"networkName":"backend","mode":"persistent","lifecycleKey":"network-key","network":{"ipv6":true,"labels":[{"key":"app","value":"demo"}]}},
              "status": {
                "phase":"Ready","physicalNetworkRef":"app/network-physical","lifecycleKey":"network-key",
                "networkID":"network-id","networkName":"backend","driver":"bridge","ipv6":true,
                "subnets":["172.20.0.0/16","fd00::/64"],"gateways":["172.20.0.1","fd00::1"],"containerIDs":["container-id"],
                "createdAt":"2026-11-09T12:00:00.123456Z",
                "conditions":[{"type":"Ready","status":"True","observedGeneration":1,"lastTransitionTime":"2026-11-09T12:00:00Z","reason":"NetworkAvailable","message":"Ready"}]
              }
            }
            """ },
        { "LogicalContainer", """
            {
              "spec": {
                "containerName":"backend","mode":"persistent","lifecycleKey":"container-key",
                "container": {
                  "image":{
                    "image":"demo:latest","pullPolicy":"bestEffort","buildPolicy":"ifNeeded","pullRetryLimit":0,
                    "build":{
                      "contextArchive":{"source":"/src/context.tar","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"},
                      "digest":"build-key","dockerfile":"Dockerfile","tags":["demo:latest"],"args":[{"name":"CONFIG","value":"Release"}],
                      "secrets":[{"type":"env","id":"token","source":"BUILD_TOKEN","value":"test-value"},{"type":"file","id":"config","source":"/src/config"}],
                      "stage":"final","labels":[{"key":"build","value":"demo"}],"platform":"linux/arm64","baseImages":["base:latest"]
                    }
                  },
                  "entrypoint":"/app/start","command":["--port","{{ portForServing \"http\" }}"],
                  "env":[{"name":"EMPTY","value":""}],
                  "volumeMounts":[{"type":"bind","source":"/src","target":"/workspace","readOnly":true},{"type":"volume","volumeRef":"app/data","target":"/data"}],
                  "networks":[{"networkRef":"app/network","aliases":["backend"]}],
                  "createFiles":[{"destination":"/config","defaultOwner":1000,"defaultGroup":1000,"umask":0,"entries":[
                    {"type":"directory","name":"nested","owner":0,"group":0,"mode":493,"entries":[{"type":"file","name":"app.txt","contents":"hello"}]},
                    {"type":"file","name":"copy.txt","source":"/src/copy.txt","continueOnError":true},
                    {"type":"openssl","name":"cert.pem","rawContents":"Y2VydA=="}
                  ]}],
                  "labels":[{"key":"app","value":"demo"}]
                },
                "ports":[
                  {"name":"http","serviceRef":"app/service","containerPort":80,"hostPort":8081,"hostIP":"127.0.0.1","address":"localhost"},
                  {"name":"admin","service":{"name":"admin","protocol":"TCP","address":"127.0.0.1","port":8082,"addressAllocationMode":"IPv4ZeroOne"},"containerPort":81}
                ],
                "monitorPID":4294967295,"monitorTimestamp":"2026-11-09T12:00:00.000000Z","stop":true
              },
              "status": {
                "ports":[{"name":"http","serviceRef":"app/service","address":"localhost","port":80}],
                "startupLogs":{"name":"image","uid":"image-uid","stdoutFile":"/logs/build.out","stderrFile":"/logs/build.err","raw":true,"available":true,"complete":true},
                "runtimeLogs":{"name":"container","uid":"container-uid","containerID":"container-id","available":true},
                "phase":"Exited","physicalContainerRef":"app/container-physical","imageRef":"app/image",
                "containerID":"container-id","containerName":"backend","image":"demo:latest","lifecycleKey":"container-key",
                "createdAt":"2026-11-09T12:00:00.123456Z","startedAt":"2026-11-09T12:00:01.000000Z","finishedAt":"2026-11-09T12:00:02.000000Z","exitCode":0,
                "portMappings":[{"containerPort":80,"protocol":"TCP","hostIP":"127.0.0.1","hostPort":8081}],
                "conditions":[{"type":"Ready","status":"False","observedGeneration":1,"lastTransitionTime":"2026-11-09T12:00:02Z","reason":"ContainerExited","message":"Exited"}]
              }
            }
            """ },
        { "LogicalProcess", """
            {
              "spec": {
                "mode":"persistent","lifecycleKey":"process-key",
                "process":{"executablePath":"/usr/bin/server","args":["--port","{{ portForServing \"http\" }}"],"workingDirectory":"/src","ambientEnvironment":{"behavior":"DoNotInherit"},"env":[{"name":"EMPTY","value":""}]},
                "ports":[
                  {"name":"http","serviceRef":"app/service","address":"localhost","port":0},
                  {"name":"admin","service":{"name":"admin","protocol":"TCP","address":"localhost","port":8082,"addressAllocationMode":"Localhost"},"port":8083}
                ],
                "monitorPID":4294967294,"monitorTimestamp":"2026-11-09T12:00:00.123456Z","stop":true
              },
              "status": {
                "ports":[{"name":"http","serviceRef":"app/service","address":"localhost","port":8081}],
                "runtimeLogs":{"name":"process","uid":"process-uid","stdoutFile":"/logs/process.out","stderrFile":"/logs/process.err","raw":true,"available":true,"complete":true},
                "phase":"Exited","physicalProcessRef":"app/process-physical","lifecycleKey":"process-key","pid":4294967295,
                "identityTimestamp":"2026-11-09T12:00:00.000000Z","finishedAt":"2026-11-09T12:00:02.000000Z","exitCode":0,
                "conditions":[{"type":"Ready","status":"False","observedGeneration":1,"lastTransitionTime":"2026-11-09T12:00:02Z","reason":"ProcessExited","message":"Exited"}]
              }
            }
            """ },
        { "LogicalContainerExec", """
            {
              "spec":{"containerRef":"app/container","execution":{"command":"/bin/sh","args":["-c","echo hello"],"workingDirectory":"/app","env":[{"name":"EMPTY","value":""}],"envFiles":["/config/env"]}},
              "status":{
                "phase":"Exited","startedAt":"2026-11-09T12:00:00.000000Z","finishedAt":"2026-11-09T12:00:01.000000Z","exitCode":0,
                "conditions":[{"type":"Ready","status":"True","observedGeneration":1,"lastTransitionTime":"2026-11-09T12:00:01Z","reason":"ExecutionExited","message":"Done"}],
                "physicalExecRef":"app/exec-physical","containerID":"container-id",
                "runtimeLogs":{"name":"exec","uid":"exec-uid","stdoutFile":"/logs/exec.out","stderrFile":"/logs/exec.err","available":true,"complete":true}
              }
            }
            """ },
        { "PhysicalContainerVolume", """
            {
              "spec":{"volumeName":"data","removeRuntimeVolumeOnDelete":true,"labels":[{"key":"app","value":"demo"}]},
              "status":{
                "phase":"Ready","volumeID":"volume-id","driver":"local","mountPoint":"/volumes/data","scope":"local","createdAt":"2026-11-09T12:00:00.123456Z",
                "conditions":[{"type":"Ready","status":"True","observedGeneration":1,"lastTransitionTime":"2026-11-09T12:00:00Z","reason":"VolumeAvailable","message":"Ready"}]
              }
            }
            """ }
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task RoundTripsGoContracts(string kind, string payload)
    {
        var source = JsonNode.Parse(payload)!.AsObject();
        source["apiVersion"] = "usvc-dev.developer.microsoft.com/v2";
        source["kind"] = kind;
        source["metadata"] = new JsonObject { ["name"] = "resource", ["namespace"] = kind == "Namespace" ? null : "app" };
        var json = source.ToJsonString();
        CustomResource resource = kind switch
        {
            "Namespace" => KubernetesJson.Deserialize<V2.Namespace>(json),
            "LogicalService" => KubernetesJson.Deserialize<V2.LogicalService>(json),
            "ServiceEndpoint" => KubernetesJson.Deserialize<V2.ServiceEndpoint>(json),
            "LogicalContainerNetwork" => KubernetesJson.Deserialize<V2.LogicalContainerNetwork>(json),
            "LogicalContainer" => KubernetesJson.Deserialize<V2.LogicalContainer>(json),
            "LogicalProcess" => KubernetesJson.Deserialize<V2.LogicalProcess>(json),
            "LogicalContainerExec" => KubernetesJson.Deserialize<V2.LogicalContainerExec>(json),
            "PhysicalContainerVolume" => KubernetesJson.Deserialize<V2.PhysicalContainerVolume>(json),
            _ => throw new InvalidOperationException(kind)
        };
        var serialized = KubernetesJson.Serialize(resource);
        var result = JsonNode.Parse(serialized)!;

        // A snapshot alone could bless a missing DTO property. Also check that every supplied
        // spec/status field survives deserialization and reserialization.
        Assert.True(JsonNode.DeepEquals(source["spec"], result["spec"]), serialized);
        Assert.True(JsonNode.DeepEquals(source["status"], result["status"]), serialized);
        await Verify(result.ToJsonString(new() { WriteIndented = true }), "json").UseParameters(kind);
    }

    [Fact]
    public async Task FactoriesAndDefaultsPreserveOmissionAndPointerZeroValues()
    {
        CustomResource[] resources =
        [
            V2.Namespace.Create("app"),
            V2.LogicalService.Create("service", "app"),
            V2.ServiceEndpoint.Create("endpoint", "app", "service"),
            V2.LogicalContainerNetwork.Create("network", "app"),
            V2.LogicalContainer.Create("container", "app"),
            V2.LogicalProcess.Create("process", "app"),
            V2.LogicalContainerExec.Create("exec", "app", "container"),
            V2.PhysicalContainerVolume.Create("volume", "app", "data")
        ];
        var output = new JsonObject();
        foreach (var resource in resources)
        {
            Assert.Equal("usvc-dev.developer.microsoft.com/v2", resource.ApiVersion);
            Assert.Equal(resource is V2.Namespace ? null : "app", resource.Metadata.NamespaceProperty);
            output[resource.Kind] = JsonNode.Parse(KubernetesJson.Serialize(resource));
        }

        output["pointerZeroes"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.LogicalContainerSpec
        {
            MonitorPid = 0,
            Container = new()
            {
                Image = new() { PullRetryLimit = 0 },
                CreateFiles = [new() { Umask = 0, Entries = [new() { Name = "file", Owner = 0, Group = 0 }] }]
            }
        }));
        output["allocatedProcessPort"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.LogicalProcessPort { Name = "http" }));
        output["unknownProcessExitCode"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.LogicalProcessStatus { Phase = V2.LogicalProcessPhase.Exited }));
        output["successfulProcessExitCode"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.LogicalProcessStatus { ExitCode = 0 }));
        output["containerPortRange"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.ContainerPortSpec
        {
            ContainerPort = 8000, HostPort = 9000, RangeSize = 3, HostIp = "::1", Protocol = "UDP"
        }));
        output["existingImageRef"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.LogicalContainerConfig { ImageRef = "app/image" }));
        output["directoryBuild"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.ContainerBuildContext { Context = "/src" }));
        output["inlineArchive"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.ContainerBuildContextArchive { RawContents = "dGFy" }));
        output["existingProcess"] = JsonNode.Parse(KubernetesJson.Serialize(new V2.LogicalProcessSpec
        {
            Mode = V2.LogicalProcessMode.Existing,
            Pid = 4294967295,
            IdentityTimestamp = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc)
        }));
        await Verify(output.ToJsonString(new() { WriteIndented = true }), "json");
    }

    [Theory]
    [InlineData("2026-11-09T12:00:00.000000Z")]
    [InlineData("2026-11-09T13:00:00.000000+01:00")]
    public void ReadsMicroTimeOffsetsAndNulls(string timestamp)
    {
        var process = KubernetesJson.Deserialize<V2.LogicalProcessStatus>($$"""{"identityTimestamp":"{{timestamp}}","finishedAt":null,"exitCode":null}""");

        Assert.Equal(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc), process.IdentityTimestamp);
        Assert.Null(process.FinishedAt);
        Assert.Null(process.ExitCode);
    }

    [Fact]
    public async Task V1FactoriesKeepTheirContracts()
    {
        var container = Container.Create("container", "image");
        var executable = Executable.Create("process", "/bin/server");
        var service = Service.Create("service");
        await Verify(new JsonObject
        {
            ["container"] = JsonNode.Parse(KubernetesJson.Serialize(container)),
            ["executable"] = JsonNode.Parse(KubernetesJson.Serialize(executable)),
            ["service"] = JsonNode.Parse(KubernetesJson.Serialize(service))
        }.ToJsonString(new() { WriteIndented = true }), "json");
    }
}
