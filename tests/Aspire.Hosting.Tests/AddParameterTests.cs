// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AddParameterTests
{
    [Fact]
    public async Task ContextualParameterWaitsAreCountedPerCaller()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = builder.AddParameter("first", secret: true).Resource;
        var second = builder.AddParameter("second").Resource;
        first.EnsureValueTask();
        second.EnsureValueTask();
        var caller = builder.AddExecutable("caller", "unused", ".").Resource;
        var other = builder.AddExecutable("other", "unused", ".").Resource;
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        using var canceledRead = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var startingState = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info);
        await app.ResourceNotifications.PublishUpdateAsync(caller, s => s with { State = startingState });
        await app.ResourceNotifications.PublishUpdateAsync(other, s => s with { State = startingState });
        var context = new ValueProviderContext { ExecutionContext = builder.ExecutionContext, Caller = caller };
        var otherContext = new ValueProviderContext { ExecutionContext = builder.ExecutionContext, Caller = other };

        var canceled = first.GetValueAsync(context, canceledRead.Token).AsTask();
        var firstRead = ((IValueProvider)first).GetValueAsync(context, cts.Token).AsTask();
        var secondRead = second.GetValueAsync(context, cts.Token).AsTask();
        var otherRead = first.GetValueAsync(otherContext, cts.Token).AsTask();
        AssertParameterWaitState(app, caller, KnownResourceStates.UnresolvedParameters, "first", "second");
        AssertParameterWaitState(app, other, KnownResourceStates.UnresolvedParameters, "first");

        // A replayed startup snapshot must not hide an outstanding read.
        await app.ResourceNotifications.PublishUpdateAsync(caller, s => s with { State = startingState });
        canceledRead.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled).DefaultTimeout();
        AssertParameterWaitState(app, caller, KnownResourceStates.UnresolvedParameters, "first", "second");

        await first.SetValueAsync("secret-value");
        Assert.Equal("secret-value", await firstRead.DefaultTimeout());
        Assert.Equal("secret-value", await otherRead.DefaultTimeout());
        AssertParameterWaitState(app, caller, KnownResourceStates.UnresolvedParameters, "second");
        AssertParameterWaitState(app, other, KnownResourceStates.Starting);

        await second.SetValueAsync("second-value");
        Assert.Equal("second-value", await secondRead.DefaultTimeout());
        AssertParameterWaitState(app, caller, KnownResourceStates.Starting);
        Assert.True(app.ResourceNotifications.TryGetCurrentState(caller.Name, out var state));
        Assert.Equal(startingState, state.Snapshot.State);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("FailedToStart")]
    [InlineData("Exited")]
    [InlineData("Stopping")]
    [InlineData("Waiting")]
    [InlineData("Building")]
    [InlineData("NotStarted")]
    public async Task CompletingParameterWaitDoesNotOverwriteNewerState(string newerState)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parameter = builder.AddParameter("parameter").Resource;
        parameter.EnsureValueTask();
        var caller = builder.AddExecutable("caller", "unused", ".")
            .WaitFor(builder.AddExecutable("dependency", "unused", ".")).Resource;
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        await app.ResourceNotifications.PublishUpdateAsync(caller, s => s with { State = KnownResourceStates.Starting });
        var read = parameter.GetValueAsync(new ValueProviderContext { ExecutionContext = builder.ExecutionContext, Caller = caller }, cts.Token).AsTask();
        AssertParameterWaitState(app, caller, KnownResourceStates.UnresolvedParameters, "parameter");

        await app.ResourceNotifications.PublishUpdateAsync(caller, s => s with { State = newerState });
        AssertParameterWaitState(app, caller, newerState);
        await parameter.SetValueAsync("value");
        Assert.Equal("value", await read.DefaultTimeout());
        AssertParameterWaitState(app, caller, newerState);
    }

    [Fact]
    public async Task ParameterWaitRestoresCustomResourceInitializationState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parameter = builder.AddParameter("parameter").Resource;
        parameter.EnsureValueTask();
        var caller = builder.AddExternalService("remote", builder.CreateResourceBuilder(parameter)).Resource;
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        await app.ResourceNotifications.PublishUpdateAsync(caller, s => s with { State = KnownResourceStates.Waiting });

        var read = parameter.GetValueAsync(new ValueProviderContext { ExecutionContext = builder.ExecutionContext, Caller = caller }, cts.Token).AsTask();
        AssertParameterWaitState(app, caller, KnownResourceStates.UnresolvedParameters, "parameter");
        await parameter.SetValueAsync("https://example.com");
        Assert.Equal("https://example.com", await read.DefaultTimeout());
        AssertParameterWaitState(app, caller, KnownResourceStates.Waiting);
    }

    [Fact]
    public async Task FaultedParameterWaitIsRemovedAndExceptionIsPreserved()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parameter = builder.AddParameter("parameter").Resource;
        parameter.EnsureValueTask();
        var caller = builder.AddExecutable("caller", "unused", ".").Resource;
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        await app.ResourceNotifications.PublishUpdateAsync(caller, s => s with { State = KnownResourceStates.Starting });
        var context = new ValueProviderContext { ExecutionContext = builder.ExecutionContext, Caller = caller };
        var read = parameter.GetValueAsync(context, cts.Token).AsTask();
        AssertParameterWaitState(app, caller, KnownResourceStates.UnresolvedParameters, "parameter");

        var error = new InvalidOperationException("Failed to load parameter.");
        await parameter.SetExceptionAsync(error);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => read).DefaultTimeout());
        AssertParameterWaitState(app, caller, KnownResourceStates.Starting);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => parameter.GetValueAsync(context, cts.Token).AsTask()));
        AssertParameterWaitState(app, caller, KnownResourceStates.Starting);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("arguments")]
    [InlineData("callback")]
    [InlineData("external-service")]
    [InlineData("connection-string")]
    public async Task StartupParameterWaitsFollowActualValueResolution(string source)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var selected = builder.AddParameter("selected");
        var unused = builder.AddParameter("unused");
        var condition = builder.AddParameter("condition", "true");
        selected.Resource.EnsureValueTask();
        unused.Resource.EnsureValueTask();
        var expression = ReferenceExpression.CreateConditional(
            condition.Resource, "true", ReferenceExpression.Create($"{selected}"), ReferenceExpression.Create($"{unused}"));
        var caller = builder.AddExecutable("caller", "unused", ".");
        var callbackCount = 0;
        switch (source)
        {
            case "environment":
                caller.WithEnvironment("VALUE", expression);
                break;
            case "arguments":
                caller.WithArgs(expression);
                break;
            case "callback":
                caller.WithEnvironment(async context =>
                {
                    callbackCount++;
                    context.EnvironmentVariables["VALUE"] = (await selected.Resource.GetValueAsync(
                        new ValueProviderContext { ExecutionContext = context.ExecutionContext, Caller = context.Resource },
                        context.CancellationToken))!;
                });
                break;
            case "external-service":
                caller.WithEnvironment("VALUE", builder.AddExternalService("remote", selected));
                break;
            case "connection-string":
                caller.WithReference(builder.AddResource(new ConnectionStringResource("connection", expression)));
                break;
        }
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        await app.ResourceNotifications.PublishUpdateAsync(caller.Resource, s => s with { State = KnownResourceStates.Starting });
        AssertParameterWaitState(app, caller.Resource, KnownResourceStates.Starting);

        var configuration = ExecutionConfigurationBuilder.Create(caller.Resource)
            .WithArgumentsConfig().WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, cancellationToken: cts.Token);
        AssertParameterWaitState(app, caller.Resource, KnownResourceStates.UnresolvedParameters, "selected");

        await selected.Resource.SetValueAsync("https://example.com");
        var result = await configuration.DefaultTimeout();
        Assert.Null(result.Exception);
        AssertParameterWaitState(app, caller.Resource, KnownResourceStates.Starting);
        Assert.False(unused.Resource.WaitForValueTcs!.Task.IsCompleted);
        Assert.Equal(source == "callback" ? 1 : 0, callbackCount);
    }

    [Fact]
    public async Task ContextFreeAndPublishParameterReadsDoNotReportCallerWaits()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parameter = builder.AddParameter("parameter").Resource;
        parameter.EnsureValueTask();
        var caller = builder.AddExecutable("caller", "unused", ".").Resource;
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        await app.ResourceNotifications.PublishUpdateAsync(caller, s => s with { State = KnownResourceStates.Starting });

        var reads = new[]
        {
            parameter.GetValueAsync(new ValueProviderContext { ExecutionContext = builder.ExecutionContext }, cts.Token).AsTask(),
            parameter.GetValueAsync(new ValueProviderContext { Caller = caller }, cts.Token).AsTask(),
            parameter.GetValueAsync(new ValueProviderContext { Caller = caller, ExecutionContext = new(DistributedApplicationOperation.Publish) }, cts.Token).AsTask(),
            parameter.GetValueAsync(cts.Token).AsTask()
        };
        AssertParameterWaitState(app, caller, KnownResourceStates.Starting);
        await parameter.SetValueAsync("value");
        Assert.Equal(["value", "value", "value", "value"], await Task.WhenAll(reads).DefaultTimeout());
        AssertParameterWaitState(app, caller, KnownResourceStates.Starting);
    }

    [Fact]
    public async Task ConnectionStringParameterWaitsAreRemovedFromOriginalReplicaIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parameter = Assert.IsAssignableFrom<ParameterResource>(builder.AddConnectionString("connection").Resource);
        parameter.EnsureValueTask();
        var caller = builder.AddExecutable("caller", "unused", ".")
            .WithAnnotation(new DcpInstancesAnnotation([new("caller-first", "first", 0), new("caller-second", "second", 1)]));
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        await app.ResourceNotifications.PublishUpdateAsync(caller.Resource, s => s with { State = KnownResourceStates.Starting });
        var read = ((IValueProvider)parameter).GetValueAsync(
            new ValueProviderContext { ExecutionContext = builder.ExecutionContext, Caller = caller.Resource }, cts.Token).AsTask();
        foreach (var id in new[] { "caller-first", "caller-second" })
        {
            Assert.True(app.ResourceNotifications.TryGetCurrentState(id, out var state));
            Assert.Equal(KnownResourceStates.UnresolvedParameters, state.Snapshot.State?.Text);
        }

        caller.WithAnnotation(new DcpInstancesAnnotation([new("caller-replacement", "replacement", 0)]), ResourceAnnotationMutationBehavior.Replace);
        await app.ResourceNotifications.PublishUpdateAsync(caller.Resource, "caller-second", s => s with { State = KnownResourceStates.Running });
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).DefaultTimeout();
        Assert.True(app.ResourceNotifications.TryGetCurrentState("caller-first", out var first));
        Assert.Equal(KnownResourceStates.Starting, first.Snapshot.State?.Text);
        Assert.True(app.ResourceNotifications.TryGetCurrentState("caller-second", out var second));
        Assert.Equal(KnownResourceStates.Running, second.Snapshot.State?.Text);
        Assert.Empty(first.Snapshot.Properties.Where(p => p.Name == KnownProperties.Resource.UnresolvedParameters));
        Assert.Empty(second.Snapshot.Properties.Where(p => p.Name == KnownProperties.Resource.UnresolvedParameters));
        Assert.False(app.ResourceNotifications.TryGetCurrentState("caller-replacement", out _));
    }

    [Fact]
    public async Task PgAdminReportsItsOwnParameterWaitWhileGeneratingConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", secret: true);
        var postgres = builder.AddPostgres("postgres", password: password).WithPgAdmin();
        using var app = builder.Build();
        password.Resource.EnsureValueTask();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var pgadmin = Assert.Single(builder.Resources, r => r.Name == "pgadmin");
        await app.ResourceNotifications.PublishUpdateAsync(pgadmin, s => s with { State = KnownResourceStates.Starting });
        var callback = Assert.Single(pgadmin.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());

        var files = callback.Callback(new() { Model = pgadmin, Services = app.Services }, cts.Token);
        AssertParameterWaitState(app, pgadmin, KnownResourceStates.UnresolvedParameters, "password");
        Assert.False(app.ResourceNotifications.TryGetCurrentState(postgres.Resource.Name, out _));

        await password.Resource.SetValueAsync("password-value");
        Assert.Single(await files.DefaultTimeout());
        AssertParameterWaitState(app, pgadmin, KnownResourceStates.Starting);
    }

    private static void AssertParameterWaitState(DistributedApplication app, IResource resource, string expectedState, params string[] parameters)
    {
        Assert.True(app.ResourceNotifications.TryGetCurrentState(resource.Name, out var state));
        Assert.Equal(expectedState, state.Snapshot.State?.Text);
        var property = state.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.UnresolvedParameters);
        if (parameters.Length == 0)
        {
            Assert.Null(property);
        }
        else
        {
            Assert.NotNull(property);
            Assert.Equal(parameters, Assert.IsType<string[]>(property.Value));
            Assert.False(property.IsSensitive);
            Assert.Equal(KnownResourceStateStyles.Warn, state.Snapshot.State?.Style);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateParameterFromConfigurationPreservesOverridableDefault(bool overridden)
    {
        var configuration = new ConfigurationManager();
        var parameter = ParameterResourceBuilderExtensions.CreateParameter(configuration, "public-origin", secret: false, "http://localhost:8080");

        if (overridden)
        {
            configuration["Parameters:public_origin"] = "https://public.example";
        }
        Assert.Equal(overridden ? "https://public.example" : "http://localhost:8080", await parameter.GetValueAsync(default));
        Assert.Equal("http://localhost:8080", parameter.Default!.GetDefaultValue());
    }

    [Fact]
    public async Task CreateParameterFromConfigurationUsesStandardValueLookup()
    {
        var configuration = new ConfigurationManager();
        var parameter = ParameterResourceBuilderExtensions.CreateParameter(configuration, "public-origin", secret: false);
        Assert.True(parameter.Required);
        Assert.False(parameter.Secret);
        Assert.Null(parameter.Default);
        Assert.Equal("Parameters:public-origin", parameter.ConfigurationKey);
        var missing = ParameterResourceBuilderExtensions.CreateParameter(configuration, "missing", secret: false);
        await Assert.ThrowsAsync<MissingParameterValueException>(async () => await missing.GetValueAsync(default));

        configuration["Parameters:public_origin"] = "https://public.example";
        Assert.Equal("https://public.example", await parameter.GetValueAsync(default));
    }

    [Fact]
    public void ParametersAreVisibleByDefault()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Configuration["Parameters:pass"] = "pass1";

        appBuilder.AddParameter("pass", secret: true);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());
        var annotation = parameterResource.Annotations.OfType<ResourceSnapshotAnnotation>().SingleOrDefault();

        Assert.NotNull(annotation);

        var state = annotation.InitialSnapshot;

        Assert.False(state.IsHidden);
        Assert.Collection(state.Properties,
            prop =>
            {
                Assert.Equal(CustomResourceKnownProperties.Source, prop.Name);
                Assert.Equal("Parameters:pass", prop.Value);
            });
    }

    [Fact]
    public void ParametersAreRequiredByDefault()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var parameter = appBuilder.AddParameter("test");

        Assert.True(parameter.Resource.Required);
    }

    [Fact]
    public void ParameterWithOptional_MarksParameterAsNotRequired()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var parameter = appBuilder.AddParameter("test")
            .WithOptional();

        Assert.False(parameter.Resource.Required);
    }

    [Fact]
    public void ParameterWithRequired_SetsRequiredFlag()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var parameter = appBuilder.AddParameter("test")
            .WithOptional()
            .WithRequired();

        Assert.True(parameter.Resource.Required);
    }

    [Fact]
    public async Task ParameterWithOptional_AfterMissingValueWasProbed_ResolvesToNull()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test");

        Assert.False(parameter.Resource.TryGetCurrentValue(out _));

        parameter.WithOptional();

        Assert.Null(await parameter.Resource.GetValueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ParameterWithRequired_AfterOptionalMissingValueWasProbed_Throws()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithOptional();

        Assert.False(parameter.Resource.TryGetCurrentValue(out _));

        parameter.WithRequired();

        await Assert.ThrowsAsync<MissingParameterValueException>(
            () => parameter.Resource.GetValueAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ParameterWithOptional_AfterRequiredValueTaskWasFaulted_ResolvesToNull()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test");

        await parameter.Resource.SetValueAsync(null).DefaultTimeout();

        parameter.WithOptional();

        Assert.Null(await parameter.Resource.GetValueAsync(CancellationToken.None).DefaultTimeout());
    }

    [Fact]
    public async Task ParameterWithRequired_AfterOptionalValueTaskResolvedToNull_Throws()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithOptional();

        await parameter.Resource.SetValueAsync(null).DefaultTimeout();

        parameter.WithRequired();

        await Assert.ThrowsAsync<MissingParameterValueException>(
            () => parameter.Resource.GetValueAsync(CancellationToken.None).AsTask()).DefaultTimeout();
    }

    [Fact]
    public async Task OptionalParameterWithoutValue_ResolvesToNull()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var parameter = appBuilder.AddParameter("optional")
            .WithOptional();

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "optional");

        Assert.Null(await parameterResource.GetValueAsync(default));
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal(string.Empty, parameter.Resource.Value);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task OptionalParameterSetValueAsync_CanSetAndClearValue()
    {
        var parameter = new ParameterResource("optional", _ => throw new MissingParameterValueException("Parameter 'optional' is missing."), secret: false)
        {
            Required = false
        };

        Assert.False(parameter.TryGetCurrentValue(out _));

        await parameter.SetValueAsync("configured").DefaultTimeout();

        Assert.True(parameter.TryGetCurrentValue(out var configuredValue));
        Assert.Equal("configured", configuredValue);

        await parameter.SetValueAsync().DefaultTimeout();

        Assert.False(parameter.TryGetCurrentValue(out _));
        Assert.Null(await parameter.GetValueAsync(CancellationToken.None).DefaultTimeout());
    }

    [Fact]
    public async Task ParameterTrySetValue_CompletesParameterWithProgrammaticValue()
    {
        var parameter = new ParameterResource("test", _ => throw new MissingParameterValueException("Parameter 'test' is missing."), secret: false);

        Assert.False(parameter.TryGetCurrentValue(out _));
        Assert.True(parameter.TrySetValue("programmatic"));
        Assert.False(parameter.TrySetValue("replacement"));
        Assert.True(parameter.TryGetCurrentValue(out var value));
        Assert.Equal("programmatic", value);
        Assert.Equal("programmatic", await parameter.GetValueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ParameterTrySetException_CompletesParameterWithProgrammaticException()
    {
        var parameter = new ParameterResource("test", _ => "fallback", secret: false);
        var exception = new InvalidOperationException("Programmatic failure.");

        Assert.True(parameter.TrySetException(exception));
        Assert.False(parameter.TrySetValue("replacement"));
        Assert.False(parameter.TryGetCurrentValue(out _));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => parameter.GetValueAsync(CancellationToken.None).AsTask());
        Assert.Same(exception, thrown);
    }

    [Fact]
    public async Task ParameterSetValueAsync_ReplacesCurrentValue()
    {
        var parameter = new ParameterResource("test", _ => "initial", secret: false);

        Assert.True(parameter.TrySetValue("first"));

        await parameter.SetValueAsync("second").DefaultTimeout();

        Assert.True(parameter.TryGetCurrentValue(out var value));
        Assert.Equal("second", value);
        Assert.Equal("second", await parameter.GetValueAsync(CancellationToken.None).DefaultTimeout());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RequiredParameterSetValueAsync_WithMissingValueFaultsParameter(string? value)
    {
        var parameter = new ParameterResource("test", _ => "initial", secret: false);

        await parameter.SetValueAsync(value).DefaultTimeout();

        Assert.False(parameter.TryGetCurrentValue(out _));
        await Assert.ThrowsAsync<MissingParameterValueException>(() => parameter.GetValueAsync(CancellationToken.None).AsTask()).DefaultTimeout();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RequiredParameterTrySetValue_WithMissingValueFaultsParameter(string? value)
    {
        var parameter = new ParameterResource("test", _ => "initial", secret: false);

        Assert.True(parameter.TrySetValue(value));
        Assert.False(parameter.TrySetValue("replacement"));

        Assert.False(parameter.TryGetCurrentValue(out _));
        await Assert.ThrowsAsync<MissingParameterValueException>(() => parameter.GetValueAsync(CancellationToken.None).AsTask()).DefaultTimeout();
    }

    [Fact]
    public async Task ParameterSetExceptionAsync_ReplacesCurrentValueWithException()
    {
        var parameter = new ParameterResource("test", _ => "initial", secret: false);
        var exception = new InvalidOperationException("Programmatic failure.");

        await parameter.SetValueAsync("current").DefaultTimeout();
        await parameter.SetExceptionAsync(exception).DefaultTimeout();

        Assert.False(parameter.TryGetCurrentValue(out _));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => parameter.GetValueAsync(CancellationToken.None).AsTask()).DefaultTimeout();
        Assert.Same(exception, thrown);
    }

    [Fact]
    public async Task ParameterSetValueAsync_NotifiesAllObserversWhenOneThrows()
    {
        var parameter = new ParameterResource("test", _ => "initial", secret: false);
        var secondObserverInvoked = false;

        parameter.ValueChanged += (_, _, _) => throw new InvalidOperationException("boom");
        parameter.ValueChanged += (_, _, _) =>
        {
            secondObserverInvoked = true;
            return Task.CompletedTask;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => parameter.SetValueAsync("value")).DefaultTimeout();

        Assert.Equal("boom", thrown.Message);
        Assert.True(secondObserverInvoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParameterSetAsync_WhenAlreadyCanceled_DoesNotChangeValue(bool setException)
    {
        var parameter = new ParameterResource("test", _ => "initial", secret: false);
        await parameter.SetValueAsync("current").DefaultTimeout();
        var version = parameter.ValueChangeVersion;
        var notified = false;
        parameter.ValueChanged += (_, _, _) =>
        {
            notified = true;
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setException
            ? parameter.SetExceptionAsync(new InvalidOperationException("canceled"), cts.Token)
            : parameter.SetValueAsync("replacement", cts.Token)).DefaultTimeout();

        Assert.Equal("current", await parameter.GetValueAsync(CancellationToken.None).DefaultTimeout());
        Assert.Equal(version, parameter.ValueChangeVersion);
        Assert.False(notified);
    }

    [Fact]
    public async Task ParameterSetValueAsync_AggregatesMultipleObserverFailures()
    {
        var parameter = new ParameterResource("test", _ => "initial", secret: false);

        parameter.ValueChanged += (_, _, _) => throw new InvalidOperationException("first");
        parameter.ValueChanged += (_, _, _) => throw new InvalidOperationException("second");

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => parameter.SetValueAsync("value")).DefaultTimeout();

        Assert.Equal(2, aggregate.InnerExceptions.Count);
    }

    [Fact]
    public void ParameterSetValueAsync_TrySetSwallowsObserverFailures()
    {
        var parameter = new ParameterResource("test", _ => "initial", secret: false);

        parameter.ValueChanged += (_, _, _) => throw new InvalidOperationException("boom");

        Assert.True(parameter.TrySetValue("value"));
        Assert.True(parameter.TryGetCurrentValue(out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public void ParametersWithConfigurationValueDoNotGetDefaultValue()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:pass"] = "ValueFromConfiguration"
        });
        var parameter = appBuilder.AddParameter("pass");
        parameter.Resource.Default = new TestParameterDefault("DefaultValue");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal("ValueFromConfiguration", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Theory]
    // We test all the combinations of {direct param, callback param} x {config value, no config value}
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParametersWithDefaultValueStringOverloadUsedRegardlessOfConfigurationValue(bool useCallback, bool hasConfig)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        if (hasConfig)
        {
            appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Parameters:pass"] = "ValueFromConfiguration"
            });
        }

        if (useCallback)
        {
            appBuilder.AddParameter("pass", () => "DefaultValue");
        }
        else
        {
            appBuilder.AddParameter("pass", "DefaultValue");
        }

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Make sure the code value is used, ignoring any config value
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "pass");
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"DefaultValue", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        // The manifest should not include anything about the default value
        var paramManifest = await ManifestUtils.GetManifest(appModel.Resources.OfType<ParameterResource>().Single(r => r.Name == "pass")).DefaultTimeout();
        var expectedManifest = $$"""
            {
              "type": "parameter.v0",
              "value": "{pass.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string"
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, paramManifest.ToString());
    }

    [Fact]
    public async Task OptionalParameterCannotBePublishedToManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddParameter("optional")
            .WithOptional();

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameter = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "optional");
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ManifestUtils.GetManifest(parameter)).DefaultTimeout();

        Assert.Equal(
            "Optional parameter resource 'optional' cannot be published because the manifest schema does not support optional parameter inputs.",
            exception.Message);
    }

    [Theory]
    // We test all the combinations of {direct param, callback param} x {config value, no config value}
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParametersWithDefaultValueGetPublishedIfPublishFlagIsPassed(bool useCallback, bool hasConfig)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        if (hasConfig)
        {
            appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Parameters:pass"] = "ValueFromConfiguration"
            });
        }

        if (useCallback)
        {
            appBuilder.AddParameter("pass", () => "DefaultValue", publishValueAsDefault: true);
        }
        else
        {
            appBuilder.AddParameter("pass", "DefaultValue", publishValueAsDefault: true);
        }

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Make sure the code value is used, ignoring any config value
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "pass");
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"DefaultValue", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        // The manifest should include the default value, since we passed publishValueAsDefault: true
        var paramManifest = await ManifestUtils.GetManifest(appModel.Resources.OfType<ParameterResource>().Single(r => r.Name == "pass")).DefaultTimeout();
        var expectedManifest = $$"""
            {
              "type": "parameter.v0",
              "value": "{pass.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string",
                  "default": {
                    "value": "DefaultValue"
                  }
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, paramManifest.ToString());
    }

    [Fact]
    public void AddParameterWithBothPublishValueAsDefaultAndSecretFails()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        // publishValueAsDefault and secret are mutually exclusive. Test both overloads.
        var ex1 = Assert.Throws<ArgumentException>(() => appBuilder.AddParameter("pass", () => "SomeSecret", publishValueAsDefault: true, secret: true));
        Assert.Equal($"A parameter cannot be both secret and published as a default value. (Parameter 'secret')", ex1.Message);
        var ex2 = Assert.Throws<ArgumentException>(() => appBuilder.AddParameter("pass", "SomeSecret", publishValueAsDefault: true, secret: true));
        Assert.Equal($"A parameter cannot be both secret and published as a default value. (Parameter 'secret')", ex2.Message);
    }

    [Fact]
    public void AddParameterForPolyglotWithPublishValueAsDefaultAndNoValueFails()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var ex = Assert.Throws<ArgumentException>(() => ParameterResourceBuilderExtensions.AddParameterForPolyglot(appBuilder, "pass", publishValueAsDefault: true));

        Assert.Equal("A parameter value is required when publishValueAsDefault is true. (Parameter 'publishValueAsDefault')", ex.Message);
    }

    [Fact]
    public void AddParameterForPolyglotWithBothPublishValueAsDefaultAndSecretFails()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var ex = Assert.Throws<ArgumentException>(() =>
            ParameterResourceBuilderExtensions.AddParameterForPolyglot(appBuilder, "pass", "SomeSecret", publishValueAsDefault: true, secret: true));

        Assert.Equal("secret", ex.ParamName);
        Assert.Contains("A parameter cannot be both secret and published as a default value.", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParametersWithDefaultValueObjectOverloadUseConfigurationValueWhenPresent(bool hasConfig)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        if (hasConfig)
        {
            appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Parameters:pass"] = "ValueFromConfiguration"
            });
        }

        var genParam = new GenerateParameterDefault { MinLength = 10 };

        var parameter = appBuilder.AddParameter("pass", genParam);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Make sure the the generated default value is only used when there isn't a config value
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "pass");
        if (hasConfig)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            Assert.Equal("ValueFromConfiguration", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete
        }
        else
        {
#pragma warning disable CS0618 // Type or member is obsolete
            Assert.NotEqual("ValueFromConfiguration", parameterResource.Value);
            // We can't test the exact value since it's random, but we can test the length
            Assert.Equal(10, parameterResource.Value.Length);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        // The manifest should always include the fields for the generated default value
        var paramManifest = await ManifestUtils.GetManifest(appModel.Resources.OfType<ParameterResource>().Single(r => r.Name == "pass")).DefaultTimeout();
        var expectedManifest = $$"""
            {
              "type": "parameter.v0",
              "value": "{pass.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string",
                  "default": {
                    "generate": {
                      "minLength": 10
                    }
                  }
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, paramManifest.ToString());
    }

    [Fact]
    public void ParametersWithDefaultValueObjectOverloadOnlyGetWrappedWhenTheyShould()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        // Here it should get wrapped in UserSecretsParameterDefault, since we pass persist: true
        var parameter1 = appBuilder.AddParameter("val1", new GenerateParameterDefault(), persist: true);
        Assert.IsType<UserSecretsParameterDefault>(parameter1.Resource.Default);

        // Here it should not get wrapped, since we don't pass the persist flag
        var parameter2 = appBuilder.AddParameter("val2", new GenerateParameterDefault());
        Assert.IsType<GenerateParameterDefault>(parameter2.Resource.Default);
    }

    [Fact]
    public async Task ParametersCanGetValueFromNonDefaultConfigurationKeys()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:val"] = "ValueFromConfigurationParams",
            ["Auth:AccessToken"] = "MyAccessToken",
        });

        var parameter = appBuilder.AddParameterFromConfiguration("val", "Auth:AccessToken");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>(), r => r.Name == "val");
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"MyAccessToken", parameterResource.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        // The manifest is not affected by the custom configuration key
        var paramManifest = await ManifestUtils.GetManifest(appModel.Resources.OfType<ParameterResource>().Single(r => r.Name == "val")).DefaultTimeout();
        var expectedManifest = $$"""
                {
                  "type": "parameter.v0",
                  "value": "{val.inputs.value}",
                  "inputs": {
                    "value": {
                      "type": "string"
                    }
                  }
                }
                """;
        Assert.Equal(expectedManifest, paramManifest.ToString());
    }

    [Fact]
    public async Task AddConnectionStringParameterIsASecretParameterInTheManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddConnectionString("mycs");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var connectionStringResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        Assert.Equal("mycs", connectionStringResource.Name);
        var connectionStringManifest = await ManifestUtils.GetManifest(connectionStringResource).DefaultTimeout();

        var expectedManifest = $$"""
            {
              "type": "parameter.v0",
              "connectionString": "{mycs.value}",
              "value": "{mycs.inputs.value}",
              "inputs": {
                "value": {
                  "type": "string",
                  "secret": true
                }
              }
            }
            """;

        var s = connectionStringManifest.ToString();

        Assert.Equal(expectedManifest, s);
    }

    private sealed class TestParameterDefault(string defaultValue) : ParameterDefault
    {
        public override string GetDefaultValue() => defaultValue;

        public override void WriteToManifest(ManifestPublishingContext context)
        {
            throw new NotImplementedException();
        }
    }

    [Fact]
    public void ParameterWithDescription_SetsDescriptionProperty()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("This is a test parameter");

        // Assert
        Assert.Equal("This is a test parameter", parameter.Resource.Description);
        Assert.False(parameter.Resource.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterWithMarkdownDescription_SetsDescriptionAndMarkupProperties()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("This is a **markdown** description", enableMarkdown: true);

        // Assert
        Assert.Equal("This is a **markdown** description", parameter.Resource.Description);
        Assert.True(parameter.Resource.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterWithDescriptionAndCustomInput_AddsInputGeneratorAnnotation()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("Custom input parameter")
            .WithCustomInput(p => new InteractionInput
            {
                Name = "CustomInput",
                InputType = InputType.Number,
                Label = "Custom Label",
                Description = p.Description,
                EnableDescriptionMarkdown = p.EnableDescriptionMarkdown
            });

        // Assert
        Assert.Equal("Custom input parameter", parameter.Resource.Description);
        Assert.True(parameter.Resource.Annotations.OfType<InputGeneratorAnnotation>().Any());
    }

    [Fact]
    public void ParameterCreateInput_WithoutCustomGenerator_ReturnsDefaultInput()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("Test description");

        // Act
        var input = parameter.Resource.CreateInput();

        // Assert
        Assert.Equal(InputType.Text, input.InputType);
        Assert.Equal("test", input.Label);
        Assert.Equal("Test description", input.Description);
        Assert.Equal(string.Format(InteractionStrings.ParametersInputsParameterPlaceholder, "test"), input.Placeholder);
        Assert.False(input.EnableDescriptionMarkdown);
        Assert.True(input.Required);
    }

    [Fact]
    public void ParameterCreateInput_ForOptionalParameter_ReturnsOptionalInput()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithOptional();

        var input = parameter.Resource.CreateInput();

        Assert.False(input.Required);
    }

    [Fact]
    public void ParameterCreateInput_WithNameOverride_UsesOverrideAsInputName()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("Test description");

        // Act
        var input = parameter.Resource.CreateInput("value");

        // Assert
        Assert.Equal("value", input.Name);
        Assert.Equal("test", input.Label);
        Assert.Equal("Test description", input.Description);
        Assert.Equal(string.Format(InteractionStrings.ParametersInputsParameterPlaceholder, "test"), input.Placeholder);
    }

    [Fact]
    public void ParameterCreateInput_WithDynamicLoading_SetsDynamicLoading()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test");
        var dynamicLoading = new InputLoadOptions
        {
            AlwaysLoadOnStart = true,
            LoadCallback = _ => Task.CompletedTask
        };

        var input = parameter.Resource.CreateInput(dynamicLoading: dynamicLoading);

        Assert.Same(dynamicLoading, input.DynamicLoading);
    }

    [Fact]
    public void ParameterCreateInput_ForSecretParameter_ReturnsSecretTextInput()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("secret", secret: true)
            .WithDescription("Secret description");

        // Act
        var input = parameter.Resource.CreateInput();

        // Assert
        Assert.Equal(InputType.SecretText, input.InputType);
        Assert.Equal("secret", input.Label);
        Assert.Equal("Secret description", input.Description);
        Assert.Equal(string.Format(InteractionStrings.ParametersInputsParameterPlaceholder, "secret"), input.Placeholder);
        Assert.False(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterCreateInput_WithCustomGenerator_UsesGenerator()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("Custom description")
            .WithCustomInput(p => new InteractionInput
            {
                Name = "TestParameter",
                InputType = InputType.Number,
                Label = "Custom Label",
                Description = "Custom: " + p.Description,
                EnableDescriptionMarkdown = true,
                Placeholder = "Enter number"
            });

        // Act
        var input = parameter.Resource.CreateInput();

        // Assert
        Assert.Equal(InputType.Number, input.InputType);
        Assert.Equal("Custom Label", input.Label);
        Assert.Equal("Custom: Custom description", input.Description);
        Assert.Equal("Enter number", input.Placeholder);
        Assert.True(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterCreateInput_WithCustomGenerator_PreservesGeneratorRequiredForRequiredParameter()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithCustomInput(_ => new InteractionInput
            {
                Name = "CustomInput",
                InputType = InputType.Text,
                Required = false
            });

        var input = parameter.Resource.CreateInput();

        Assert.False(input.Required);
    }

    [Fact]
    public void ParameterCreateInput_WithCustomGenerator_ForcesNotRequiredForOptionalParameter()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithOptional()
            .WithCustomInput(_ => new InteractionInput
            {
                Name = "CustomInput",
                InputType = InputType.Text,
                Required = true
            });

        var input = parameter.Resource.CreateInput();

        Assert.False(input.Required);
    }

    [Fact]
    public void ParameterCreateInput_WithCustomGeneratorAndExplicitRequired_OverridesGenerator()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithOptional()
            .WithCustomInput(_ => new InteractionInput
            {
                Name = "CustomInput",
                InputType = InputType.Text,
                Required = false
            });

        var input = parameter.Resource.CreateInput(required: true);

        Assert.True(input.Required);
    }

    [Fact]
    public void ParameterCreateInput_WithCustomGeneratorAndNameOverride_UsesOverrideAsInputName()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithCustomInput(_ => new InteractionInput
            {
                Name = "CustomInput",
                InputType = InputType.Number,
                Label = "Custom Label",
                Description = "Custom description",
                Placeholder = "Enter number",
                Value = "5"
            });

        // Act
        var input = parameter.Resource.CreateInput("value");

        // Assert
        Assert.Equal("value", input.Name);
        Assert.Equal(InputType.Number, input.InputType);
        Assert.Equal("Custom Label", input.Label);
        Assert.Equal("Custom description", input.Description);
        Assert.Equal("Enter number", input.Placeholder);
        Assert.Equal("5", input.Value);
    }

    [Fact]
    public void ParameterCreateInput_WithCustomGeneratorAndDynamicLoading_UsesDynamicLoading()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithCustomInput(_ => new InteractionInput
            {
                Name = "CustomInput",
                InputType = InputType.Number,
                Label = "Custom Label"
            });
        var dynamicLoading = new InputLoadOptions
        {
            AlwaysLoadOnStart = true,
            LoadCallback = _ => Task.CompletedTask
        };

        var input = parameter.Resource.CreateInput(dynamicLoading: dynamicLoading);

        Assert.Same(dynamicLoading, input.DynamicLoading);
    }

    [Fact]
    public void ParameterCreateInput_WithMarkdownDescription_SetsMarkupFlag()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        var parameter = appBuilder.AddParameter("test")
            .WithDescription("**Bold** description", enableMarkdown: true);

        // Act
        var input = parameter.Resource.CreateInput();

        // Assert
        Assert.Equal("**Bold** description", input.Description);
        Assert.True(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterWithCustomInput_AddsInputGeneratorAnnotation()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();

        // Act
        var parameter = appBuilder.AddParameter("test")
            .WithCustomInput(p => new InteractionInput
            {
                Name = "TestParam",
                InputType = InputType.Number,
                Label = "Custom Label",
                Description = "Custom description",
                EnableDescriptionMarkdown = false
            });

        // Assert
        Assert.True(parameter.Resource.Annotations.OfType<InputGeneratorAnnotation>().Any());

        var input = parameter.Resource.CreateInput();
        Assert.Equal(InputType.Number, input.InputType);
        Assert.Equal("Custom Label", input.Label);
        Assert.Equal("Custom description", input.Description);
        Assert.False(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void ParameterWithPolyglotCustomInput_AddsInputGeneratorAnnotation()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var parameter = appBuilder.AddParameter("worker-count")
            .WithDescription("**Worker** count", enableMarkdown: true);

        InvokeWithCustomInputForPolyglot(parameter, new Dictionary<string, object?>
        {
            [nameof(InteractionInput.InputType)] = InputType.Number,
            [nameof(InteractionInput.Label)] = "Worker Count",
            [nameof(InteractionInput.Placeholder)] = "Enter number (1-10)",
            [nameof(InteractionInput.Options)] = new Dictionary<string, string>
            {
                ["one"] = "One",
                ["two"] = "Two"
            },
            [nameof(InteractionInput.Value)] = "2",
            [nameof(InteractionInput.MaxLength)] = 2
        });

        Assert.True(parameter.Resource.Annotations.OfType<InputGeneratorAnnotation>().Any());

        var input = parameter.Resource.CreateInput("value", required: true);
        Assert.Equal("value", input.Name);
        Assert.Equal(InputType.Number, input.InputType);
        Assert.Equal("Worker Count", input.Label);
        Assert.Equal("**Worker** count", input.Description);
        Assert.True(input.EnableDescriptionMarkdown);
        Assert.Equal("Enter number (1-10)", input.Placeholder);
        Assert.Equal("2", input.Value);
        Assert.True(input.Required);
        Assert.Equal(2, input.MaxLength);
        Assert.Collection(input.Options!,
            option =>
            {
                Assert.Equal("one", option.Key);
                Assert.Equal("One", option.Value);
            },
            option =>
            {
                Assert.Equal("two", option.Key);
                Assert.Equal("Two", option.Value);
            });
    }

    [Fact]
    public void ParameterWithPolyglotCustomInput_DefaultsToSecretTextForSecretParameter()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var parameter = appBuilder.AddParameter("api-key", secret: true);
        InvokeWithCustomInputForPolyglot(parameter);

        var input = parameter.Resource.CreateInput();
        Assert.Equal(InputType.SecretText, input.InputType);
        Assert.Equal("api-key", input.Label);
        Assert.Equal(string.Format(InteractionStrings.ParametersInputsParameterPlaceholder, "api-key"), input.Placeholder);
    }

    [Fact]
    public void ParameterWithPolyglotCustomInput_IgnoresEmptyOptionalValues()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var parameter = appBuilder.AddParameter("worker-count")
            .WithDescription("**Worker** count", enableMarkdown: true);

        InvokeWithCustomInputForPolyglot(parameter, new Dictionary<string, object?>
        {
            [nameof(InteractionInput.Label)] = "",
            [nameof(InteractionInput.Description)] = "",
            [nameof(InteractionInput.Placeholder)] = "",
            [nameof(InteractionInput.Value)] = "",
            [nameof(InteractionInput.Options)] = new Dictionary<string, string>()
        });

        var input = parameter.Resource.CreateInput();
        Assert.Equal("worker-count", input.Label);
        Assert.Equal("**Worker** count", input.Description);
        Assert.True(input.EnableDescriptionMarkdown);
        Assert.Equal(string.Format(InteractionStrings.ParametersInputsParameterPlaceholder, "worker-count"), input.Placeholder);
        Assert.Null(input.Value);
        Assert.Null(input.Options);
    }

    private static void InvokeWithCustomInputForPolyglot(IResourceBuilder<ParameterResource> parameter, IReadOnlyDictionary<string, object?>? properties = null)
    {
        var hostingAssembly = typeof(ParameterResourceBuilderExtensions).Assembly;
        var optionsType = hostingAssembly.GetType("Aspire.Hosting.Ats.ParameterCustomInputOptions", throwOnError: true)!;
        var options = Activator.CreateInstance(optionsType, nonPublic: true)!;

        foreach (var (propertyName, value) in properties ?? new Dictionary<string, object?>())
        {
            optionsType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)!.SetValue(options, value);
        }

        var method = typeof(ParameterResourceBuilderExtensions).GetMethod(
            "WithCustomInputForPolyglot",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, [parameter, options]);
    }

    [Fact]
    public async Task ParameterWithDashInName_CanBeResolvedWithUnderscoreInConfiguration()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        
        // Configuration using underscore instead of dash (as would come from environment variables or command line)
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:storage_account_name"] = "test-storage-account"
        });

        // Act - parameter defined with dash
        appBuilder.AddParameter("storage-account-name");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        // Assert - should resolve to the value set with underscore
        Assert.Equal("test-storage-account", await parameterResource.GetValueAsync(default));
    }

    [Fact]
    public async Task ParameterWithDashInName_PrefersDashConfigurationOverUnderscore()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        
        // Set both versions, dash version should take precedence
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:storage-account-name"] = "dash-value",
            ["Parameters:storage_account_name"] = "underscore-value"
        });

        // Act
        appBuilder.AddParameter("storage-account-name");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        // Assert - should prefer the exact match (with dash)
        Assert.Equal("dash-value", await parameterResource.GetValueAsync(default));
    }

    [Fact]
    public async Task ParameterWithoutDash_DoesNotFallbackToUnderscore()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        
        // Set only underscore version for a parameter without dash
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters:storage_name"] = "underscore-value"
        });

        // Act
        appBuilder.AddParameter("storagename");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        // Assert - should not find the value because names don't match
        await Assert.ThrowsAsync<MissingParameterValueException>(async () =>
        {
            _ = await parameterResource.GetValueAsync(default);
        });
    }

    [Fact]
    public async Task ParameterWithCustomConfigurationKey_UsesFallback()
    {
        // Arrange
        var appBuilder = DistributedApplication.CreateBuilder();
        
        // Set configuration with custom key that has underscore
        appBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CustomSection:my_key"] = "custom-value"
        });

        // Act - use custom configuration key with dash
        appBuilder.AddParameterFromConfiguration("my-param", "CustomSection:my-key");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var parameterResource = Assert.Single(appModel.Resources.OfType<ParameterResource>());

        // Assert - should find the value using the normalized key (dash -> underscore)
        Assert.Equal("custom-value", await parameterResource.GetValueAsync(default));
    }
}
