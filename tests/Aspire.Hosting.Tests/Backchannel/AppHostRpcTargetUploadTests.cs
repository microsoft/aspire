// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Backchannel;

[Trait("Partition", "4")]
public class AppHostRpcTargetUploadTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task UploadFileAsync_ValidRequest_StoresAndCompletesUpload()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        await using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();
        var fileUploadStore = app.Services.GetRequiredService<IInteractionFileUploadStore>();
        const int interactionId = 1;
        const string inputName = "File";
        fileUploadStore.StartInteraction(interactionId);
        var data = Encoding.UTF8.GetBytes("uploaded content");

        var response = await target.UploadFileAsync(new UploadFileRequest
        {
            Data = data,
            FileName = "test.txt",
            InteractionId = interactionId,
            InputName = inputName
        });

        var filePath = Assert.IsType<string>(fileUploadStore.GetFilePath(response.FileId, interactionId, inputName));
        Assert.Equal(data, await File.ReadAllBytesAsync(filePath));

        fileUploadStore.CancelInteraction(interactionId);

        Assert.Null(fileUploadStore.GetFilePath(response.FileId, interactionId, inputName));
        Assert.False(File.Exists(filePath));
    }

    [Theory]
    [InlineData(0, "File", "An interaction ID is required when uploading a file.")]
    [InlineData(1, "", "An input name is required when uploading a file.")]
    public async Task UploadFileAsync_InvalidOwnership_Throws(int interactionId, string inputName, string expectedMessage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        await using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => target.UploadFileAsync(new UploadFileRequest
        {
            Data = [],
            FileName = "test.txt",
            InteractionId = interactionId,
            InputName = inputName
        }));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task UploadFileAsync_ExceedsConfiguredLimit_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration[KnownConfigNames.MaxFileUploadSize] = "1";
        await using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => target.UploadFileAsync(new UploadFileRequest
        {
            Data = [1, 2],
            FileName = "large.bin",
            InteractionId = 1,
            InputName = "File"
        }));

        Assert.Contains("exceeds the maximum upload size of 1 bytes", exception.Message);
    }

    [Fact]
    public async Task UploadFileAsync_WriteCanceled_RemovesEntry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        await using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();
        var fileUploadStore = app.Services.GetRequiredService<IInteractionFileUploadStore>();
        const int interactionId = 1;
        const string inputName = "File";
        fileUploadStore.StartInteraction(interactionId);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target.UploadFileAsync(new UploadFileRequest
        {
            Data = [1],
            FileName = "test.txt",
            InteractionId = interactionId,
            InputName = inputName
        }, cancellationTokenSource.Token));

        fileUploadStore.CompleteInteraction(interactionId);
        fileUploadStore.StartInteraction(interactionId);
        var (_, replacementPath) = fileUploadStore.CreateEntry("replacement.txt", interactionId, inputName);

        Assert.True(File.Exists(replacementPath));
    }
}
