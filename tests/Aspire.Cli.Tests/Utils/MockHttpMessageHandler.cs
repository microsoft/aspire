// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

/// <summary>
/// Mock HTTP message handler for testing HTTP client interactions.
/// Supports returning fixed responses, dynamic responses via factory, throwing exceptions, and request validation.
/// </summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responseFactory;
    private readonly HttpResponseMessage? _response;
    private readonly Exception? _exception;
    private readonly Action<HttpRequestMessage>? _requestValidator;

    /// <summary>
    /// Gets a value indicating whether the request validator was invoked.
    /// </summary>
    public bool RequestValidated { get; private set; }

    /// <summary>
    /// Creates a handler that returns the specified response.
    /// </summary>
    /// <param name="response">The HTTP response to return.</param>
    /// <param name="requestValidator">Optional action to validate the request.</param>
    public MockHttpMessageHandler(HttpResponseMessage response, Action<HttpRequestMessage>? requestValidator = null)
    {
        _response = response;
        _requestValidator = requestValidator;
    }

    /// <summary>
    /// Creates a handler that generates responses dynamically using the provided factory.
    /// </summary>
    /// <param name="responseFactory">A function that creates responses based on the request.</param>
    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    /// <summary>
    /// Creates a handler that throws the specified exception.
    /// </summary>
    /// <param name="exception">The exception to throw.</param>
    public MockHttpMessageHandler(Exception exception)
    {
        _exception = exception;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_exception is not null)
        {
            throw _exception;
        }

        if (_requestValidator is not null)
        {
            _requestValidator(request);
            RequestValidated = true;
        }

        if (_responseFactory is not null)
        {
            return Task.FromResult(_responseFactory(request));
        }

        return Task.FromResult(_response!);
    }
}
