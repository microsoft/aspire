// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.GenAI;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using Microsoft.OpenApi;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Model;

public sealed class GenAIVisualizerDialogViewModelTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_NoGenAIAttributes_NoMessages()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.Items);
        Assert.Null(vm.ModelName);
        Assert.Null(vm.InputTokens);
        Assert.Null(vm.OutputTokens);
    }

    [Fact]
    public void Create_SpanError_HasErrorItem()
    {
        // Arrange
        var repository = CreateRepository();

        var status = new Status
        {
            Code = Status.Types.StatusCode.Error,
            Message = "Error!"
        };
        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create("error.type", "Exception")
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes, status: status)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Items,
            i =>
            {
                Assert.Equal(GenAIItemType.Error, i.Type);
                Assert.Collection(i.ItemParts,
                    p => Assert.Equal("""
                        Exception

                        Error!
                        """, p.ErrorMessage));
            });
        Assert.False(vm.NoMessageContent);
    }

    [Fact]
    public void Create_GenAILogEntries_HasMessages()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"), KeyValuePair.Create("server.address", "ai-server.address")])
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords =
                        {
                            CreateLogRecord(
                                time: s_testTime,
                                traceId: "1",
                                spanId: "1-1",
                                message: JsonSerializer.Serialize(new SystemOrUserEvent { Content = "System!" }, GenAIEventsContext.Default.SystemOrUserEvent),
                                attributes: [KeyValuePair.Create("event.name", "gen_ai.system.message")]),
                            CreateLogRecord(
                                time: s_testTime.AddSeconds(1),
                                traceId: "1",
                                spanId: "1-1",
                                message: JsonSerializer.Serialize(new SystemOrUserEvent { Content = "User!" }, GenAIEventsContext.Default.SystemOrUserEvent),
                                attributes: [KeyValuePair.Create("event.name", "gen_ai.user.message")]),
                            CreateLogRecord(
                                time: s_testTime.AddSeconds(2),
                                traceId: "1",
                                spanId: "1-1",
                                message: JsonSerializer.Serialize(new AssistantEvent { Content = "Assistant!" }, GenAIEventsContext.Default.AssistantEvent),
                                attributes: [KeyValuePair.Create("event.name", "gen_ai.assistant.message")])
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("System!", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("User!", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.AssistantMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Assistant!", Assert.IsType<TextPart>(p.MessagePart).Content));
            });
        Assert.Null(vm.ModelName);
        Assert.Null(vm.InputTokens);
        Assert.Null(vm.OutputTokens);
    }

    [Fact]
    public void Create_GenAILogEntries_EmptyMessage_NoMessagesCreated()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"), KeyValuePair.Create("server.address", "ai-server.address")])
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords =
                        {
                            CreateLogRecord(
                                time: s_testTime,
                                traceId: "1",
                                spanId: "1-1",
                                message: "",
                                attributes: [KeyValuePair.Create("event.name", "gen_ai.system.message")]),
                            CreateLogRecord(
                                time: s_testTime.AddSeconds(1),
                                traceId: "1",
                                spanId: "1-1",
                                message: string.Empty,
                                attributes: [KeyValuePair.Create("event.name", "gen_ai.user.message")])
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.Items);
        Assert.Null(vm.DisplayErrorMessage);
    }

    [Fact]
    public void Create_GenAISpanEvents_HasMessages()
    {
        // Arrange
        var repository = CreateRepository();

        var events = new List<Span.Types.Event>
        {
            CreateSpanEvent(
                name: "gen_ai.system.message",
                startTime: 0,
                attributes: [
                    KeyValuePair.Create(GenAIHelpers.GenAIEventContent, JsonSerializer.Serialize(new SystemOrUserEvent { Content = "System!" }, GenAIEventsContext.Default.SystemOrUserEvent)),
                    KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
                ]),
            CreateSpanEvent(
                name: "gen_ai.user.message",
                startTime: 1,
                attributes: [
                    KeyValuePair.Create(GenAIHelpers.GenAIEventContent, JsonSerializer.Serialize(new SystemOrUserEvent { Content = "User!" }, GenAIEventsContext.Default.SystemOrUserEvent)),
                    KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
                ]),
            CreateSpanEvent(
                name: "gen_ai.assistant.message",
                startTime: 2,
                attributes: [
                    KeyValuePair.Create(GenAIHelpers.GenAIEventContent, JsonSerializer.Serialize(new AssistantEvent { Content = "Assistant!" }, GenAIEventsContext.Default.AssistantEvent)),
                ]),
            CreateSpanEvent(
                name: "other_name_that_is_ignored",
                startTime: 3,
                attributes: [
                    KeyValuePair.Create(GenAIHelpers.GenAIEventContent, JsonSerializer.Serialize(new AssistantEvent { Content = "Assistant!" }, GenAIEventsContext.Default.AssistantEvent)),
                    KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
                ])
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"), KeyValuePair.Create("server.address", "ai-server.address")], events: events)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("System!", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("User!", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.AssistantMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Assistant!", Assert.IsType<TextPart>(p.MessagePart).Content));
            });
        Assert.Null(vm.ModelName);
        Assert.Null(vm.InputTokens);
        Assert.Null(vm.OutputTokens);
    }

    [Fact]
    public void Create_GenAISpanEvents_EmptyContent_NoMessagesCreated()
    {
        // Arrange
        var repository = CreateRepository();

        var events = new List<Span.Types.Event>
        {
            CreateSpanEvent(
                name: "gen_ai.system.message",
                startTime: 0,
                attributes: [
                    KeyValuePair.Create(GenAIHelpers.GenAIEventContent, ""),
                    KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
                ]),
            CreateSpanEvent(
                name: "gen_ai.user.message",
                startTime: 1,
                attributes: [
                    KeyValuePair.Create(GenAIHelpers.GenAIEventContent, string.Empty),
                ])
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!")], events: events)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.Items);
        Assert.Null(vm.DisplayErrorMessage);
    }

    [Fact]
    public void Create_GenAISpanEvents_MissingContent_NoMessagesCreated()
    {
        // Arrange
        var repository = CreateRepository();

        var events = new List<Span.Types.Event>
        {
            CreateSpanEvent(
                name: "gen_ai.system.message",
                startTime: 0,
                attributes: [
                    KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
                    // GenAIEventContent attribute is missing
                ])
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!")], events: events)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.Items);
        Assert.Null(vm.DisplayErrorMessage);
    }

    [Fact]
    public void Create_GenAISpanAttributes_HasMessages()
    {
        // Arrange
        var repository = CreateRepository();

        var systemInstruction = JsonSerializer.Serialize(new List<MessagePart>
        {
            new TextPart { Content = "System!" }
        }, GenAIMessagesContext.Default.ListMessagePart);

        var inputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "user",
                Parts = [new TextPart { Content = "User!" }]
            },
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new ToolCallRequestPart { Name = "generate_names", Arguments = JsonNode.Parse(@"{""count"":2}") }]
            },
            new ChatMessage
            {
                Role = "user",
                Parts = [new ToolCallResponsePart { Response = JsonNode.Parse(@"[""Jack"",""Jane""]") }]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        var outputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new TextPart { Content = "Output!" }]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            KeyValuePair.Create(GenAIHelpers.GenAISystemInstructions, systemInstruction),
            KeyValuePair.Create(GenAIHelpers.GenAIInputMessages, inputMessages),
            KeyValuePair.Create(GenAIHelpers.GenAIOutputInstructions, outputMessages)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("System!", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("User!", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.AssistantMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p =>
                    {
                        var toolCallRequestPart = Assert.IsType<ToolCallRequestPart>(p.MessagePart);
                        Assert.Equal("generate_names", toolCallRequestPart.Name);
                        Assert.Equal(@"{""count"":2}", toolCallRequestPart.Arguments!.ToJsonString());
                    });
            },
            m =>
            {
                Assert.Equal(GenAIItemType.ToolMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal(@"[""Jack"",""Jane""]", Assert.IsType<ToolCallResponsePart>(p.MessagePart).Response!.ToJsonString()));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Output!", Assert.IsType<TextPart>(p.MessagePart).Content));
            });
        Assert.Null(vm.ModelName);
        Assert.Null(vm.InputTokens);
        Assert.Null(vm.OutputTokens);
        Assert.Null(vm.DisplayErrorMessage);
    }

    [Fact]
    public void Create_GenAISpanAttributes_InvalidJson_DisplayErrorMessage()
    {
        // Arrange
        var repository = CreateRepository();

        var systemInstruction = JsonSerializer.Serialize(new List<MessagePart>
        {
            new TextPart { Content = "System!" }
        }, GenAIMessagesContext.Default.ListMessagePart);

        var inputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "user",
                Parts = [new TextPart { Content = "User!" }]
            },
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new ToolCallRequestPart { Name = "generate_names", Arguments = JsonNode.Parse(@"{""count"":2}") }]
            },
            new ChatMessage
            {
                Role = "user",
                Parts = [new ToolCallResponsePart { Response = JsonNode.Parse(@"[""Jack"",""Jane""]") }]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        var outputMessages = "invalid!";

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            KeyValuePair.Create(GenAIHelpers.GenAISystemInstructions, systemInstruction),
            KeyValuePair.Create(GenAIHelpers.GenAIInputMessages, inputMessages),
            KeyValuePair.Create(GenAIHelpers.GenAIOutputInstructions, outputMessages)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.Items);
        Assert.NotNull(vm.DisplayErrorMessage);
    }

    [Fact]
    public void Create_GenAISpanAttributesWithoutContent_HasNoMessageContent()
    {
        // Arrange
        var repository = CreateRepository();

        var systemInstruction = JsonSerializer.Serialize(new List<MessagePart>
        {
            new TextPart { Content = "" }
        }, GenAIMessagesContext.Default.ListMessagePart);

        var inputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "user",
                Parts = [new TextPart { Content = "" }]
            },
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new ToolCallRequestPart { Name = "generate_names" }]
            },
            new ChatMessage
            {
                Role = "user",
                Parts = [new ToolCallResponsePart()]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        var outputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new TextPart { Content = "" }]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            KeyValuePair.Create(GenAIHelpers.GenAISystemInstructions, systemInstruction),
            KeyValuePair.Create(GenAIHelpers.GenAIInputMessages, inputMessages),
            KeyValuePair.Create(GenAIHelpers.GenAIOutputInstructions, outputMessages)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.AssistantMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p =>
                    {
                        var toolCallRequestPart = Assert.IsType<ToolCallRequestPart>(p.MessagePart);
                        Assert.Equal("generate_names", toolCallRequestPart.Name);
                        Assert.Null(toolCallRequestPart.Arguments);
                    });
            },
            m =>
            {
                Assert.Equal(GenAIItemType.ToolMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Null(Assert.IsType<ToolCallResponsePart>(p.MessagePart).Response));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("", Assert.IsType<TextPart>(p.MessagePart).Content));
            });
        Assert.True(vm.NoMessageContent);
    }

    [Fact]
    public void Create_NoMessages_HasNoMessageContent()
    {
        // Arrange
        var repository = CreateRepository();

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.Items);
        Assert.True(vm.NoMessageContent);
    }

    [Fact]
    public void Create_LangSmithFormat_HasMessages()
    {
        // Arrange
        var repository = CreateRepository();

        // LangSmith uses flattened attributes with indexed format
        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            // Prompt messages
            KeyValuePair.Create("gen_ai.prompt.0.role", "system"),
            KeyValuePair.Create("gen_ai.prompt.0.content", "You are a helpful assistant."),
            KeyValuePair.Create("gen_ai.prompt.1.role", "user"),
            KeyValuePair.Create("gen_ai.prompt.1.content", "Hello, how are you?"),
            // Completion messages
            KeyValuePair.Create("gen_ai.completion.0.role", "assistant"),
            KeyValuePair.Create("gen_ai.completion.0.content", "I'm doing well, thank you!")
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("You are a helpful assistant.", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Hello, how are you?", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("I'm doing well, thank you!", Assert.IsType<TextPart>(p.MessagePart).Content));
            });
        Assert.Null(vm.ModelName);
        Assert.Null(vm.InputTokens);
        Assert.Null(vm.OutputTokens);
    }

    [Fact]
    public void Create_LangSmithFormat_MessageRoleContentFallback_HasMessages()
    {
        // Arrange
        var repository = CreateRepository();

        // LangSmith format with message.role and message.content as fallback
        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            // Prompt messages using message.role and message.content
            KeyValuePair.Create("gen_ai.prompt.0.message.role", "system"),
            KeyValuePair.Create("gen_ai.prompt.0.message.content", "You are a coding assistant."),
            KeyValuePair.Create("gen_ai.prompt.1.message.role", "user"),
            KeyValuePair.Create("gen_ai.prompt.1.message.content", "Write a hello world program."),
            // Completion messages using message.role and message.content
            KeyValuePair.Create("gen_ai.completion.0.message.role", "assistant"),
            KeyValuePair.Create("gen_ai.completion.0.message.content", "Here's a simple hello world program...")
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("You are a coding assistant.", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Write a hello world program.", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Here's a simple hello world program...", Assert.IsType<TextPart>(p.MessagePart).Content));
            });
        Assert.Null(vm.ModelName);
        Assert.Null(vm.InputTokens);
        Assert.Null(vm.OutputTokens);
    }

    [Fact]
    public void Create_LangSmithFormat_WithGapsInIndices_HasMessages()
    {
        // Arrange
        var repository = CreateRepository();

        // LangSmith format with gaps in indices (0, 2, 5 instead of consecutive 0, 1, 2)
        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            // Prompt messages with gaps in indices
            KeyValuePair.Create("gen_ai.prompt.0.role", "system"),
            KeyValuePair.Create("gen_ai.prompt.0.content", "You are a helpful assistant."),
            // Skip index 1
            KeyValuePair.Create("gen_ai.prompt.2.role", "user"),
            KeyValuePair.Create("gen_ai.prompt.2.content", "What is 2+2?"),
            // Skip indices 3 and 4
            KeyValuePair.Create("gen_ai.prompt.5.role", "user"),
            KeyValuePair.Create("gen_ai.prompt.5.content", "Follow up question."),
            // Completion messages with gaps
            KeyValuePair.Create("gen_ai.completion.0.role", "assistant"),
            KeyValuePair.Create("gen_ai.completion.0.content", "The answer is 4."),
            // Skip index 1
            KeyValuePair.Create("gen_ai.completion.3.role", "assistant"),
            KeyValuePair.Create("gen_ai.completion.3.content", "Follow up answer.")
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        // Messages should be parsed in order of their indices (0, 2, 5 for prompts; 0, 3 for completions)
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("You are a helpful assistant.", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("What is 2+2?", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Equal("TestService", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Follow up question.", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("The answer is 4.", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Equal("ai-server.address", m.ResourceName);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Follow up answer.", Assert.IsType<TextPart>(p.MessagePart).Content));
            });
        Assert.Null(vm.ModelName);
        Assert.Null(vm.InputTokens);
        Assert.Null(vm.OutputTokens);
    }

    [Fact]
    public void Create_GenAIToolDefinitions_ParsesToolDefinitions()
    {
        // Arrange
        var repository = CreateRepository();

        // Create tool definitions JSON manually since OpenApiSchema doesn't serialize well with System.Text.Json
        var toolDefinitionsJson = """
        [
          {
            "type": "function",
            "name": "get_current_weather",
            "description": "Get the current weather in a given location",
            "parameters": {
              "type": "object",
              "properties": {
                "location": {
                  "type": "string",
                  "description": "The city and state, e.g. San Francisco, CA"
                },
                "unit": {
                  "type": "string",
                  "enum": ["celsius", "fahrenheit"]
                }
              },
              "required": ["location", "unit"]
            }
          }
        ]
        """;

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create(GenAIHelpers.GenAIToolDefinitions, toolDefinitionsJson)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.ToolDefinitions,
            tool =>
            {
                Assert.Equal("function", tool.ToolDefinition.Type);
                Assert.Equal("get_current_weather", tool.ToolDefinition.Name);
                Assert.Equal("Get the current weather in a given location", tool.ToolDefinition.Description);
                Assert.NotNull(tool.ToolDefinition.Parameters);
                Assert.Equal(JsonSchemaType.Object, tool.ToolDefinition.Parameters.Type);
                Assert.NotNull(tool.ToolDefinition.Parameters.Properties);
                Assert.Equal(2, tool.ToolDefinition.Parameters.Properties.Count);

                Assert.True(tool.ToolDefinition.Parameters.Properties.ContainsKey("location"));
                var locationProp = tool.ToolDefinition.Parameters.Properties["location"];
                Assert.Equal(JsonSchemaType.String, locationProp.Type);
                Assert.Equal("The city and state, e.g. San Francisco, CA", locationProp.Description);

                Assert.True(tool.ToolDefinition.Parameters.Properties.ContainsKey("unit"));
                var unitProp = tool.ToolDefinition.Parameters.Properties["unit"];
                Assert.Equal(JsonSchemaType.String, unitProp.Type);
                Assert.NotNull(unitProp.Enum);
                Assert.Equal(2, unitProp.Enum.Count);
                Assert.Equal("celsius", unitProp.Enum[0]!.GetValue<string>());
                Assert.Equal("fahrenheit", unitProp.Enum[1]!.GetValue<string>());

                Assert.NotNull(tool.ToolDefinition.Parameters.Required);
                Assert.Equal(2, tool.ToolDefinition.Parameters.Required.Count);
                Assert.Contains("location", tool.ToolDefinition.Parameters.Required);
                Assert.Contains("unit", tool.ToolDefinition.Parameters.Required);
            });
    }

    [Fact]
    public void Create_GenAIToolDefinitions_InvalidJson_EmptyToolDefinitions()
    {
        // Arrange
        var repository = CreateRepository();

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create(GenAIHelpers.GenAIToolDefinitions, "invalid json")
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.ToolDefinitions);
    }

    [Fact]
    public void Create_NoToolDefinitions_EmptyToolDefinitions()
    {
        // Arrange
        var repository = CreateRepository();

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!")
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.ToolDefinitions);
    }

    [Fact]
    public void Create_GenAISpanAttributes_JsonWithCommentsAndTrailingCommas_HasMessages()
    {
        // Arrange
        var repository = CreateRepository();

        // JSON with comments and trailing commas - these are commonly produced by AI SDKs and LLM outputs
        var systemInstruction = """
            [
                // System instruction comment
                {
                    "type": "text",
                    "content": "You are a helpful assistant.", // trailing comment
                },
            ]
            """;

        var inputMessages = """
            [
                /* Multi-line
                   comment */
                {
                    "role": "user",
                    "parts": [
                        {
                            "type": "text",
                            "content": "Hello!", // inline comment
                        },
                    ],
                },
            ]
            """;

        var outputMessages = """
            [
                {
                    "role": "assistant", // role comment
                    "parts": [
                        {
                            "type": "text",
                            "content": "Hi there!", // response comment
                        },
                    ], // trailing comma after parts
                }, // trailing comma after message
            ]
            """;

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            KeyValuePair.Create(GenAIHelpers.GenAISystemInstructions, systemInstruction),
            KeyValuePair.Create(GenAIHelpers.GenAIInputMessages, inputMessages),
            KeyValuePair.Create(GenAIHelpers.GenAIOutputInstructions, outputMessages)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert - verify JSON with comments and trailing commas is parsed correctly
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("You are a helpful assistant.", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Hello!", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Hi there!", Assert.IsType<TextPart>(p.MessagePart).Content));
            });
        Assert.Null(vm.DisplayErrorMessage);
    }

    [Fact]
    public void Create_GenAIToolDefinitions_JsonWithCommentsAndTrailingCommas_HasToolDefinitions()
    {
        // Arrange
        var repository = CreateRepository();

        // Tool definitions JSON with comments and trailing commas
        var toolDefinitions = """
            [
                // First tool definition
                {
                    "type": "function",
                    "name": "get_weather", // tool name
                    "description": "Gets weather for a location",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "location": {
                                "type": "string",
                                "description": "The city name", // trailing comment
                            },
                        }, // trailing comma
                    },
                },
                /* Second tool */
                {
                    "type": "function",
                    "name": "search",
                    "description": "Searches the web",
                },
            ]
            """;

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create(GenAIHelpers.GenAIToolDefinitions, toolDefinitions)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert - verify tool definitions with comments and trailing commas are parsed correctly
        Assert.Equal(2, vm.ToolDefinitions.Count);
        Assert.Contains(vm.ToolDefinitions, t => t.ToolDefinition.Name == "get_weather" && t.ToolDefinition.Description == "Gets weather for a location");
        Assert.Contains(vm.ToolDefinitions, t => t.ToolDefinition.Name == "search" && t.ToolDefinition.Description == "Searches the web");
    }

    [Fact]
    public void Create_GenAIToolDefinitions_TypeAsArray_ParsesToolDefinitions()
    {
        // Arrange
        var repository = CreateRepository();

        // Create tool definitions JSON with "type" as an array of strings
        // This is valid in JSON Schema and can occur in GenAI tool schemas
        var toolDefinitionsJson = """
        [
          {
            "type": "function",
            "name": "get_optional_value",
            "description": "Get an optional value that may be null",
            "parameters": {
              "type": "object",
              "properties": {
                "value": {
                  "type": ["string", "null"],
                  "description": "The value which can be string or null"
                },
                "count": {
                  "type": ["number", "null"]
                },
                "flag": {
                  "type": "boolean"
                }
              },
              "required": ["flag"]
            }
          }
        ]
        """;

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create(GenAIHelpers.GenAIToolDefinitions, toolDefinitionsJson)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.ToolDefinitions,
            tool =>
            {
                Assert.Equal("function", tool.ToolDefinition.Type);
                Assert.Equal("get_optional_value", tool.ToolDefinition.Name);
                Assert.Equal("Get an optional value that may be null", tool.ToolDefinition.Description);
                Assert.NotNull(tool.ToolDefinition.Parameters);
                Assert.Equal(JsonSchemaType.Object, tool.ToolDefinition.Parameters.Type);
                Assert.NotNull(tool.ToolDefinition.Parameters.Properties);
                Assert.Equal(3, tool.ToolDefinition.Parameters.Properties.Count);

                // Check "value" property with type array ["string", "null"]
                Assert.True(tool.ToolDefinition.Parameters.Properties.ContainsKey("value"));
                var valueProp = tool.ToolDefinition.Parameters.Properties["value"];
                Assert.Equal(JsonSchemaType.String | JsonSchemaType.Null, valueProp.Type); // Should OR all types together
                Assert.Equal("The value which can be string or null", valueProp.Description);

                // Check "count" property with type array ["number", "null"]
                Assert.True(tool.ToolDefinition.Parameters.Properties.ContainsKey("count"));
                var countProp = tool.ToolDefinition.Parameters.Properties["count"];
                Assert.Equal(JsonSchemaType.Number | JsonSchemaType.Null, countProp.Type); // Should OR all types together

                // Check "flag" property with simple string type
                Assert.True(tool.ToolDefinition.Parameters.Properties.ContainsKey("flag"));
                var flagProp = tool.ToolDefinition.Parameters.Properties["flag"];
                Assert.Equal(JsonSchemaType.Boolean, flagProp.Type);

                Assert.NotNull(tool.ToolDefinition.Parameters.Required);
                Assert.Single(tool.ToolDefinition.Parameters.Required);
                Assert.Contains("flag", tool.ToolDefinition.Parameters.Required);
            });
    }

    [Fact]
    public void Create_GenAIToolDefinitions_WithArrayItems_ParsesAndFormatsCorrectly()
    {
        // Arrange
        var repository = CreateRepository();

        // Create tool definitions JSON with array types and items
        var toolDefinitionsJson = """
        [
          {
            "type": "function",
            "name": "process_data",
            "description": "Process a list of data items",
            "parameters": {
              "type": "object",
              "properties": {
                "items": {
                  "type": "array",
                  "items": {
                    "type": "string"
                  },
                  "description": "List of string items"
                },
                "numbers": {
                  "type": "array",
                  "items": {
                    "type": "number"
                  }
                },
                "mixed": {
                  "type": ["array", "null"],
                  "items": {
                    "type": "integer"
                  }
                }
              }
            }
          }
        ]
        """;

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create(GenAIHelpers.GenAIToolDefinitions, toolDefinitionsJson)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.ToolDefinitions,
            tool =>
            {
                Assert.Equal("function", tool.ToolDefinition.Type);
                Assert.Equal("process_data", tool.ToolDefinition.Name);
                Assert.NotNull(tool.ToolDefinition.Parameters);
                Assert.NotNull(tool.ToolDefinition.Parameters.Properties);
                Assert.Equal(3, tool.ToolDefinition.Parameters.Properties.Count);

                // Check "items" property - array of strings
                var itemsProp = tool.ToolDefinition.Parameters.Properties["items"];
                Assert.Equal(JsonSchemaType.Array, itemsProp.Type);
                Assert.NotNull(itemsProp.Items);
                Assert.Equal(JsonSchemaType.String, itemsProp.Items.Type);
                Assert.Equal("array<string>", string.Join(", ", GenAISchemaHelpers.ConvertTypeToNames(itemsProp)));

                // Check "numbers" property - array of numbers
                var numbersProp = tool.ToolDefinition.Parameters.Properties["numbers"];
                Assert.Equal(JsonSchemaType.Array, numbersProp.Type);
                Assert.NotNull(numbersProp.Items);
                Assert.Equal(JsonSchemaType.Number, numbersProp.Items.Type);
                Assert.Equal("array<number>", string.Join(", ", GenAISchemaHelpers.ConvertTypeToNames(numbersProp)));

                // Check "mixed" property - array or null of integers (null excluded from display)
                var mixedProp = tool.ToolDefinition.Parameters.Properties["mixed"];
                Assert.Equal(JsonSchemaType.Array | JsonSchemaType.Null, mixedProp.Type);
                Assert.NotNull(mixedProp.Items);
                Assert.Equal(JsonSchemaType.Integer, mixedProp.Items.Type);
                Assert.Equal("array<integer>", string.Join(", ", GenAISchemaHelpers.ConvertTypeToNames(mixedProp)));
            });
    }

    private static GenAIVisualizerDialogViewModel Create(
        TelemetryRepository repository,
        SpanDetailsViewModel spanDetailsViewModel)
    {
        return GenAIVisualizerDialogViewModel.Create(
            spanDetailsViewModel,
            selectedLogEntryId: null,
            errorRecorder: new TestTelemetryErrorRecorder(),
            telemetryRepository: repository,
            () => [spanDetailsViewModel.Span]);
    }

    [Fact]
    public void Create_NoEvaluationResults_EmptyEvaluationsList()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Empty(vm.Evaluations);
    }

    [Fact]
    public void Create_EvaluationResultsInLogEntries_ParsedCorrectly()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords =
                        {
                            CreateLogRecord(
                                time: s_testTime,
                                traceId: "1",
                                spanId: "1-1",
                                message: string.Empty,
                                attributes: [
                                    KeyValuePair.Create("event.name", "gen_ai.evaluation.result"),
                                    KeyValuePair.Create("gen_ai.evaluation.name", "Relevance"),
                                    KeyValuePair.Create("gen_ai.evaluation.score.label", "relevant"),
                                    KeyValuePair.Create("gen_ai.evaluation.score.value", "0.85"),
                                    KeyValuePair.Create("gen_ai.evaluation.explanation", "The response is factually accurate and addresses the question."),
                                    KeyValuePair.Create("gen_ai.response.id", "chatcmpl-123")
                                ]),
                            CreateLogRecord(
                                time: s_testTime.AddSeconds(1),
                                traceId: "1",
                                spanId: "1-1",
                                message: string.Empty,
                                attributes: [
                                    KeyValuePair.Create("event.name", "gen_ai.evaluation.result"),
                                    KeyValuePair.Create("gen_ai.evaluation.name", "IntentResolution"),
                                    KeyValuePair.Create("gen_ai.evaluation.score.label", "incorrect"),
                                    KeyValuePair.Create("gen_ai.evaluation.score.value", "0.65"),
                                    KeyValuePair.Create("error.type", "timeout")
                                ])
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Evaluations,
            e =>
            {
                Assert.Equal("Relevance", e.Name);
                Assert.Equal("relevant", e.ScoreLabel);
                Assert.Equal(0.85, e.ScoreValue);
                Assert.Equal("The response is factually accurate and addresses the question.", e.Explanation);
                Assert.Equal("chatcmpl-123", e.ResponseId);
                Assert.Null(e.ErrorType);
            },
            e =>
            {
                Assert.Equal("IntentResolution", e.Name);
                Assert.Equal("incorrect", e.ScoreLabel);
                Assert.Equal(0.65, e.ScoreValue);
                Assert.Null(e.Explanation);
                Assert.Null(e.ResponseId);
                Assert.Equal("timeout", e.ErrorType);
            });
    }

    [Fact]
    public void Create_EvaluationResultsInSpanEvents_ParsedCorrectly()
    {
        // Arrange
        var repository = CreateRepository();

        var events = new List<Span.Types.Event>
        {
            CreateSpanEvent(
                name: "gen_ai.evaluation.result",
                startTime: 0,
                attributes: new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("gen_ai.evaluation.name", "Relevance"),
                    KeyValuePair.Create("gen_ai.evaluation.score.label", "pass"),
                    KeyValuePair.Create("gen_ai.evaluation.score.value", "0.95"),
                    KeyValuePair.Create("gen_ai.evaluation.explanation", "Very accurate response"),
                    KeyValuePair.Create("gen_ai.response.id", "chatcmpl-456")
                }),
            CreateSpanEvent(
                name: "gen_ai.evaluation.result",
                startTime: 1,
                attributes: new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("gen_ai.evaluation.name", "IntentResolution"),
                    KeyValuePair.Create("gen_ai.evaluation.score.value", "0.88")
                })
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime.AddMinutes(1),
                                endTime: s_testTime.AddMinutes(10),
                                events: events)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Collection(vm.Evaluations,
            e =>
            {
                Assert.Equal("Relevance", e.Name);
                Assert.Equal("pass", e.ScoreLabel);
                Assert.Equal(0.95, e.ScoreValue);
                Assert.Equal("Very accurate response", e.Explanation);
                Assert.Equal("chatcmpl-456", e.ResponseId);
                Assert.Null(e.ErrorType);
            },
            e =>
            {
                Assert.Equal("IntentResolution", e.Name);
                Assert.Null(e.ScoreLabel);
                Assert.Equal(0.88, e.ScoreValue);
                Assert.Null(e.Explanation);
                Assert.Null(e.ResponseId);
                Assert.Null(e.ErrorType);
            });
    }

    [Fact]
    public void Create_EvaluationResultsMinimalData_ParsedCorrectly()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords =
                        {
                            CreateLogRecord(
                                time: s_testTime,
                                traceId: "1",
                                spanId: "1-1",
                                message: string.Empty,
                                attributes: [
                                    KeyValuePair.Create("event.name", "gen_ai.evaluation.result"),
                                    KeyValuePair.Create("gen_ai.evaluation.name", "BasicMetric")
                                ])
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Single(vm.Evaluations);
        Assert.Equal("BasicMetric", vm.Evaluations[0].Name);
        Assert.Null(vm.Evaluations[0].ScoreLabel);
        Assert.Null(vm.Evaluations[0].ScoreValue);
        Assert.Null(vm.Evaluations[0].Explanation);
        Assert.Null(vm.Evaluations[0].ResponseId);
        Assert.Null(vm.Evaluations[0].ErrorType);
    }

    [Fact]
    public void Create_EvaluationResultsFromBothLogEntriesAndSpanEvents_AllParsed()
    {
        // Arrange
        var repository = CreateRepository();

        var events = new List<Span.Types.Event>
        {
            CreateSpanEvent(
                name: "gen_ai.evaluation.result",
                startTime: 0,
                attributes: new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("gen_ai.evaluation.name", "SpanEventMetric"),
                    KeyValuePair.Create("gen_ai.evaluation.score.value", "0.75")
                })
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime.AddMinutes(1),
                                endTime: s_testTime.AddMinutes(10),
                                events: events)
                        }
                    }
                }
            }
        });

        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords =
                        {
                            CreateLogRecord(
                                time: s_testTime,
                                traceId: "1",
                                spanId: "1-1",
                                message: string.Empty,
                                attributes: [
                                    KeyValuePair.Create("event.name", "gen_ai.evaluation.result"),
                                    KeyValuePair.Create("gen_ai.evaluation.name", "LogEntryMetric"),
                                    KeyValuePair.Create("gen_ai.evaluation.score.value", "0.65")
                                ])
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert
        Assert.Equal(2, vm.Evaluations.Count);
        Assert.Contains(vm.Evaluations, e => e.Name == "LogEntryMetric" && e.ScoreValue == 0.65);
        Assert.Contains(vm.Evaluations, e => e.Name == "SpanEventMetric" && e.ScoreValue == 0.75);
    }

    [Fact]
    public void Create_GenAIToolDefinitions_UnexpectedTypeObject_DoesNotThrow()
    {
        // Arrange
        var repository = CreateRepository();

        // Create tool definitions JSON with a parameter that has "type" as an object instead of string
        // This simulates the issue reported where Microsoft Agent Framework might produce such JSON
        var toolDefinitionsJson = """
        [
          {
            "type": "function",
            "name": "get_data",
            "description": "Gets data from source",
            "parameters": {
              "type": "object",
              "properties": {
                "validParam": {
                  "type": "string",
                  "description": "A valid parameter with string type"
                },
                "invalidParam": {
                  "type": {
                    "description": "This is an object instead of a string - should be handled gracefully"
                  },
                  "description": "A parameter with invalid type structure"
                }
              }
            }
          }
        ]
        """;

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create(GenAIHelpers.GenAIToolDefinitions, toolDefinitionsJson)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act - should not throw an exception
        var vm = Create(repository, spanDetailsViewModel);

        // Assert - tool definition should be parsed, with valid parameter parsed correctly
        // and invalid parameter handled gracefully (type will be null instead of throwing)
        Assert.Single(vm.ToolDefinitions);
        var tool = vm.ToolDefinitions[0];
        Assert.Equal("function", tool.ToolDefinition.Type);
        Assert.Equal("get_data", tool.ToolDefinition.Name);
        Assert.Equal("Gets data from source", tool.ToolDefinition.Description);
        Assert.NotNull(tool.ToolDefinition.Parameters);
        Assert.NotNull(tool.ToolDefinition.Parameters.Properties);
        Assert.Equal(2, tool.ToolDefinition.Parameters.Properties.Count);

        // Valid parameter should be parsed correctly
        Assert.True(tool.ToolDefinition.Parameters.Properties.ContainsKey("validParam"));
        var validParam = tool.ToolDefinition.Parameters.Properties["validParam"];
        Assert.Equal(JsonSchemaType.String, validParam.Type);
        Assert.Equal("A valid parameter with string type", validParam.Description);

        // Invalid parameter should still be in properties but with null type (not throw exception)
        Assert.True(tool.ToolDefinition.Parameters.Properties.ContainsKey("invalidParam"));
        var invalidParam = tool.ToolDefinition.Parameters.Properties["invalidParam"];
        Assert.Null(invalidParam.Type); // Type should be null since it couldn't be parsed
        Assert.Equal("A parameter with invalid type structure", invalidParam.Description);
    }

    [Fact]
    public void Create_GenAISpanAttributes_TruncatedInputMessages_DisplaysAvailableMessages()
    {
        // Arrange
        var repository = CreateRepository();

        var inputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "user",
                Parts = [new TextPart { Content = "First message" }]
            },
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new TextPart { Content = "Second message" }]
            },
            new ChatMessage
            {
                Role = "user",
                Parts = [new TextPart { Content = "Third message that will be truncated" }]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        // Truncate the JSON mid-way through the third message
        var truncatedInput = inputMessages.Substring(0, inputMessages.IndexOf("Third"));

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            KeyValuePair.Create(GenAIHelpers.GenAIInputMessages, truncatedInput)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert - first two messages parsed, third truncated, plus truncation indicator
        Assert.Null(vm.DisplayErrorMessage);
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("First message", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.AssistantMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Second message", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.UserMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal(Resources.Dialogs.GenAIUnexpectedOrTruncatedContent, p.ErrorMessage));
            });
    }

    [Fact]
    public void Create_GenAISpanAttributes_TruncatedSystemInstructions_DisplaysPartialContent()
    {
        // Arrange
        var repository = CreateRepository();

        var systemInstruction = JsonSerializer.Serialize(new List<MessagePart>
        {
            new TextPart { Content = "First instruction" },
            new TextPart { Content = "Second instruction that will be truncated" }
        }, GenAIMessagesContext.Default.ListMessagePart);

        // Truncate the JSON mid-way through the second instruction
        var truncatedInstruction = systemInstruction.Substring(0, systemInstruction.IndexOf("Second"));

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            KeyValuePair.Create(GenAIHelpers.GenAISystemInstructions, truncatedInstruction)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert - first instruction parsed, second truncated, plus truncation indicator
        Assert.Null(vm.DisplayErrorMessage);
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.SystemMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("First instruction", Assert.IsType<TextPart>(p.MessagePart).Content),
                    p => Assert.Equal(Resources.Dialogs.GenAIUnexpectedOrTruncatedContent, p.ErrorMessage));
            });
    }

    [Fact]
    public void Create_GenAISpanAttributes_TruncatedOutputMessages_DisplaysAvailableMessages()
    {
        // Arrange
        var repository = CreateRepository();

        var outputMessages = JsonSerializer.Serialize(new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new TextPart { Content = "Complete output" }]
            },
            new ChatMessage
            {
                Role = "assistant",
                Parts = [new TextPart { Content = "Truncated output message" }]
            }
        }, GenAIMessagesContext.Default.ListChatMessage);

        // Truncate the JSON mid-way through the second message
        var truncatedOutput = outputMessages.Substring(0, outputMessages.IndexOf("Truncated"));

        var attributes = new KeyValuePair<string, string>[]
        {
            KeyValuePair.Create(GenAIHelpers.GenAISystem, "System!"),
            KeyValuePair.Create("server.address", "ai-server.address"),
            KeyValuePair.Create(GenAIHelpers.GenAIOutputInstructions, truncatedOutput)
        };

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var span = repository.GetSpan(GetHexId("1"), GetHexId("1-1"))!;
        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, repository, repository.GetResources());

        // Act
        var vm = Create(repository, spanDetailsViewModel);

        // Assert - first output message parsed, second truncated, plus truncation indicator
        Assert.Null(vm.DisplayErrorMessage);
        Assert.Collection(vm.Items,
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal("Complete output", Assert.IsType<TextPart>(p.MessagePart).Content));
            },
            m =>
            {
                Assert.Equal(GenAIItemType.OutputMessage, m.Type);
                Assert.Collection(m.ItemParts,
                    p => Assert.Equal(Resources.Dialogs.GenAIUnexpectedOrTruncatedContent, p.ErrorMessage));
            });
    }
}
