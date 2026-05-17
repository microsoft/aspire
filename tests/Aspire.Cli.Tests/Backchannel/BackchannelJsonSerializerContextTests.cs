// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Backchannel;

public class BackchannelJsonSerializerContextTests
{
    private static readonly JsonSerializerOptions s_options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
    private const string ResourceSnapshotJson =
        """
        {
          "Name": "frontend",
          "DisplayName": "Frontend",
          "ResourceType": "Project",
          "State": "Running",
          "StateStyle": "success",
          "HealthStatus": "Healthy",
          "ExitCode": 0,
          "CreatedAt": "2024-01-01T00:00:00Z",
          "StartedAt": "2024-01-01T00:00:01Z",
          "StoppedAt": "2024-01-01T00:00:02Z",
          "Urls": [
            {
              "Name": "http",
              "Url": "http://localhost:5000",
              "IsInternal": false,
              "DisplayProperties": {
                "DisplayName": "HTTP",
                "SortOrder": 1
              }
            }
          ],
          "Relationships": [
            {
              "ResourceName": "database",
              "Type": "Reference"
            }
          ],
          "HealthReports": [
            {
              "Name": "live",
              "Status": "Healthy",
              "Description": "OK",
              "ExceptionText": null
            }
          ],
          "Volumes": [
            {
              "Source": "data",
              "Target": "/data",
              "MountType": "volume",
              "IsReadOnly": false
            }
          ],
          "EnvironmentVariables": [
            {
              "Name": "ASPNETCORE_ENVIRONMENT",
              "Value": "Development",
              "IsFromSpec": true
            }
          ],
          "Properties": {
            "pid": "123"
          },
          "IsHidden": false,
          "McpServer": {
            "EndpointUrl": "http://localhost:8000",
            "Tools": [
              {
                "Name": "query",
                "Description": "Runs a SQL query",
                "InputSchema": {
                  "type": "object"
                }
              }
            ]
          },
          "Commands": [
            {
              "Name": "stop",
              "DisplayName": "Stop",
              "Description": "Stops the resource",
              "ArgumentInputs": [
                {
                  "Name": "force",
                  "Label": "Force",
                  "Description": "Force stop",
                  "InputType": "checkbox",
                  "Required": false,
                  "Options": {
                    "true": "True"
                  },
                  "AllowCustomChoice": false,
                  "Disabled": false,
                  "MaxLength": 5
                }
              ],
              "Visibility": "UI",
              "State": "Enabled"
            }
          ]
        }
        """;

    private const string GetResourcesResponseJson =
        $$"""
        {
          "Resources": [
            {{ResourceSnapshotJson}}
          ]
        }
        """;

    [Fact]
    public void JsonSerializerOptionsCanSerializeAndDeserializeResourceSnapshotMcpServers()
    {
        var servers = new Aspire.Cli.Backchannel.ResourceSnapshotMcpServer[]
        {
            new()
            {
                EndpointUrl = "http://localhost:8000",
                Tools =
                [
                    new Tool
                    {
                        Name = "query",
                        Description = "Runs a SQL query",
                        InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"sql\":{\"type\":\"string\"}}}").RootElement
                    }
                ]
            }
        };

        var json = JsonSerializer.Serialize(servers, s_options);
        var roundTripped = JsonSerializer.Deserialize<Aspire.Cli.Backchannel.ResourceSnapshotMcpServer[]>(json, s_options);

        Assert.NotNull(roundTripped);
        Assert.Single(roundTripped);
        Assert.Equal("http://localhost:8000", roundTripped[0].EndpointUrl);
        Assert.Single(roundTripped[0].Tools);
        Assert.Equal("query", roundTripped[0].Tools[0].Name);
    }

    [Fact]
    public void JsonSerializerOptionsCanSerializeAndDeserializeDictionaryStringJsonElement()
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sql"] = JsonDocument.Parse("\"select 1\"").RootElement,
            ["limit"] = JsonDocument.Parse("1").RootElement
        };

        var json = JsonSerializer.Serialize(payload, s_options);
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, s_options);

        Assert.NotNull(roundTripped);
        Assert.Equal("select 1", roundTripped["sql"].GetString());
        Assert.Equal(1, roundTripped["limit"].GetInt32());
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializePublishingActivityWithoutHierarchyMetadata()
    {
        var json =
            """
            {
              "Type": "step",
              "Data": {
                "Id": "step-1",
                "StatusText": "Prepare",
                "CompletionState": "InProgress"
              }
            }
            """;

        var activity = JsonSerializer.Deserialize<PublishingActivity>(json, s_options);

        Assert.NotNull(activity);
        Assert.Equal(PublishingActivityTypes.Step, activity.Type);
        Assert.Equal("step-1", activity.Data.Id);
        Assert.Equal("Prepare", activity.Data.StatusText);
        Assert.Null(activity.Data.ParentStepId);
        Assert.Null(activity.Data.HierarchyLevel);
        Assert.Null(activity.Data.CompletionMessage);
        Assert.Equal(CompletionStates.InProgress, activity.Data.CompletionState);
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializeExplicitNullResourceSnapshotCollectionsAsEmpty()
    {
        var json =
            """
            {
              "Name": "frontend",
              "ResourceType": "Project",
              "Urls": null,
              "Relationships": null,
              "HealthReports": null,
              "Volumes": null,
              "EnvironmentVariables": null,
              "Properties": null,
              "McpServer": {
                "EndpointUrl": "http://localhost:8000",
                "Tools": null
              },
              "Commands": [
                {
                  "Name": "stop",
                  "State": "Enabled",
                  "Visibility": null,
                  "ArgumentInputs": null
                }
              ]
            }
            """;

        var snapshot = JsonSerializer.Deserialize<ResourceSnapshot>(json, s_options);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Urls);
        Assert.Empty(snapshot.Relationships);
        Assert.Empty(snapshot.HealthReports);
        Assert.Empty(snapshot.Volumes);
        Assert.Empty(snapshot.EnvironmentVariables);
        Assert.Empty(snapshot.Properties);
        Assert.NotNull(snapshot.McpServer);
        Assert.Empty(snapshot.McpServer.Tools);
        var command = Assert.Single(snapshot.Commands);
        Assert.Empty(command.ArgumentInputs);
        Assert.Equal(KnownCommandVisibility.Default, command.Visibility);

        var resourceJson = ResourceSnapshotMapper.MapToResourceJson(snapshot, [snapshot]);
        var mappedCommand = Assert.Single(resourceJson.Commands!);
        Assert.Null(mappedCommand.Value.ArgumentInputs);
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializeExplicitNullPipelineStepCollectionsAsEmpty()
    {
        var json =
            """
            {
              "Steps": [
                {
                  "Name": "build",
                  "DependsOn": null,
                  "Tags": null
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize<GetPipelineStepsResponse>(json, s_options);

        Assert.NotNull(response);
        var step = Assert.Single(response.Steps);
        Assert.Empty(step.DependsOn);
        Assert.Empty(step.Tags);

        var nullStepsResponse = JsonSerializer.Deserialize<GetPipelineStepsResponse>("{\"Steps\":null}", s_options);
        Assert.NotNull(nullStepsResponse);
        Assert.Empty(nullStepsResponse.Steps);
    }

    [Fact]
    public void PipelineStepNormalizationSkipsMissingNamesAndNullChildValues()
    {
        var json =
            """
            {
              "Steps": [
                null,
                {
                  "Description": "missing name"
                },
                {
                  "Name": "publish",
                  "DependsOn": [null, "build"],
                  "Tags": [null, "deploy"]
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize<GetPipelineStepsResponse>(json, s_options);

        Assert.NotNull(response);
        var step = Assert.Single(AppHostCliBackchannel.NormalizePipelineSteps(response.Steps, NullLogger.Instance, "test"));
        Assert.Equal("publish", step.Name);
        Assert.Equal(["build"], step.DependsOn);
        Assert.Equal(["deploy"], step.Tags);
    }

    [Fact]
    public void CallMcpToolNormalizationSkipsMalformedContentItems()
    {
        var response = JsonSerializer.Deserialize<CallMcpToolResponse>(
            """
            {
              "IsError": false,
              "Content": [
                null,
                { "Type": null, "Text": "missing type" },
                { "Type": "text", "Text": "ok" }
              ]
            }
            """,
            s_options);

        Assert.NotNull(response);
        var content = Assert.Single(AppHostAuxiliaryBackchannel.NormalizeCallMcpToolResponse(response).Content);
        Assert.Equal("text", content.Type);
        Assert.Equal("ok", content.Text);
    }

    [Theory]
    [InlineData(nameof(GetAppHostInfoResponse.Pid), "GetAppHostInfoAsync.Pid")]
    [InlineData(nameof(GetAppHostInfoResponse.AspireHostVersion), "GetAppHostInfoAsync.AspireHostVersion")]
    [InlineData(nameof(GetAppHostInfoResponse.AppHostPath), "GetAppHostInfoAsync.AppHostPath")]
    public void AppHostAuxiliaryBackchannelRejectsNullAppHostInfoResponseRequiredMembers(string path, string expectedMemberName)
    {
        var response = DeserializeMutated<GetAppHostInfoResponse>(
            """
            {
              "Pid": "123",
              "AspireHostVersion": "13.0.0",
              "AppHostPath": "/workspace/AppHost.csproj"
            }
            """,
            path,
            JsonMutation.SetNull);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AppHostAuxiliaryBackchannel.RequireGetAppHostInfoResponse(response, "GetAppHostInfoAsync"));

        Assert.Contains(expectedMemberName, exception.Message);
    }

    [Fact]
    public void AppHostAuxiliaryBackchannelRejectsNullLegacyAppHostInformationPath()
    {
        var appHostInfo = DeserializeMutated<AppHostInformation>(
            """
            {
              "AppHostPath": "/workspace/AppHost.csproj",
              "ProcessId": 123
            }
            """,
            nameof(AppHostInformation.AppHostPath),
            JsonMutation.SetNull);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AppHostAuxiliaryBackchannel.ValidateAppHostInformation(appHostInfo, "GetAppHostInformationAsync"));

        Assert.Contains("GetAppHostInformationAsync.AppHostPath", exception.Message);
    }

    [Fact]
    public void AppHostAuxiliaryBackchannelRejectsNullOperationResponses()
    {
        var executeException = Assert.Throws<InvalidOperationException>(
            () => AppHostAuxiliaryBackchannel.RequireExecuteResourceCommandResponse(null, "ExecuteResourceCommandAsync"));
        Assert.Contains("ExecuteResourceCommandAsync", executeException.Message);

        var waitException = Assert.Throws<InvalidOperationException>(
            () => AppHostAuxiliaryBackchannel.RequireWaitForResourceResponse(null, "WaitForResourceAsync"));
        Assert.Contains("WaitForResourceAsync", waitException.Message);
    }

    [Fact]
    public void ExecuteResourceCommandResponseNormalizationSkipsMalformedValidationErrors()
    {
        var response = JsonSerializer.Deserialize<ExecuteResourceCommandResponse>(
            """
            {
              "Success": false,
              "ValidationErrors": [
                null,
                {
                  "ArgumentName": null,
                  "ErrorMessage": null
                },
                {
                  "ArgumentName": "selector",
                  "ErrorMessage": "Required."
                }
              ]
            }
            """,
            s_options);

        var validationError = Assert.Single(AppHostAuxiliaryBackchannel.RequireExecuteResourceCommandResponse(response, "ExecuteResourceCommandAsync").ValidationErrors);
        Assert.Equal("selector", validationError.ArgumentName);
        Assert.Equal("Required.", validationError.ErrorMessage);
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializeExplicitNullRpcResourceStateEndpointsAsEmpty()
    {
        var json =
            """
            {
              "Resource": "frontend",
              "Type": "Project",
              "State": "Running",
              "Endpoints": null
            }
            """;

        var state = JsonSerializer.Deserialize<RpcResourceState>(json, s_options);

        Assert.NotNull(state);
        Assert.Empty(state.Endpoints);
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializeExplicitNullDashboardUrlsAsNull()
    {
        var json =
            """
            {
              "ApiBaseUrl": "http://localhost:18888",
              "ApiToken": "token",
              "DashboardUrls": null,
              "IsHealthy": true
            }
            """;

        var dashboardInfo = JsonSerializer.Deserialize<GetDashboardInfoResponse>(json, s_options);

        Assert.NotNull(dashboardInfo);
        Assert.Null(dashboardInfo.DashboardUrls?.FirstOrDefault());
    }

    [Fact]
    public void ExtensionBackchannelNormalizesCapabilityArrays()
    {
        var capabilities = ExtensionBackchannel.NormalizeCapabilities([null!, "", "baseline.v1"]);

        Assert.Equal(["baseline.v1"], capabilities);
    }

    [Fact]
    public void ExtensionBackchannelRejectsMalformedOutgoingDisplayLines()
    {
        Assert.Throws<ArgumentNullException>(() => new DisplayLineState("stdout", null!));

        var exception = Assert.Throws<InvalidOperationException>(
            () => ExtensionBackchannel.NormalizeDisplayLines([new DisplayLineState("", "line")]));

        Assert.Contains("displayLines", exception.Message);
    }

    [Fact]
    public void ExtensionBackchannelRejectsMalformedOutgoingEnvironment()
    {
        var missingName = Assert.Throws<InvalidOperationException>(
            () => ExtensionBackchannel.NormalizeEnvironment([new EnvVar { Name = null, Value = "value" }]));
        Assert.Contains("no name", missingName.Message);

        var missingValue = Assert.Throws<InvalidOperationException>(
            () => ExtensionBackchannel.NormalizeEnvironment([new EnvVar { Name = "ASPIRE_ENV", Value = null }]));
        Assert.Contains("ASPIRE_ENV", missingValue.Message);
    }

    [Fact]
    public void ExtensionBackchannelRejectsMalformedDebugSessionArgs()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ExtensionBackchannel.NormalizeDebugSessionOptions(new DebugSessionOptions
            {
                Command = "run",
                Args = [null!]
            }));

        Assert.Contains("startDebugSession", exception.Message);
    }

    [Theory]
    [MemberData(nameof(BackchannelBoundaryTypes))]
    public void JsonSerializerOptionsIncludeBackchannelBoundaryType(Type type)
    {
        var typeInfo = s_options.GetTypeInfo(type);

        Assert.NotNull(typeInfo);
    }

    [Fact]
    public void BackchannelBoundaryTypesIncludeNestedPayloadTypes()
    {
        var boundaryTypeSet = s_backchannelBoundaryTypes.ToHashSet();
        var errors = new List<string>();

        foreach (var type in s_backchannelBoundaryTypes)
        {
            foreach (var referencedType in EnumerateBackchannelPayloadTypes(type))
            {
                if (!boundaryTypeSet.Contains(referencedType))
                {
                    errors.Add($"{type.Name}: Referenced backchannel payload type '{referencedType.Name}' must be added to {nameof(BackchannelBoundaryTypes)} so source-generated JSON metadata is covered.");
                }
            }
        }

        Assert.Empty(errors);
    }

    [Fact]
    public void BackchannelBoundaryTypesIncludeRequestAndResponseTypes()
    {
        var boundaryTypeSet = s_backchannelBoundaryTypes.ToHashSet();
        var missingTypes = typeof(GetCapabilitiesRequest).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(GetCapabilitiesRequest).Namespace)
            .Where(static type => type.IsClass && type.IsSealed)
            .Where(static type => type.Name.EndsWith("Request", StringComparison.Ordinal) || type.Name.EndsWith("Response", StringComparison.Ordinal))
            .Where(type => !boundaryTypeSet.Contains(type))
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missingTypes);
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializeOldResourceCommandResponseShape()
    {
        var json =
            """
            {
              "ErrorMessage": "command failed",
              "ValidationErrors": null,
              "Value": {
                "Format": "text",
                "DisplayImmediately": false
              }
            }
            """;

        var response = JsonSerializer.Deserialize<ExecuteResourceCommandResponse>(json, s_options);

        Assert.NotNull(response);
        Assert.False(response.Success);
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal("command failed", response.ErrorMessage);
#pragma warning restore CS0618 // Type or member is obsolete
        Assert.Empty(response.ValidationErrors);
        Assert.NotNull(response.Value);
        Assert.Null(response.Value.Value);
        Assert.Equal(CommandResultFormat.Text, response.Value.Format);
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializeOldAppHostWireFixtures()
    {
        var capabilities = JsonSerializer.Deserialize<GetCapabilitiesResponse>("{}", s_options);
        Assert.NotNull(capabilities);
        Assert.Empty(capabilities.Capabilities);

        var resources = JsonSerializer.Deserialize<GetResourcesResponse>(
            """
            {
              "Resources": [
                {
                  "Name": "api",
                  "ResourceType": "Project",
                  "State": "Running"
                }
              ]
            }
            """,
            s_options);

        Assert.NotNull(resources);
        var resource = Assert.Single(resources.Resources);
        Assert.Equal("api", resource.Name);
        Assert.Empty(resource.Urls);
        Assert.Empty(resource.Commands);

        var pipelineSteps = JsonSerializer.Deserialize<GetPipelineStepsResponse>(
            """
            {
              "Steps": [
                {
                  "Name": "publish",
                  "Description": "Publish"
                }
              ]
            }
            """,
            s_options);

        Assert.NotNull(pipelineSteps);
        var step = Assert.Single(pipelineSteps.Steps);
        Assert.Equal("publish", step.Name);
        Assert.Empty(step.DependsOn);
        Assert.Empty(step.Tags);

        var waitResponse = JsonSerializer.Deserialize<WaitForResourceResponse>("{}", s_options);
        Assert.NotNull(waitResponse);
        Assert.False(waitResponse.Success);
        Assert.Null(waitResponse.State);
    }

    [Theory]
    [InlineData("future-format")]
    [InlineData(null)]
    public void JsonSerializerOptionsCanDeserializeUnknownCommandResultFormatAsNone(string? format)
    {
        var json = $$"""
            {
              "Success": true,
              "Value": {
                "Value": "value",
                "Format": {{JsonSerializer.Serialize(format)}}
              }
            }
            """;

        var response = JsonSerializer.Deserialize<ExecuteResourceCommandResponse>(json, s_options);

        Assert.NotNull(response);
        Assert.NotNull(response.Value);
        Assert.Equal(CommandResultFormat.None, response.Value.Format);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(99, 0)]
    public void JsonSerializerOptionsPreserveLegacyNumericCommandResultFormat(int format, int expectedFormat)
    {
        var response = JsonSerializer.Deserialize<ExecuteResourceCommandResponse>(
            $$"""
            {
              "Success": true,
              "Value": {
                "Value": "value",
                "Format": {{format}}
              }
            }
            """,
            s_options);

        Assert.NotNull(response);
        Assert.NotNull(response.Value);
        Assert.Equal((CommandResultFormat)expectedFormat, response.Value.Format);
    }

    [Fact]
    public void JsonSerializerOptionsDefaultMissingCommandResultFormatAsNone()
    {
        var response = JsonSerializer.Deserialize<ExecuteResourceCommandResponse>(
            """
            {
              "Success": true,
              "Value": {
                "Value": "value"
              }
            }
            """,
            s_options);

        Assert.NotNull(response);
        Assert.NotNull(response.Value);
        Assert.Equal(CommandResultFormat.None, response.Value.Format);
    }

    [Fact]
    public void JsonSerializerOptionsPreserveExplicitEmptyStreamingText()
    {
        var appHostLogEntry = JsonSerializer.Deserialize<BackchannelLogEntry>(
            """
            {
              "EventId": {
                "Id": 0,
                "Name": null
              },
              "LogLevel": 2,
              "Message": "",
              "Timestamp": "2024-01-01T00:00:00Z",
              "CategoryName": "Aspire.AppHost"
            }
            """,
            s_options);

        Assert.NotNull(appHostLogEntry);
        Assert.True(appHostLogEntry.HasMessage);

        var resourceLogLine = JsonSerializer.Deserialize<ResourceLogLine>(
            """
            {
              "ResourceName": "api",
              "LineNumber": 1,
              "Content": "",
              "IsError": false
            }
            """,
            s_options);

        Assert.NotNull(resourceLogLine);
        Assert.True(resourceLogLine.HasContent);
    }

    [Theory]
    [InlineData(nameof(BackchannelLogEntry.Message))]
    [InlineData(nameof(BackchannelLogEntry.CategoryName))]
    public void AppHostCliBackchannelSkipsMalformedLogEntries(string path)
    {
        foreach (var mutation in Enum.GetValues<JsonMutation>())
        {
            var entry = DeserializeMutated<BackchannelLogEntry>(
                """
                {
                  "EventId": {
                    "Id": 0,
                    "Name": null
                  },
                  "LogLevel": 2,
                  "Message": "hello",
                  "Timestamp": "2024-01-01T00:00:00Z",
                  "CategoryName": "Aspire.AppHost"
                }
                """,
                path,
                mutation);

            Assert.False(AppHostCliBackchannel.TryValidateLogEntry(entry, NullLogger.Instance, "GetAppHostLogEntriesAsync"));
        }
    }

    [Theory]
    [InlineData(nameof(ResourceLogLine.ResourceName))]
    [InlineData(nameof(ResourceLogLine.Content))]
    public void AppHostAuxiliaryBackchannelSkipsMalformedResourceLogLines(string path)
    {
        foreach (var mutation in Enum.GetValues<JsonMutation>())
        {
            var line = DeserializeMutated<ResourceLogLine>(
                """
                {
                  "ResourceName": "api",
                  "LineNumber": 1,
                  "Content": "hello",
                  "IsError": false
                }
                """,
                path,
                mutation);

            Assert.False(AppHostAuxiliaryBackchannel.TryValidateResourceLogLine(line, NullLogger.Instance, "GetConsoleLogsAsync"));
        }
    }

    [Theory]
    [MemberData(nameof(DefaultedBackchannelOutputMembers))]
    public void JsonSerializerOptionsDefaultNullAndMissingBackchannelOutputMembers(BackchannelPayloadCase testCase)
    {
        var nullPayload = DeserializeMutated(testCase.Type, testCase.Json, testCase.Path, JsonMutation.SetNull);
        AssertDefaultedMember(nullPayload, testCase.Path);

        var missingPayload = DeserializeMutated(testCase.Type, testCase.Json, testCase.Path, JsonMutation.Omit);
        AssertDefaultedMember(missingPayload, testCase.Path);
    }

    [Theory]
    [InlineData(JsonMutation.SetNull)]
    [InlineData(JsonMutation.Omit)]
    public void JsonSerializerOptionsKeepDashboardUrlsNullWhenWireSendsNullOrOmitsIt(JsonMutation mutation)
    {
        var dashboardInfo = DeserializeMutated<GetDashboardInfoResponse>(
            """
            {
              "ApiBaseUrl": "http://localhost:18888",
              "ApiToken": "token",
              "DashboardUrls": [
                "http://localhost:18888/login?t=token"
              ],
              "IsHealthy": true
            }
            """,
            nameof(GetDashboardInfoResponse.DashboardUrls),
            mutation);

        Assert.Null(dashboardInfo.DashboardUrls);
    }

    [Theory]
    [MemberData(nameof(MalformedPublishingActivities))]
    public void AppHostCliBackchannelRejectsExplicitNullPublishingActivityMembers(string json, string expectedMemberName)
    {
        var activity = JsonSerializer.Deserialize<PublishingActivity>(json, s_options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AppHostCliBackchannel.RequirePublishingActivity(activity, "GetPublishingActivitiesAsync"));

        Assert.Contains(expectedMemberName, exception.Message);
    }

    [Fact]
    public void PublishingPromptNormalizationSkipsMalformedOptionalItems()
    {
        var activity = JsonSerializer.Deserialize<PublishingActivity>(
            """
            {
              "Type": "prompt",
              "Data": {
                "Id": "prompt-1",
                "StatusText": "Configure",
                "Inputs": [
                  null,
                  {
                    "Name": "region",
                    "Label": "Region",
                    "InputType": "Choice",
                    "Options": [
                      null,
                      { "Key": null, "Value": "Missing key" },
                      { "Key": "missing-value", "Value": null },
                      { "Key": "westus", "Value": "West US" }
                    ],
                    "ValidationErrors": [
                      null,
                      "",
                      "Region is required."
                    ]
                  }
                ]
              }
            }
            """,
            s_options);

        Assert.NotNull(activity);
        var input = Assert.Single(PipelineCommandBase.NormalizePromptInputs(activity.Data.Inputs));
        var option = Assert.Single(PipelineCommandBase.NormalizePromptOptions(input.Options));
        Assert.Equal("westus", option.Key);
        Assert.Equal("West US", option.Value);
        Assert.Equal(["Region is required."], PipelineCommandBase.NormalizeValidationErrors(input.ValidationErrors));
    }

    [Fact]
    public void PublishingPipelineSummaryNormalizationSkipsMalformedOptionalItems()
    {
        var activity = JsonSerializer.Deserialize<PublishingActivity>(
            """
            {
              "Type": "publish-complete",
              "Data": {
                "Id": "publish",
                "StatusText": "Published",
                "PipelineSummary": [
                  null,
                  { "Key": null, "Value": "Missing key" },
                  { "Key": "Endpoint", "Value": null },
                  { "Key": "Endpoint", "Value": "https://localhost:5001" }
                ]
              }
            }
            """,
            s_options);

        Assert.NotNull(activity);
        var summary = Assert.Single(PipelineCommandBase.NormalizePipelineSummary(activity.Data.PipelineSummary));
        Assert.Equal("Endpoint", summary.Key);
        Assert.Equal("https://localhost:5001", summary.Value);
    }

    [Theory]
    [MemberData(nameof(MalformedRequiredStreamPayloadMembers))]
    public void AppHostCliBackchannelRejectsNullOrMissingRequiredStreamPayloadMembers(RequiredStreamPayloadCase testCase)
    {
        foreach (var mutation in Enum.GetValues<JsonMutation>())
        {
            var payload = DeserializeMutated(testCase.Type, testCase.Json, testCase.Path, mutation);

            var exception = Assert.Throws<InvalidOperationException>(() => RequireStreamPayload(payload, testCase));

            Assert.Contains(testCase.ExpectedMemberName, exception.Message);
        }
    }

    public static TheoryData<BackchannelPayloadCase> DefaultedBackchannelOutputMembers => new()
    {
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, nameof(ResourceSnapshot.Urls)),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, nameof(ResourceSnapshot.Relationships)),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, nameof(ResourceSnapshot.HealthReports)),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, nameof(ResourceSnapshot.Volumes)),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, nameof(ResourceSnapshot.EnvironmentVariables)),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, nameof(ResourceSnapshot.Properties)),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, nameof(ResourceSnapshot.Commands)),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, $"{nameof(ResourceSnapshot.McpServer)}.{nameof(ResourceSnapshotMcpServer.Tools)}"),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, $"{nameof(ResourceSnapshot.Commands)}[0].{nameof(ResourceSnapshotCommand.ArgumentInputs)}"),
        new BackchannelPayloadCase(nameof(ResourceSnapshot), typeof(ResourceSnapshot), ResourceSnapshotJson, $"{nameof(ResourceSnapshot.Commands)}[0].{nameof(ResourceSnapshotCommand.Visibility)}"),
        new BackchannelPayloadCase(nameof(GetCapabilitiesResponse), typeof(GetCapabilitiesResponse), """{"Capabilities":["aux.v1","aux.v2"]}""", nameof(GetCapabilitiesResponse.Capabilities)),
        new BackchannelPayloadCase(nameof(GetResourcesResponse), typeof(GetResourcesResponse), GetResourcesResponseJson, nameof(GetResourcesResponse.Resources)),
        new BackchannelPayloadCase(nameof(GetResourcesResponse), typeof(GetResourcesResponse), GetResourcesResponseJson, $"{nameof(GetResourcesResponse.Resources)}[0].{nameof(ResourceSnapshot.Urls)}"),
        new BackchannelPayloadCase(nameof(CallMcpToolResponse), typeof(CallMcpToolResponse), """{"IsError":false,"Content":[{"Type":"text","Text":"hello"}]}""", nameof(CallMcpToolResponse.Content)),
        new BackchannelPayloadCase(nameof(ExecuteResourceCommandResponse), typeof(ExecuteResourceCommandResponse), """{"Success":false,"ValidationErrors":[{"ArgumentName":"name","ErrorMessage":"required"}]}""", nameof(ExecuteResourceCommandResponse.ValidationErrors)),
        new BackchannelPayloadCase(nameof(RpcResourceState), typeof(RpcResourceState), """{"Resource":"frontend","Type":"Project","State":"Running","Endpoints":["http://localhost:5000"],"Health":"Healthy"}""", nameof(RpcResourceState.Endpoints)),
        new BackchannelPayloadCase(nameof(GetPipelineStepsResponse), typeof(GetPipelineStepsResponse), """{"Steps":[{"Name":"build","Description":"Builds","DependsOn":["restore"],"Tags":["build"],"ResourceName":"api"}]}""", nameof(GetPipelineStepsResponse.Steps)),
        new BackchannelPayloadCase(nameof(GetPipelineStepsResponse), typeof(GetPipelineStepsResponse), """{"Steps":[{"Name":"build","Description":"Builds","DependsOn":["restore"],"Tags":["build"],"ResourceName":"api"}]}""", $"{nameof(GetPipelineStepsResponse.Steps)}[0].{nameof(PipelineStepInfo.DependsOn)}"),
        new BackchannelPayloadCase(nameof(GetPipelineStepsResponse), typeof(GetPipelineStepsResponse), """{"Steps":[{"Name":"build","Description":"Builds","DependsOn":["restore"],"Tags":["build"],"ResourceName":"api"}]}""", $"{nameof(GetPipelineStepsResponse.Steps)}[0].{nameof(PipelineStepInfo.Tags)}"),
        new BackchannelPayloadCase(nameof(PublishingActivity), typeof(PublishingActivity), """{"Type":"step","Data":{"Id":"step-1","StatusText":"Prepare","CompletionState":"Completed"}}""", $"{nameof(PublishingActivity.Data)}.{nameof(PublishingActivityData.CompletionState)}"),
    };

    private static readonly Type[] s_backchannelBoundaryTypes =
    [
        typeof(BackchannelTraceContext),
        typeof(GetCapabilitiesRequest),
        typeof(GetCapabilitiesResponse),
        typeof(GetAppHostInfoRequest),
        typeof(GetAppHostInfoResponse),
        typeof(GetDashboardInfoRequest),
        typeof(GetDashboardInfoResponse),
        typeof(GetResourcesRequest),
        typeof(GetResourcesResponse),
        typeof(WatchResourcesRequest),
        typeof(GetConsoleLogsRequest),
        typeof(CallMcpToolRequest),
        typeof(CallMcpToolResponse),
        typeof(McpToolContentItem),
        typeof(StopAppHostRequest),
        typeof(StopAppHostResponse),
        typeof(ExecuteResourceCommandRequest),
        typeof(ExecuteResourceCommandResponse),
        typeof(ResourceCommandArgumentValidationError),
        typeof(ResourceCommandArgumentValidationError[]),
        typeof(ExecuteResourceCommandResult),
        typeof(WaitForResourceRequest),
        typeof(WaitForResourceResponse),
        typeof(RpcResourceState),
        typeof(DashboardUrlsState),
        typeof(PublishingActivity),
        typeof(PublishingActivityData),
        typeof(BackchannelPipelineSummaryItem),
        typeof(PublishingPromptInput),
        typeof(PublishingPromptInputAnswer),
        typeof(PublishingPromptInputAnswer[]),
        typeof(BackchannelLogEntry),
        typeof(PipelineStepInfo),
        typeof(PipelineStepInfo[]),
        typeof(GetPipelineStepsRequest),
        typeof(GetPipelineStepsResponse),
        typeof(DashboardMcpConnectionInfo),
        typeof(ResourceSnapshot),
        typeof(ResourceSnapshot[]),
        typeof(ResourceSnapshotCommand),
        typeof(ResourceSnapshotCommandArgument),
        typeof(ResourceSnapshotUrl),
        typeof(ResourceSnapshotUrlDisplayProperties),
        typeof(ResourceSnapshotRelationship),
        typeof(ResourceSnapshotHealthReport),
        typeof(ResourceSnapshotVolume),
        typeof(ResourceSnapshotEnvironmentVariable),
        typeof(ResourceSnapshotMcpServer),
        typeof(AppHostInformation),
        typeof(ResourceLogLine),
        typeof(ResourceLogLine[]),
        typeof(CommandResultFormat),
    ];

    public static TheoryData<Type> BackchannelBoundaryTypes => new(s_backchannelBoundaryTypes);

    public static TheoryData<string, string> MalformedPublishingActivities => new()
    {
        { """{"Type":null,"Data":{"Id":"step-1","StatusText":"Prepare"}}""", "GetPublishingActivitiesAsync.Type" },
        { """{"Type":"step","Data":null}""", "GetPublishingActivitiesAsync.Data" },
        { """{"Type":"step","Data":{"Id":null,"StatusText":"Prepare"}}""", "GetPublishingActivitiesAsync.Data.Id" },
        { """{"Type":"step","Data":{"Id":"step-1","StatusText":null}}""", "GetPublishingActivitiesAsync.Data.StatusText" }
    };

    public static TheoryData<RequiredStreamPayloadCase> MalformedRequiredStreamPayloadMembers => new()
    {
        new RequiredStreamPayloadCase(StreamPayloadKind.PublishingActivity, typeof(PublishingActivity), """{"Type":"step","Data":{"Id":"step-1","StatusText":"Prepare"}}""", nameof(PublishingActivity.Type), "GetPublishingActivitiesAsync.Type"),
        new RequiredStreamPayloadCase(StreamPayloadKind.PublishingActivity, typeof(PublishingActivity), """{"Type":"step","Data":{"Id":"step-1","StatusText":"Prepare"}}""", nameof(PublishingActivity.Data), "GetPublishingActivitiesAsync.Data"),
        new RequiredStreamPayloadCase(StreamPayloadKind.PublishingActivity, typeof(PublishingActivity), """{"Type":"step","Data":{"Id":"step-1","StatusText":"Prepare"}}""", $"{nameof(PublishingActivity.Data)}.{nameof(PublishingActivityData.Id)}", "GetPublishingActivitiesAsync.Data.Id"),
        new RequiredStreamPayloadCase(StreamPayloadKind.PublishingActivity, typeof(PublishingActivity), """{"Type":"step","Data":{"Id":"step-1","StatusText":"Prepare"}}""", $"{nameof(PublishingActivity.Data)}.{nameof(PublishingActivityData.StatusText)}", "GetPublishingActivitiesAsync.Data.StatusText"),
        new RequiredStreamPayloadCase(StreamPayloadKind.ResourceState, typeof(RpcResourceState), """{"Resource":"frontend","Type":"Project","State":"Running","Endpoints":["http://localhost:5000"]}""", nameof(RpcResourceState.Resource), "GetResourceStatesAsync.Resource"),
        new RequiredStreamPayloadCase(StreamPayloadKind.ResourceState, typeof(RpcResourceState), """{"Resource":"frontend","Type":"Project","State":"Running","Endpoints":["http://localhost:5000"]}""", nameof(RpcResourceState.Type), "GetResourceStatesAsync.Type"),
        new RequiredStreamPayloadCase(StreamPayloadKind.ResourceState, typeof(RpcResourceState), """{"Resource":"frontend","Type":"Project","State":"Running","Endpoints":["http://localhost:5000"]}""", nameof(RpcResourceState.State), "GetResourceStatesAsync.State"),
    };

    private static object DeserializeMutated(Type type, string json, string path, JsonMutation mutation)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Test payload was not valid JSON.");
        Mutate(node, path, mutation);
        return JsonSerializer.Deserialize(node.ToJsonString(), type, s_options) ?? throw new InvalidOperationException($"Deserializing {type.Name} returned null.");
    }

    private static T DeserializeMutated<T>(string json, string path, JsonMutation mutation)
        where T : class
    {
        return Assert.IsType<T>(DeserializeMutated(typeof(T), json, path, mutation));
    }

    private static void Mutate(JsonNode node, string path, JsonMutation mutation)
    {
        var segments = path.Split('.');
        var current = node;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = GetChild(current, segments[i]);
        }

        ApplyMutation(current, segments[^1], mutation);
    }

    private static JsonNode GetChild(JsonNode node, string segment)
    {
        var (propertyName, index) = ParseSegment(segment);
        var child = node.AsObject()[propertyName] ?? throw new InvalidOperationException($"Could not find JSON property '{propertyName}'.");
        return index is null
            ? child
            : child.AsArray()[index.GetValueOrDefault()] ?? throw new InvalidOperationException($"Could not find JSON array element '{segment}'.");
    }

    private static void ApplyMutation(JsonNode node, string segment, JsonMutation mutation)
    {
        var (propertyName, index) = ParseSegment(segment);
        if (index is null)
        {
            var obj = node.AsObject();
            if (mutation is JsonMutation.SetNull)
            {
                obj[propertyName] = null;
            }
            else
            {
                Assert.True(obj.Remove(propertyName), $"Could not remove JSON property '{propertyName}'.");
            }
            return;
        }

        var array = (node.AsObject()[propertyName] ?? throw new InvalidOperationException($"Could not find JSON property '{propertyName}'.")).AsArray();
        if (mutation is JsonMutation.SetNull)
        {
            array[index.GetValueOrDefault()] = null;
        }
        else
        {
            array.RemoveAt(index.GetValueOrDefault());
        }
    }

    private static (string PropertyName, int? Index) ParseSegment(string segment)
    {
        var bracketIndex = segment.IndexOf('[');
        if (bracketIndex < 0)
        {
            return (segment, null);
        }

        var endBracketIndex = segment.IndexOf(']', bracketIndex);
        Assert.True(endBracketIndex > bracketIndex, $"Invalid JSON path segment '{segment}'.");

        return (segment[..bracketIndex], int.Parse(segment[(bracketIndex + 1)..endBracketIndex], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IEnumerable<Type> EnumerateBackchannelPayloadTypes(Type type)
    {
        return EnumeratePayloadTypes(type)
            .SelectMany(payloadType => payloadType.GetProperties().SelectMany(property => EnumeratePayloadTypes(property.PropertyType)))
            .Where(IsBackchannelPayloadType);
    }

    private static IEnumerable<Type> EnumeratePayloadTypes(Type type)
    {
        if (type.IsArray)
        {
            yield return type.GetElementType()!;
            yield break;
        }

        var nullableType = Nullable.GetUnderlyingType(type);
        if (nullableType is not null)
        {
            yield return nullableType;
            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var payloadType in EnumeratePayloadTypes(argument))
                {
                    yield return payloadType;
                }
            }
            yield break;
        }

        yield return type;
    }

    private static bool IsBackchannelPayloadType(Type type)
    {
        return type.Namespace == typeof(GetCapabilitiesRequest).Namespace &&
            !type.IsPrimitive &&
            type != typeof(string);
    }

    private static void AssertDefaultedMember(object payload, string path)
    {
        switch (payload)
        {
            case ResourceSnapshot snapshot:
                AssertDefaultedResourceSnapshotMember(snapshot, path);
                break;
            case GetResourcesResponse response when path is nameof(GetResourcesResponse.Resources):
                Assert.Empty(response.Resources);
                break;
            case GetCapabilitiesResponse response:
                Assert.Equal(nameof(GetCapabilitiesResponse.Capabilities), path);
                Assert.Empty(response.Capabilities);
                break;
            case GetResourcesResponse response when path.StartsWith($"{nameof(GetResourcesResponse.Resources)}[0].", StringComparison.Ordinal):
                AssertDefaultedResourceSnapshotMember(Assert.Single(response.Resources), path[($"{nameof(GetResourcesResponse.Resources)}[0].").Length..]);
                break;
            case ExecuteResourceCommandResponse response:
                Assert.Equal(nameof(ExecuteResourceCommandResponse.ValidationErrors), path);
                Assert.Empty(response.ValidationErrors);
                break;
            case CallMcpToolResponse response:
                Assert.Equal(nameof(CallMcpToolResponse.Content), path);
                Assert.Empty(response.Content);
                break;
            case RpcResourceState state:
                Assert.Equal(nameof(RpcResourceState.Endpoints), path);
                Assert.Empty(state.Endpoints);
                break;
            case GetPipelineStepsResponse response when path is nameof(GetPipelineStepsResponse.Steps):
                Assert.Empty(response.Steps);
                break;
            case GetPipelineStepsResponse response when path == $"{nameof(GetPipelineStepsResponse.Steps)}[0].{nameof(PipelineStepInfo.DependsOn)}":
                Assert.Empty(Assert.Single(response.Steps).DependsOn);
                break;
            case GetPipelineStepsResponse response when path == $"{nameof(GetPipelineStepsResponse.Steps)}[0].{nameof(PipelineStepInfo.Tags)}":
                Assert.Empty(Assert.Single(response.Steps).Tags);
                break;
            case PublishingActivity activity:
                Assert.Equal($"{nameof(PublishingActivity.Data)}.{nameof(PublishingActivityData.CompletionState)}", path);
                Assert.Equal(CompletionStates.InProgress, activity.Data.CompletionState);
                break;
            default:
                throw new InvalidOperationException($"No assertion registered for {payload.GetType().Name}.{path}.");
        }
    }

    private static void AssertDefaultedResourceSnapshotMember(ResourceSnapshot snapshot, string path)
    {
        switch (path)
        {
            case nameof(ResourceSnapshot.Urls):
                Assert.Empty(snapshot.Urls);
                break;
            case nameof(ResourceSnapshot.Relationships):
                Assert.Empty(snapshot.Relationships);
                break;
            case nameof(ResourceSnapshot.HealthReports):
                Assert.Empty(snapshot.HealthReports);
                break;
            case nameof(ResourceSnapshot.Volumes):
                Assert.Empty(snapshot.Volumes);
                break;
            case nameof(ResourceSnapshot.EnvironmentVariables):
                Assert.Empty(snapshot.EnvironmentVariables);
                break;
            case nameof(ResourceSnapshot.Properties):
                Assert.Empty(snapshot.Properties);
                break;
            case nameof(ResourceSnapshot.Commands):
                Assert.Empty(snapshot.Commands);
                break;
            case $"{nameof(ResourceSnapshot.McpServer)}.{nameof(ResourceSnapshotMcpServer.Tools)}":
                Assert.NotNull(snapshot.McpServer);
                Assert.Empty(snapshot.McpServer.Tools);
                break;
            case $"{nameof(ResourceSnapshot.Commands)}[0].{nameof(ResourceSnapshotCommand.ArgumentInputs)}":
                Assert.Empty(Assert.Single(snapshot.Commands).ArgumentInputs);
                break;
            case $"{nameof(ResourceSnapshot.Commands)}[0].{nameof(ResourceSnapshotCommand.Visibility)}":
                Assert.Equal(KnownCommandVisibility.Default, Assert.Single(snapshot.Commands).Visibility);
                break;
            default:
                throw new InvalidOperationException($"No assertion registered for ResourceSnapshot.{path}.");
        }
    }

    private static void RequireStreamPayload(object payload, RequiredStreamPayloadCase testCase)
    {
        switch (testCase.Kind)
        {
            case StreamPayloadKind.PublishingActivity:
                AppHostCliBackchannel.RequirePublishingActivity(Assert.IsType<PublishingActivity>(payload), "GetPublishingActivitiesAsync");
                break;
            case StreamPayloadKind.ResourceState:
                AppHostCliBackchannel.RequireResourceState(Assert.IsType<RpcResourceState>(payload), "GetResourceStatesAsync");
                break;
            default:
                throw new InvalidOperationException($"Unknown stream payload kind '{testCase.Kind}'.");
        }
    }

    public sealed record BackchannelPayloadCase(string Name, Type Type, string Json, string Path)
    {
        public override string ToString() => $"{Name}.{Path}";
    }

    public sealed record RequiredStreamPayloadCase(StreamPayloadKind Kind, Type Type, string Json, string Path, string ExpectedMemberName)
    {
        public override string ToString() => $"{Kind}.{Path}";
    }

    public enum StreamPayloadKind
    {
        PublishingActivity,
        ResourceState
    }

    public enum JsonMutation
    {
        SetNull,
        Omit
    }
}
