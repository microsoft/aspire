// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading.Channels;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Integration;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public class LogTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _testOutputHelper;

    public LogTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public void AddLogs()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                Assert.Equal("546573745370616e4964", resource.SpanId);
                Assert.Equal("5465737454726163654964", resource.TraceId);
                Assert.Equal("Test {Log}", resource.OriginalFormat);
                Assert.Equal("Test Value!", resource.Message);
                Assert.Equal("TestLogger", resource.Scope.Name);
                Assert.Collection(resource.Attributes,
                    p =>
                    {
                        Assert.Equal("Log", p.Key);
                        Assert.Equal("Value!", p.Value);
                    });
            });

        var propertyKeys = repository.GetLogPropertyKeys(resources[0].ResourceKey)!;
        Assert.Collection(propertyKeys,
            s => Assert.Equal("Log", s));
    }

    [Fact]
    public void AddLogs_NoBody_EmptyMessage()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(skipBody: true) }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                Assert.Equal("", resource.Message);
            });
    }

    [Fact]
    public void AddLogs_MultipleOutOfOrder()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "1"),
                            CreateLogRecord(time: s_testTime.AddMinutes(2), message: "2"),
                            CreateLogRecord(time: s_testTime.AddMinutes(3), message: "3"),
                            CreateLogRecord(time: s_testTime.AddMinutes(10), message: "10"),
                            CreateLogRecord(time: s_testTime.AddMinutes(9), message: "9"),
                            CreateLogRecord(time: s_testTime.AddMinutes(4), message: "4"),
                            CreateLogRecord(time: s_testTime.AddMinutes(5), message: "5"),
                            CreateLogRecord(time: s_testTime.AddMinutes(7), message: "7"),
                            CreateLogRecord(time: s_testTime.AddMinutes(6), message: "6"),
                            CreateLogRecord(time: s_testTime.AddMinutes(8), message: "8"),
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            l =>
            {
                Assert.Equal("1", l.Message);
                Assert.Same(OtlpScope.Empty, l.Scope);
            },
            l => Assert.Equal("2", l.Message),
            l => Assert.Equal("3", l.Message),
            l => Assert.Equal("4", l.Message),
            l => Assert.Equal("5", l.Message),
            l => Assert.Equal("6", l.Message),
            l => Assert.Equal("7", l.Message),
            l => Assert.Equal("8", l.Message),
            l => Assert.Equal("9", l.Message),
            l => Assert.Equal("10", l.Message));
    }

    [Fact]
    public void AddLogs_Error_UnviewedCount()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "1", severity: SeverityNumber.Trace),
                            CreateLogRecord(time: s_testTime.AddMinutes(2), message: "2", severity: SeverityNumber.Debug),
                            CreateLogRecord(time: s_testTime.AddMinutes(3), message: "3", severity: SeverityNumber.Info),
                            CreateLogRecord(time: s_testTime.AddMinutes(4), message: "4", severity: SeverityNumber.Warn),
                            CreateLogRecord(time: s_testTime.AddMinutes(5), message: "5", severity: SeverityNumber.Error),
                            CreateLogRecord(time: s_testTime.AddMinutes(6), message: "6", severity: SeverityNumber.Fatal)
                        }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "2"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "1", severity: SeverityNumber.Fatal)
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var unviewedCounts1 = repository.GetResourceUnviewedErrorLogsCount();

        Assert.True(unviewedCounts1.TryGetValue(new ResourceKey("TestService", "1"), out var unviewedCount1));
        Assert.Equal(2, unviewedCount1);

        Assert.True(unviewedCounts1.TryGetValue(new ResourceKey("TestService", "2"), out var unviewedCount2));
        Assert.Equal(1, unviewedCount2);

        repository.MarkViewedErrorLogs(new ResourceKey("TestService", "1"));

        var unviewedCounts2 = repository.GetResourceUnviewedErrorLogsCount();

        Assert.False(unviewedCounts2.TryGetValue(new ResourceKey("TestService", "1"), out _));

        Assert.True(unviewedCounts2.TryGetValue(new ResourceKey("TestService", "2"), out unviewedCount2));
        Assert.Equal(1, unviewedCount2);

        repository.MarkViewedErrorLogs(null);

        var unviewedCounts3 = repository.GetResourceUnviewedErrorLogsCount();

        Assert.False(unviewedCounts3.TryGetValue(new ResourceKey("TestService", "1"), out _));
        Assert.False(unviewedCounts3.TryGetValue(new ResourceKey("TestService", "2"), out _));
    }

    [Fact]
    public void AddLogs_Error_UnviewedCount_WithReadSubscriptionAll()
    {
        // Arrange
        var repository = CreateRepository();
        using var subscription = repository.OnNewLogs(resourceKey: null, SubscriptionType.Read, () => Task.CompletedTask);

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "1", severity: SeverityNumber.Error),
                        }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "2"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "1", severity: SeverityNumber.Fatal)
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var unviewedCounts = repository.GetResourceUnviewedErrorLogsCount();

        Assert.False(unviewedCounts.TryGetValue(new ResourceKey("TestService", "1"), out _));
        Assert.False(unviewedCounts.TryGetValue(new ResourceKey("TestService", "2"), out _));
    }

    [Fact]
    public void AddLogs_Error_UnviewedCount_WithReadSubscriptionOneApp()
    {
        // Arrange
        var repository = CreateRepository();
        using var subscription = repository.OnNewLogs(resourceKey: new ResourceKey("TestService", "1"), SubscriptionType.Read, () => Task.CompletedTask);

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "1", severity: SeverityNumber.Error),
                        }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "2"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "1", severity: SeverityNumber.Fatal)
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var unviewedCounts = repository.GetResourceUnviewedErrorLogsCount();

        Assert.False(unviewedCounts.TryGetValue(new ResourceKey("TestService", "1"), out _));
        Assert.True(unviewedCounts.TryGetValue(new ResourceKey("TestService", "2"), out var unviewedCount));
        Assert.Equal(1, unviewedCount);
    }

    [Fact]
    public void AddLogs_Error_UnviewedCount_WithNonReadSubscription()
    {
        // Arrange
        var repository = CreateRepository();
        using var subscription = repository.OnNewLogs(resourceKey: null, SubscriptionType.Other, () => Task.CompletedTask);

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "1", severity: SeverityNumber.Error),
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var unviewedCounts = repository.GetResourceUnviewedErrorLogsCount();

        Assert.True(unviewedCounts.TryGetValue(new ResourceKey("TestService", "1"), out var unviewedCount));
        Assert.Equal(1, unviewedCount);
    }

    [Fact]
    public void GetLogs_UnknownResource()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = new ResourceKey("TestService", "UnknownResource"),
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

        // Assert
        Assert.Empty(logs.Items);
    }

    [Fact]
    public void GetLogPropertyKeys_UnknownResource()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var propertyKeys = repository.GetLogPropertyKeys(new ResourceKey("TestService", "UnknownResource"));

        // Assert
        Assert.Empty(propertyKeys);
    }

    [Fact]
    public async Task Subscriptions_AddLog()
    {
        // Arrange
        var repository = CreateRepository();

        var newResourcesTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.OnNewResources(() =>
        {
            newResourcesTcs.TrySetResult();
            return Task.CompletedTask;
        });

        // Act 1
        var addContext1 = new AddContext();
        repository.AddLogs(addContext1, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });

        // Assert 1
        Assert.Equal(0, addContext1.FailureCount);
        await newResourcesTcs.Task.DefaultTimeout();

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        // Act 2
        var newLogsTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.OnNewLogs(resources[0].ResourceKey, SubscriptionType.Read, () =>
        {
            newLogsTcs.TrySetResult();
            return Task.CompletedTask;
        });

        var addContext2 = new AddContext();
        repository.AddLogs(addContext2, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });

        await newLogsTcs.Task.DefaultTimeout();

        // Assert 2
        Assert.Equal(0, addContext2.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 1,
            Filters = []
        })!;
        Assert.Single(logs.Items);
        Assert.Equal(2, logs.TotalItemCount);
    }

    [Fact]
    public void Unsubscribe()
    {
        // Arrange
        var repository = CreateRepository();

        var onNewResourcesCalled = false;
        var subscription = repository.OnNewResources(() =>
        {
            onNewResourcesCalled = true;
            return Task.CompletedTask;
        });
        subscription.Dispose();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);
        Assert.False(onNewResourcesCalled, "Callback shouldn't have been called because subscription was disposed.");
    }

    [Fact]
    public async Task Subscription_RaisedFromDifferentContext_InitialContextPreserved()
    {
        // Arrange
        var asyncLocal = new AsyncLocal<string>();
        asyncLocal.Value = "CustomValue";

        var repository = CreateRepository();

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = repository.OnNewResources(() =>
        {
            tcs.SetResult(asyncLocal.Value);
            return Task.CompletedTask;
        });

        // Act
        Task task;
        using (ExecutionContext.SuppressFlow())
        {
            task = Task.Run(() =>
            {
                var addContext = new AddContext();
                repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
                {
                    new ResourceLogs
                    {
                        Resource = CreateResource(),
                        ScopeLogs =
                        {
                            new ScopeLogs
                            {
                                Scope = CreateScope("TestLogger"),
                                LogRecords = { CreateLogRecord() }
                            }
                        }
                    }
                });
            });
        }

        await task.DefaultTimeout();

        // Assert
        var callbackValue = await tcs.Task.DefaultTimeout();
        Assert.Equal("CustomValue", callbackValue);
    }

    [Fact]
    public void AddLogs_AttributeLimits_LimitsApplied()
    {
        // Arrange
        var repository = CreateRepository(maxAttributeCount: 5, maxAttributeLength: 16);

        // Act
        var attributes = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("{OriginalFormat}", "Test {Log}")
        };

        for (var i = 0; i < 10; i++)
        {
            var value = GetValue((i + 1) * 5);
            attributes.Add(new KeyValuePair<string, string>($"Key{i}", value));
        }

        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(message: GetValue(50), attributes: attributes) }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                Assert.Equal("Test {Log}", resource.OriginalFormat);
                Assert.Equal("0123456789012345", resource.Message);
                Assert.Collection(resource.Attributes,
                    p =>
                    {
                        Assert.Equal("Key0", p.Key);
                        Assert.Equal("01234", p.Value);
                    },
                    p =>
                    {
                        Assert.Equal("Key1", p.Key);
                        Assert.Equal("0123456789", p.Value);
                    },
                    p =>
                    {
                        Assert.Equal("Key2", p.Key);
                        Assert.Equal("012345678901234", p.Value);
                    },
                    p =>
                    {
                        Assert.Equal("Key3", p.Key);
                        Assert.Equal("0123456789012345", p.Value);
                    },
                    p =>
                    {
                        Assert.Equal("Key4", p.Key);
                        Assert.Equal("0123456789012345", p.Value);
                    });
            });
    }

    [Fact]
    public async Task Subscription_MultipleUpdates_MinExecuteIntervalApplied()
    {
        // Arrange
        var minExecuteInterval = TimeSpan.FromMilliseconds(500);
        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper);
        var logger = loggerFactory.CreateLogger(nameof(LogTests));
        var repository = CreateRepository(subscriptionMinExecuteInterval: minExecuteInterval, loggerFactory: loggerFactory);
        var stopwatch = new Stopwatch();

        var callCount = 0;
        var resultChannel = Channel.CreateUnbounded<int>();
        var subscription = repository.OnNewLogs(resourceKey: null, SubscriptionType.Read, async () =>
        {
            if (!stopwatch.IsRunning)
            {
                stopwatch.Start();
            }
            else
            {
                stopwatch.Stop();
            }
            ++callCount;
            resultChannel.Writer.TryWrite(callCount);
            await Task.Delay(20);
        });

        // Act
        var addContext = new AddContext();
        logger.LogInformation("Writing log 1");
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });

        // Assert
        var read1 = await resultChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(1, read1);
        logger.LogInformation("Received log 1 callback");

        logger.LogInformation("Writing log 2");
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });

        var read2 = await resultChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(2, read2);
        logger.LogInformation("Received log 2 callback");

        var elapsed = stopwatch.Elapsed;
        logger.LogInformation("Elapsed time: {Elapsed}", elapsed);
        CustomAssert.AssertExceedsMinInterval(elapsed, minExecuteInterval);
    }

    [Fact]
    public void FilterLogs_With_Message_Returns_CorrectLog()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "test_message", severity: SeverityNumber.Error),
                        }
                    }
                }
            }
        });

        var resourceKey = repository.GetResources().First().ResourceKey;

        // Assert
        Assert.Empty(repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 1,
            Filters = [new FieldTelemetryFilter { Condition = FilterCondition.Contains, Field = nameof(OtlpLogEntry.Message), Value = "does_not_contain" }]
        }).Items);

        Assert.Single(repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 1,
            Filters = [new FieldTelemetryFilter { Condition = FilterCondition.Contains, Field = nameof(OtlpLogEntry.Message), Value = "message" }]
        }).Items);
    }

    [Fact]
    public void FilterLogs_With_EventName_Returns_CorrectLog()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(instanceId: "1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "test_message", severity: SeverityNumber.Error, eventName: "MyEventName"),
                        }
                    }
                }
            }
        });

        var resourceKey = repository.GetResources().First().ResourceKey;

        // Assert
        Assert.Empty(repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 1,
            Filters = [new FieldTelemetryFilter { Condition = FilterCondition.Contains, Field = KnownStructuredLogFields.EventNameField, Value = "does_not_contain" }]
        }).Items);

        Assert.Single(repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 1,
            Filters = [new FieldTelemetryFilter { Condition = FilterCondition.Contains, Field = KnownStructuredLogFields.EventNameField, Value = "MyEvent" }]
        }).Items);
    }

    [Fact]
    public void AddLogs_MultipleResources_SameInstanceId_CreateMultipleResources()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "App1", instanceId: "computer-name"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "App2", instanceId: "computer-name"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("App1", resource.ResourceName);
                Assert.Equal("computer-name", resource.InstanceId);
            },
            resource =>
            {
                Assert.Equal("App2", resource.ResourceName);
                Assert.Equal("computer-name", resource.InstanceId);
            });

        var logs1 = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs1.Items,
            resource =>
            {
                Assert.Equal("546573745370616e4964", resource.SpanId);
                Assert.Equal("5465737454726163654964", resource.TraceId);
                Assert.Equal("Test {Log}", resource.OriginalFormat);
                Assert.Equal("Test Value!", resource.Message);
                Assert.Equal("TestLogger", resource.Scope.Name);
                Assert.Collection(resource.Attributes,
                    p =>
                    {
                        Assert.Equal("Log", p.Key);
                        Assert.Equal("Value!", p.Value);
                    });
            });

        var logs2 = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resources[1].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs2.Items,
            resource =>
            {
                Assert.Equal("546573745370616e4964", resource.SpanId);
                Assert.Equal("5465737454726163654964", resource.TraceId);
                Assert.Equal("Test {Log}", resource.OriginalFormat);
                Assert.Equal("Test Value!", resource.Message);
                Assert.Equal("TestLogger", resource.Scope.Name);
                Assert.Collection(resource.Attributes,
                    p =>
                    {
                        Assert.Equal("Log", p.Key);
                        Assert.Equal("Value!", p.Value);
                    });
            });
    }

    [Fact]
    public void GetLogs_MultipleInstances()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(1), message: "message-1", attributes: [KeyValuePair.Create("key-1", "value-1")]) }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(2), message: "message-2", attributes: [KeyValuePair.Create("key-2", "value-2")]) }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource2"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(3)) }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resourceKey = new ResourceKey("resource1", InstanceId: null);
        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                Assert.Equal("message-1", resource.Message);
                Assert.Equal("TestLogger", resource.Scope.Name);
                Assert.Collection(resource.Attributes,
                    p =>
                    {
                        Assert.Equal("key-1", p.Key);
                        Assert.Equal("value-1", p.Value);
                    });
            },
            resource =>
            {
                Assert.Equal("message-2", resource.Message);
                Assert.Equal("TestLogger", resource.Scope.Name);
                Assert.Collection(resource.Attributes,
                    p =>
                    {
                        Assert.Equal("key-2", p.Key);
                        Assert.Equal("value-2", p.Value);
                    });
            });

        var propertyKeys = repository.GetLogPropertyKeys(resourceKey)!;
        Assert.Collection(propertyKeys,
            s => Assert.Equal("key-1", s),
            s => Assert.Equal("key-2", s));
    }

    [Fact]
    public void RemoveLogs_All()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(1), message: "message-1") }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(2), message: "message-2") }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource2"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(3)) }
                    }
                }
            }
        });

        // Act
        repository.ClearStructuredLogs();

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.NotNull(logs);
        Assert.Empty(logs.Items);
        Assert.Equal(0, logs.TotalItemCount);
    }

    [Fact]
    public void RemoveLogs_SelectedResource()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(1), message: "message-1") }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(2), message: "message-2") }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource2"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(3), message: "message-3") }
                    }
                }
            }
        });

        // Act
        repository.ClearStructuredLogs(new ResourceKey("resource1", "123"));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Equal(2, logs.TotalItemCount);
        Assert.Collection(logs.Items,
                    resource =>
                    {
                        Assert.Equal("message-2", resource.Message);
                        Assert.Equal("TestLogger", resource.Scope.Name);
                    },
                    resource =>
                    {
                        Assert.Equal("message-3", resource.Message);
                        Assert.Equal("TestLogger", resource.Scope.Name);
                    });
    }

    [Fact]
    public void RemoveLogs_MultipleSelectedResources()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(1), message: "message-1") }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(2), message: "message-2") }
                    }
                }
            },
            new ResourceLogs
            {
                Resource = CreateResource(name: "resource2"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(3), message: "message-3") }
                    }
                }
            }
        });

        // Act
        repository.ClearStructuredLogs(new ResourceKey("resource1", null));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Equal(1, logs.TotalItemCount);
        var log = Assert.Single(logs.Items);
        Assert.Equal("message-3", log.Message);
        Assert.Equal("TestLogger", log.Scope.Name);
    }

    [Fact]
    public void AddLogs_ObservedUnixTimeNanos()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: DateTime.UnixEpoch, observedTime: s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                Assert.Equal(s_testTime.AddMinutes(1), resource.TimeStamp);
            });
    }

    [Fact]
    public void AddLogs_EventName_FromLogRecordField()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(eventName: "TestEvent") }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                Assert.Equal("TestEvent", resource.EventName);
            });
    }

    [Fact]
    public void AddLogs_EventName_FromLegacyAttribute()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(attributes: [new KeyValuePair<string, string>("logrecord.event.name", "LegacyEvent")]) }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                Assert.Equal("LegacyEvent", resource.EventName);
                // Legacy attribute should be filtered out
                Assert.DoesNotContain(resource.Attributes, a => a.Key == "logrecord.event.name");
            });
    }

    [Fact]
    public void AddLogs_EventName_FieldTakesPrecedenceOverAttribute()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(eventName: "FieldEvent", attributes: [new KeyValuePair<string, string>("logrecord.event.name", "AttributeEvent")]) }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                // Field should take precedence over attribute
                Assert.Equal("FieldEvent", resource.EventName);
            });
    }

    [Fact]
    public void AddLogs_EventName_NullWhenNotSet()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(attributes: []) }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(logs.Items,
            resource =>
            {
                Assert.Null(resource.EventName);
            });
    }
}
