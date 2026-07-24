// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Aspire.DashboardService.Proto.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;

namespace Aspire.Dashboard.Backend;

internal interface IDashboardResourceServiceConnection
{
    bool IsConfigured { get; }
    string UnavailableMessage { get; }

    IAsyncEnumerable<WatchResourcesUpdate> WatchResourcesAsync(
        bool isReconnect,
        CancellationToken cancellationToken);

    ValueTask<ResourceCommandResponse> ExecuteResourceCommandAsync(
        ResourceCommandRequest request,
        CancellationToken cancellationToken);

    Task RunInteractionSessionAsync(
        ChannelReader<DashboardPendingInteractionResponse> responses,
        Func<WatchInteractionsResponseUpdate, ValueTask> onUpdate,
        CancellationToken cancellationToken);
}

internal sealed class DashboardResourceServiceConnection : IDashboardResourceServiceConnection, IDisposable
{
    private const string ResourceServiceEndpointKey = "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL";
    private const string LegacyResourceServiceEndpointKey = "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL";
    private const string ResourceServiceAuthModeKey = "Dashboard:ResourceServiceClient:AuthMode";
    private const string ResourceServiceApiKeyKey = "Dashboard:ResourceServiceClient:ApiKey";
    private const string ResourceServiceCertificateSourceKey = "Dashboard:ResourceServiceClient:ClientCertificate:Source";
    private const string ResourceServiceCertificateFilePathKey = "Dashboard:ResourceServiceClient:ClientCertificate:FilePath";
    private const string ResourceServiceCertificatePasswordKey = "Dashboard:ResourceServiceClient:ClientCertificate:Password";
    private const string ResourceServiceCertificateSubjectKey = "Dashboard:ResourceServiceClient:ClientCertificate:Subject";
    private const string ResourceServiceCertificateStoreKey = "Dashboard:ResourceServiceClient:ClientCertificate:Store";
    private const string ResourceServiceCertificateLocationKey = "Dashboard:ResourceServiceClient:ClientCertificate:Location";
    private const string ApiKeyHeaderName = "x-resource-service-api-key";

    private readonly SocketsHttpHandler? _handler;
    private readonly GrpcChannel? _channel;
    private readonly Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient? _client;
    private readonly Metadata _headers = [];

    public DashboardResourceServiceConnection(
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        var endpoint = configuration[ResourceServiceEndpointKey] ?? configuration[LegacyResourceServiceEndpointKey];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var resourceServiceUri))
        {
            UnavailableMessage = $"Configure {ResourceServiceEndpointKey} with the AppHost resource-service endpoint.";
            return;
        }

        _handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
        };

        ConfigureAuthentication(configuration, _handler, _headers);

        // Apply the same transient-unavailable retry policy as the legacy dashboard client.
        // All resource, command, and interaction calls share this one channel so an
        // interaction-producing command cannot split away from its response session.
        var methodConfig = new MethodConfig
        {
            Names = { MethodName.Default },
            RetryPolicy = new RetryPolicy
            {
                MaxAttempts = 5,
                InitialBackoff = TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromSeconds(5),
                BackoffMultiplier = 1.5,
                RetryableStatusCodes = { StatusCode.Unavailable }
            }
        };
        _channel = GrpcChannel.ForAddress(resourceServiceUri, new GrpcChannelOptions
        {
            HttpHandler = _handler,
            ServiceConfig = new() { MethodConfigs = { methodConfig } },
            LoggerFactory = loggerFactory,
            ThrowOperationCanceledOnCancellation = true,
            MaxReceiveMessageSize = 16 * 1024 * 1024
        });
        _client = new Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient(_channel);
        UnavailableMessage = "The AppHost resource service is unavailable.";
        IsConfigured = true;
    }

    public bool IsConfigured { get; }

    public string UnavailableMessage { get; }

    public async IAsyncEnumerable<WatchResourcesUpdate> WatchResourcesAsync(
        bool isReconnect,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = GetClient();
        using var call = client.WatchResources(
            new WatchResourcesRequest { IsReconnect = isReconnect },
            _headers,
            cancellationToken: cancellationToken);
        await foreach (var update in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public async ValueTask<ResourceCommandResponse> ExecuteResourceCommandAsync(
        ResourceCommandRequest request,
        CancellationToken cancellationToken)
    {
        return await GetClient().ExecuteResourceCommandAsync(
            request,
            _headers,
            cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
    }

    public async Task RunInteractionSessionAsync(
        ChannelReader<DashboardPendingInteractionResponse> responses,
        Func<WatchInteractionsResponseUpdate, ValueTask> onUpdate,
        CancellationToken cancellationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var call = GetClient().WatchInteractions(
            _headers,
            cancellationToken: sessionCancellation.Token);

        var writeTask = WriteResponsesAsync(call.RequestStream, responses, sessionCancellation, cancellationToken);
        Exception? sessionException = null;
        try
        {
            await foreach (var update in call.ResponseStream.ReadAllAsync(sessionCancellation.Token).ConfigureAwait(false))
            {
                await onUpdate(update).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            sessionException = ex;
        }
        finally
        {
            await sessionCancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await writeTask.ConfigureAwait(false);
        }
        catch (Exception) when (sessionException is not null)
        {
            // The response-stream failure is the root cause of the writer cancellation.
        }
        catch (Exception ex)
        {
            sessionException = ex;
        }

        if (sessionException is not null)
        {
            ExceptionDispatchInfo.Capture(sessionException).Throw();
        }
    }

    private static async Task WriteResponsesAsync(
        IClientStreamWriter<WatchInteractionsRequestUpdate> requestStream,
        ChannelReader<DashboardPendingInteractionResponse> responses,
        CancellationTokenSource sessionCancellation,
        CancellationToken stoppingToken)
    {
        DashboardPendingInteractionResponse? pending = null;
        try
        {
            await foreach (var response in responses.ReadAllAsync(sessionCancellation.Token).ConfigureAwait(false))
            {
                pending = response;
                await requestStream.WriteAsync(response.Request, sessionCancellation.Token).ConfigureAwait(false);
                response.MarkDelivered();
                pending = null;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested && pending is null)
        {
        }
        catch (Exception ex)
        {
            pending?.MarkFailed(ex);
            await sessionCancellation.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Aspire.DashboardService.Proto.V1.DashboardService.DashboardServiceClient GetClient()
    {
        return _client ?? throw new DashboardResourceServiceUnavailableException(UnavailableMessage);
    }

    private static void ConfigureAuthentication(
        IConfiguration configuration,
        SocketsHttpHandler handler,
        Metadata headers)
    {
        var authMode = configuration[ResourceServiceAuthModeKey];
        if (string.Equals(authMode, "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            if (configuration[ResourceServiceApiKeyKey] is not { Length: > 0 } apiKey)
            {
                throw new InvalidOperationException(
                    $"{ResourceServiceAuthModeKey} is \"ApiKey\", but no {ResourceServiceApiKeyKey} is configured.");
            }

            headers.Add(ApiKeyHeaderName, apiKey);
            return;
        }

        if (!string.Equals(authMode, "Certificate", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var certificates = configuration[ResourceServiceCertificateSourceKey] switch
        {
            string source when source.Equals("File", StringComparison.OrdinalIgnoreCase) =>
                LoadFileCertificate(configuration),
            string source when source.Equals("KeyStore", StringComparison.OrdinalIgnoreCase) =>
                LoadKeyStoreCertificate(configuration),
            _ => throw new InvalidOperationException(
                $"{ResourceServiceAuthModeKey} is \"Certificate\", but no valid {ResourceServiceCertificateSourceKey} is configured.")
        };

        handler.SslOptions = new SslClientAuthenticationOptions
        {
            ClientCertificates = certificates
        };
    }

    private static X509CertificateCollection LoadFileCertificate(IConfiguration configuration)
    {
        var filePath = configuration[ResourceServiceCertificateFilePathKey];
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException(
                $"{ResourceServiceCertificateSourceKey} is \"File\", but no {ResourceServiceCertificateFilePathKey} is configured.");
        }

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            filePath,
            configuration[ResourceServiceCertificatePasswordKey]);
        return [certificate];
    }

    private static X509CertificateCollection LoadKeyStoreCertificate(IConfiguration configuration)
    {
        var subject = configuration[ResourceServiceCertificateSubjectKey];
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException(
                $"{ResourceServiceCertificateSourceKey} is \"KeyStore\", but no {ResourceServiceCertificateSubjectKey} is configured.");
        }

        var storeName = configuration[ResourceServiceCertificateStoreKey] ?? "My";
        var locationText = configuration[ResourceServiceCertificateLocationKey];
        var location = Enum.TryParse<StoreLocation>(locationText, ignoreCase: true, out var parsedLocation)
            ? parsedLocation
            : StoreLocation.CurrentUser;

        using var store = new X509Store(storeName, location);
        store.Open(OpenFlags.ReadOnly);
        var certificates = store.Certificates.Find(
            X509FindType.FindBySubjectName,
            subject,
            validOnly: true);
        if (certificates.Count is 0)
        {
            throw new InvalidOperationException(
                $"Unable to load client certificate with subject \"{subject}\" from key store.");
        }

        return certificates;
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _handler?.Dispose();
    }
}
