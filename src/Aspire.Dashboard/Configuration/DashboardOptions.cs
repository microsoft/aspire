// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Hosting;

namespace Aspire.Dashboard.Configuration;

public sealed class DashboardOptions
{
    public string? ApplicationName { get; set; }
    public OtlpOptions Otlp { get; set; } = new();
    public McpOptions Mcp { get; set; } = new();
    public FrontendOptions Frontend { get; set; } = new();
    public ResourceServiceClientOptions ResourceServiceClient { get; set; } = new();
    public TelemetryLimitOptions TelemetryLimits { get; set; } = new();
    public DebugSessionOptions DebugSession { get; set; } = new();
    public UIOptions UI { get; set; } = new();
    public AIOptions AI { get; set; } = new();
}

// Don't set values after validating/parsing options.
public sealed class ResourceServiceClientOptions
{
    private Uri? _parsedUrl;
    private byte[]? _apiKeyBytes;

    public string? Url { get; set; }
    public ResourceClientAuthMode? AuthMode { get; set; }
    public ResourceServiceClientCertificateOptions ClientCertificate { get; set; } = new();
    public string? ApiKey { get; set; }

    public Uri? GetUri() => _parsedUrl;

    internal byte[] GetApiKeyBytes() => _apiKeyBytes ?? throw new InvalidOperationException($"{nameof(ApiKey)} is not available.");

    internal bool TryParseOptions([NotNullWhen(false)] out string? errorMessage)
    {
        if (!string.IsNullOrEmpty(Url))
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out _parsedUrl))
            {
                errorMessage = $"Failed to parse resource service client endpoint URL '{Url}'.";
                return false;
            }
        }

        _apiKeyBytes = ApiKey != null ? Encoding.UTF8.GetBytes(ApiKey) : null;

        errorMessage = null;
        return true;
    }
}

public sealed class ResourceServiceClientCertificateOptions
{
    public DashboardClientCertificateSource? Source { get; set; }
    public string? FilePath { get; set; }
    public string? Password { get; set; }
    public string? Subject { get; set; }
    public string? Store { get; set; }
    public StoreLocation? Location { get; set; }
}

public sealed class AllowedCertificateRule
{
    public string? Thumbprint { get; set; }
}

// Don't set values after validating/parsing options.
public sealed class OtlpOptions
{
    private BindingAddress? _parsedGrpcEndpointAddress;
    private BindingAddress? _parsedHttpEndpointAddress;
    private byte[]? _primaryApiKeyBytes;
    private byte[]? _secondaryApiKeyBytes;

    public string? PrimaryApiKey { get; set; }
    public string? SecondaryApiKey { get; set; }
    public OtlpAuthMode? AuthMode { get; set; }
    public string? GrpcEndpointUrl { get; set; }

    public string? HttpEndpointUrl { get; set; }

    public List<AllowedCertificateRule> AllowedCertificates { get; set; } = new();

    public bool SuppressUnsecuredMessage { get; set; }

    public BindingAddress? GetGrpcEndpointAddress()
    {
        return _parsedGrpcEndpointAddress;
    }

    public BindingAddress? GetHttpEndpointAddress()
    {
        return _parsedHttpEndpointAddress;
    }

    public byte[] GetPrimaryApiKeyBytes()
    {
        Debug.Assert(_primaryApiKeyBytes is not null, "Should have been parsed during validation.");
        return _primaryApiKeyBytes;
    }

    public byte[]? GetSecondaryApiKeyBytes() => _secondaryApiKeyBytes;

    public OtlpCors Cors { get; set; } = new();

    internal bool TryParseOptions([NotNullWhen(false)] out string? errorMessage)
    {
        if (string.IsNullOrEmpty(GrpcEndpointUrl) && string.IsNullOrEmpty(HttpEndpointUrl))
        {
            errorMessage = $"Neither OTLP/gRPC or OTLP/HTTP endpoint URLs are configured. Specify either a {DashboardConfigNames.DashboardOtlpGrpcUrlName.EnvVarName} or {DashboardConfigNames.DashboardOtlpHttpUrlName.EnvVarName} value.";
            return false;
        }

        if (!string.IsNullOrEmpty(GrpcEndpointUrl) && !OptionsHelpers.TryParseBindingAddress(GrpcEndpointUrl, out _parsedGrpcEndpointAddress))
        {
            errorMessage = $"Failed to parse OTLP gRPC endpoint URL '{GrpcEndpointUrl}'.";
            return false;
        }

        if (!string.IsNullOrEmpty(HttpEndpointUrl) && !OptionsHelpers.TryParseBindingAddress(HttpEndpointUrl, out _parsedHttpEndpointAddress))
        {
            errorMessage = $"Failed to parse OTLP HTTP endpoint URL '{HttpEndpointUrl}'.";
            return false;
        }

        if (string.IsNullOrEmpty(HttpEndpointUrl) && !string.IsNullOrEmpty(Cors.AllowedOrigins))
        {
            errorMessage = $"CORS configured without an OTLP HTTP endpoint. Either remove CORS configuration or specify a {DashboardConfigNames.DashboardOtlpHttpUrlName.EnvVarName} value.";
            return false;
        }

        _primaryApiKeyBytes = PrimaryApiKey != null ? Encoding.UTF8.GetBytes(PrimaryApiKey) : null;
        _secondaryApiKeyBytes = SecondaryApiKey != null ? Encoding.UTF8.GetBytes(SecondaryApiKey) : null;

        errorMessage = null;
        return true;
    }
}

public class McpOptions
{
    private BindingAddress? _parsedEndpointAddress;
    private byte[]? _primaryApiKeyBytes;
    private byte[]? _secondaryApiKeyBytes;

    public bool? Disabled { get; set; }
    public McpAuthMode? AuthMode { get; set; }
    public string? PrimaryApiKey { get; set; }
    public string? SecondaryApiKey { get; set; }
    public string? EndpointUrl { get; set; }

    // Public URL could be different from the endpoint URL (e.g., when behind a proxy).
    public string? PublicUrl { get; set; }

    public bool SuppressUnsecuredMessage { get; set; }

    /// <summary>
    /// When true, the dashboard will show instructions for configuring the Aspire CLI MCP server
    /// instead of the dashboard's HTTP-based MCP server.
    /// </summary>
    public bool? UseCliMcp { get; set; }

    public BindingAddress? GetEndpointAddress()
    {
        return _parsedEndpointAddress;
    }

    public byte[] GetPrimaryApiKeyBytes()
    {
        Debug.Assert(_primaryApiKeyBytes is not null, "Should have been parsed during validation.");
        return _primaryApiKeyBytes;
    }

    public byte[]? GetSecondaryApiKeyBytes() => _secondaryApiKeyBytes;

    internal bool TryParseOptions([NotNullWhen(false)] out string? errorMessage)
    {
        if (!string.IsNullOrEmpty(EndpointUrl) && !OptionsHelpers.TryParseBindingAddress(EndpointUrl, out _parsedEndpointAddress))
        {
            errorMessage = $"Failed to parse MCP endpoint URL '{EndpointUrl}'.";
            return false;
        }

        _primaryApiKeyBytes = PrimaryApiKey != null ? Encoding.UTF8.GetBytes(PrimaryApiKey) : null;
        _secondaryApiKeyBytes = SecondaryApiKey != null ? Encoding.UTF8.GetBytes(SecondaryApiKey) : null;

        errorMessage = null;
        return true;
    }
}

public sealed class OtlpCors
{
    public string? AllowedOrigins { get; set; }
    public string? AllowedHeaders { get; set; }

    [MemberNotNullWhen(true, nameof(AllowedOrigins))]
    public bool IsCorsEnabled => !string.IsNullOrEmpty(AllowedOrigins);
}

// Don't set values after validating/parsing options.
public sealed class FrontendOptions
{
    private List<BindingAddress>? _parsedEndpointAddresses;
    private byte[]? _browserTokenBytes;

    public string? EndpointUrls { get; set; }
    public FrontendAuthMode? AuthMode { get; set; }
    public string? BrowserToken { get; set; }

    // Public URL could be different from the endpoint URL (e.g., when behind a proxy).
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Gets and sets an optional limit on the number of console log messages to be retained in the viewer.
    /// </summary>
    /// <remarks>
    /// The viewer will retain at most this number of log messages. When the limit is reached, the oldest messages will be removed.
    /// Defaults to 10,000, which matches the default used in the app host's circular buffer, on the publish side.
    /// </remarks>
    public int MaxConsoleLogCount { get; set; } = 10_000;

    public OpenIdConnectOptions OpenIdConnect { get; set; } = new();

    public byte[]? GetBrowserTokenBytes() => _browserTokenBytes;

    public IReadOnlyList<BindingAddress> GetEndpointAddresses()
    {
        Debug.Assert(_parsedEndpointAddresses is not null, "Should have been parsed during validation.");
        return _parsedEndpointAddresses;
    }

    internal bool TryParseOptions([NotNullWhen(false)] out string? errorMessage)
    {
        if (string.IsNullOrEmpty(EndpointUrls))
        {
            errorMessage = $"One or more frontend endpoint URLs are not configured. Specify an {DashboardConfigNames.DashboardFrontendUrlName.ConfigKey} value.";
            return false;
        }
        else
        {
            var parts = EndpointUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var addresses = new List<BindingAddress>(parts.Length);
            foreach (var part in parts)
            {
                if (OptionsHelpers.TryParseBindingAddress(part, out var bindingAddress))
                {
                    addresses.Add(bindingAddress);
                }
                else
                {
                    errorMessage = $"Failed to parse frontend endpoint URLs '{EndpointUrls}'.";
                    return false;
                }
            }
            _parsedEndpointAddresses = addresses;
        }

        _browserTokenBytes = BrowserToken != null ? Encoding.UTF8.GetBytes(BrowserToken) : null;

        errorMessage = null;
        return true;
    }
}

public static class OptionsHelpers
{
    public static bool TryParseBindingAddress(string address, [NotNullWhen(true)] out BindingAddress? bindingAddress)
    {
        try
        {
            bindingAddress = BindingAddress.Parse(address);
            return true;
        }
        catch
        {
            bindingAddress = null;
            return false;
        }
    }
}

public sealed class TelemetryLimitOptions
{
    public int MaxLogCount { get; set; } = 10_000;
    public int MaxTraceCount { get; set; } = 10_000;
    public int MaxMetricsCount { get; set; } = 50_000; // Allows for 1 metric point per second for over 12 hours.
    public int MaxAttributeCount { get; set; } = 128;
    public int MaxAttributeLength { get; set; } = int.MaxValue;
    public int MaxSpanEventCount { get; set; } = int.MaxValue;
}

public sealed class UIOptions
{
    public bool? DisableResourceGraph { get; set; }
}

// Don't set values after validating/parsing options.
public sealed class OpenIdConnectOptions
{
    private string[]? _nameClaimTypes;
    private string[]? _usernameClaimTypes;

    public string NameClaimType { get; set; } = "name";
    public string UsernameClaimType { get; set; } = "preferred_username";

    /// <summary>
    /// Gets the optional name of a claim that users authenticated via OpenID Connect are required to have.
    /// If specified, users without this claim will be rejected. If <see cref="RequiredClaimValue"/>
    /// is also specified, then the value of this claim must also match <see cref="RequiredClaimValue"/>.
    /// </summary>
    public string RequiredClaimType { get; set; } = "";

    /// <summary>
    /// Gets the optional value of the <see cref="RequiredClaimType"/> claim for users authenticated via
    /// OpenID Connect. If specified, users not having this value for the corresponding claim type are
    /// rejected.
    /// </summary>
    public string RequiredClaimValue { get; set; } = "";

    /// <summary>
    /// Gets or sets the optional value to configure the ClaimActions of <see cref="Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions"/>
    /// </summary>
    public List<ClaimAction> ClaimActions { get; set; } = new();

    public string[] GetNameClaimTypes()
    {
        Debug.Assert(_nameClaimTypes is not null, "Should have been parsed during validation.");
        return _nameClaimTypes;
    }

    public string[] GetUsernameClaimTypes()
    {
        Debug.Assert(_usernameClaimTypes is not null, "Should have been parsed during validation.");
        return _usernameClaimTypes;
    }

    internal bool TryParseOptions([NotNullWhen(false)] out IEnumerable<string>? errorMessages)
    {
        List<string>? messages = null;

        if (string.IsNullOrWhiteSpace(NameClaimType))
        {
            messages ??= [];
            messages.Add("OpenID Connect claim type for name not configured. Specify a Dashboard:Frontend:OpenIdConnect:NameClaimType value.");
        }
        else
        {
            _nameClaimTypes = NameClaimType.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (string.IsNullOrWhiteSpace(UsernameClaimType))
        {
            messages ??= [];
            messages.Add("OpenID Connect claim type for username not configured. Specify a Dashboard:Frontend:OpenIdConnect:UsernameClaimType value.");
        }
        else
        {
            _usernameClaimTypes = UsernameClaimType.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        errorMessages = messages;

        return messages is null;
    }
}

public sealed class ClaimAction
{
    public required string ClaimType { get; set; }
    public required string JsonKey { get; set; }
    public string? SubKey { get; set; }
    public bool? IsUnique { get; set; }
    public string? ValueType { get; set; }
}

public sealed class AIOptions
{
    public bool? Disabled { get; set; }
}

public sealed class DebugSessionOptions
{
    private X509Certificate2? _serverCertificate;

    public int? Port { get; set; }
    public string? Token { get; set; }
    public string? ServerCertificate { get; set; }
    public bool? TelemetryOptOut { get; set; }

    public X509Certificate2? GetServerCertificate() => _serverCertificate;

    internal bool TryParseOptions([NotNullWhen(false)] out string? errorMessage)
    {
        if (!string.IsNullOrEmpty(ServerCertificate))
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(ServerCertificate);
            }
            catch (Exception ex)
            {
                errorMessage = $"Error converting server certificate payload from base64 to bytes: {ex.Message}";
                return false;
            }

            try
            {
                _serverCertificate = new X509Certificate2(data);
            }
            catch (Exception ex)
            {
                errorMessage = $"Error reading server certificate as X509Certificate2: {ex.Message}";
                return false;
            }
        }

        errorMessage = null;
        return true;
    }
}
