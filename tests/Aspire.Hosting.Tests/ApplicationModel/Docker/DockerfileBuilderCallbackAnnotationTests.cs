// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

using Aspire.Hosting.ApplicationModel.Docker;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class DockerfileBuilderCallbackAnnotationTests
{
    [Fact]
    public void DockerfileBuilderCallbackAnnotation_Constructor_CreatesAnnotation()
    {
        // Arrange
        Func<DockerfileBuilderCallbackContext, Task> callback = context => Task.CompletedTask;

        // Act
        var annotation = new DockerfileBuilderCallbackAnnotation(callback);

        // Assert
        Assert.NotNull(annotation.Callbacks);
        Assert.Single(annotation.Callbacks);
    }

    [Fact]
    public void DockerfileBuilderCallbackAnnotation_DefaultConstructor_CreatesEmptyAnnotation()
    {
        // Arrange & Act
        var annotation = new DockerfileBuilderCallbackAnnotation();

        // Assert
        Assert.NotNull(annotation.Callbacks);
        Assert.Empty(annotation.Callbacks);
    }

    [Fact]
    public async Task DockerfileBuilderCallbackAnnotation_CallbackCanBeInvoked()
    {
        // Arrange
        var callbackInvoked = false;
        DockerfileBuilderCallbackContext? capturedContext = null;

        Func<DockerfileBuilderCallbackContext, Task> callback = context =>
        {
            callbackInvoked = true;
            capturedContext = context;
            return Task.CompletedTask;
        };

        var annotation = new DockerfileBuilderCallbackAnnotation(callback);
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(new ContainerResource("test"), builder, services, CancellationToken.None);

        // Act
        await annotation.Callbacks[0](context);

        // Assert
        Assert.True(callbackInvoked);
        Assert.NotNull(capturedContext);
        Assert.Same(context, capturedContext);
    }

    [Fact]
    public async Task DockerfileBuilderCallbackAnnotation_CallbackWithAsyncOperation()
    {
        // Arrange
        var delay = TimeSpan.FromMilliseconds(10);
        var callbackCompleted = false;

        Func<DockerfileBuilderCallbackContext, Task> callback = async context =>
        {
            await Task.Delay(delay);
            callbackCompleted = true;
        };

        var annotation = new DockerfileBuilderCallbackAnnotation(callback);
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(new ContainerResource("test"), builder, services, CancellationToken.None);

        // Act
        await annotation.Callbacks[0](context);

        // Assert
        Assert.True(callbackCompleted);
    }

    [Fact]
    public async Task DockerfileBuilderCallbackAnnotation_CallbackCanModifyDockerfileBuilder()
    {
        // Arrange
        var builderModified = false;

        Func<DockerfileBuilderCallbackContext, Task> callback = context =>
        {
            context.Builder.From("alpine:latest")
                .WorkDir("/workspace")
                .Run("apk add curl");
            builderModified = true;
            return Task.CompletedTask;
        };

        var annotation = new DockerfileBuilderCallbackAnnotation(callback);
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(new ContainerResource("test"), builder, services, CancellationToken.None);

        // Act
        await annotation.Callbacks[0](context);

        // Assert
        Assert.True(builderModified);
        Assert.Single(context.Builder.Stages);
        Assert.Equal(3, context.Builder.Stages[0].Statements.Count); // FROM + WORKDIR + RUN
    }

    [Fact]
    public async Task DockerfileBuilderCallbackAnnotation_CallbackCanAccessServices()
    {
        // Arrange
        var serviceAccessed = false;
        var testService = "test-service-value";

        Func<DockerfileBuilderCallbackContext, Task> callback = context =>
        {
            var retrievedService = context.Services.GetService<string>();
            serviceAccessed = retrievedService == testService;
            return Task.CompletedTask;
        };

        var annotation = new DockerfileBuilderCallbackAnnotation(callback);
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection()
            .AddSingleton(testService)
            .BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(new ContainerResource("test"), builder, services, CancellationToken.None);

        // Act
        await annotation.Callbacks[0](context);

        // Assert
        Assert.True(serviceAccessed);
    }

    [Fact]
    public void DockerfileBuilderCallbackAnnotation_AddCallback_AddsCallback()
    {
        // Arrange
        var annotation = new DockerfileBuilderCallbackAnnotation();
        Func<DockerfileBuilderCallbackContext, Task> callback1 = context => Task.CompletedTask;
        Func<DockerfileBuilderCallbackContext, Task> callback2 = context => Task.CompletedTask;

        // Act
        annotation.AddCallback(callback1);
        annotation.AddCallback(callback2);

        // Assert
        Assert.Equal(2, annotation.Callbacks.Count);
    }

    [Fact]
    public async Task DockerfileBuilderCallbackAnnotation_MultipleCallbacks_AllInvoked()
    {
        // Arrange
        var callback1Invoked = false;
        var callback2Invoked = false;

        var annotation = new DockerfileBuilderCallbackAnnotation();
        annotation.AddCallback(context =>
        {
            callback1Invoked = true;
            return Task.CompletedTask;
        });
        annotation.AddCallback(context =>
        {
            callback2Invoked = true;
            return Task.CompletedTask;
        });

        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(new ContainerResource("test"), builder, services, CancellationToken.None);

        // Act
        foreach (var callback in annotation.Callbacks)
        {
            await callback(context);
        }

        // Assert
        Assert.True(callback1Invoked);
        Assert.True(callback2Invoked);
    }

    [Fact]
    public async Task DockerfileBuilderCallbackAnnotation_MultipleCallbacks_BuildInSequence()
    {
        // Arrange
        var annotation = new DockerfileBuilderCallbackAnnotation();
        
        annotation.AddCallback(context =>
        {
            context.Builder.From("alpine:latest")
                .WorkDir("/app");
            return Task.CompletedTask;
        });
        
        annotation.AddCallback(context =>
        {
            context.Builder.Stages[0].Run("apk add curl")
                .Copy(".", ".");
            return Task.CompletedTask;
        });

        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(new ContainerResource("test"), builder, services, CancellationToken.None);

        // Act
        foreach (var callback in annotation.Callbacks)
        {
            await callback(context);
        }

        // Assert
        Assert.Single(context.Builder.Stages);
        Assert.Equal(4, context.Builder.Stages[0].Statements.Count); // FROM + WORKDIR + RUN + COPY
    }
}