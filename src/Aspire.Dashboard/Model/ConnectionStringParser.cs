// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Provides utilities for parsing connection strings to extract host and port information.
/// Supports various connection string formats including URIs, key-value pairs, and delimited lists.
/// </summary>
internal static partial class ConnectionStringParser
{
    private static readonly Dictionary<string, int> s_schemeDefaultPorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["http"] = 80,
        ["https"] = 443,
        ["ftp"] = 21,
        ["ftps"] = 990,
        ["ssh"] = 22,
        ["telnet"] = 23,
        ["smtp"] = 25,
        ["dns"] = 53,
        ["dhcp"] = 67,
        ["tftp"] = 69,
        ["pop3"] = 110,
        ["ntp"] = 123,
        ["imap"] = 143,
        ["snmp"] = 161,
        ["ldap"] = 389,
        ["smtps"] = 465,
        ["ldaps"] = 636,
        ["imaps"] = 993,
        ["pop3s"] = 995,
        ["mssql"] = 1433,
        ["mysql"] = 3306,
        ["postgresql"] = 5432,
        ["postgres"] = 5432,
        ["redis"] = 6379,
        ["mongodb"] = 27017,
        ["amqp"] = 5672,
        ["amqps"] = 5671,
        ["kafka"] = 9092
    };

    private static readonly string[] s_hostAliases = ["host", "server", "data source", "addr", "address", "endpoint", "contact points"];

    private static readonly string[] s_knownProtocols = ["tcp", "udp", "ssl", "tls", "http", "https", "ftp", "ssh"];

    /// <summary>
    /// Matches host:port or host,port patterns with optional IPv6 bracket notation.
    /// Examples: "localhost:5432", "127.0.0.1,1433", "[::1]:6379"
    /// </summary>
    [GeneratedRegex(@"(\[[^\]]+\]|[^,:;\s]+)[:,](\d{1,5})", RegexOptions.Compiled)]
    private static partial Regex HostPortRegex();

    /// <summary>
    /// Matches JDBC URLs to extract host and optional port.
    /// Examples: "jdbc:postgresql://localhost:5432/db", "jdbc:mysql://server/database"
    /// </summary>
    [GeneratedRegex(@"^jdbc:[^:]+://([^:/\s]+)(?::(\d+))?(?:/.*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex JdbcUrlRegex();

    /// <summary>
    /// Attempts to extract a host and optional port from an arbitrary connection string.
    /// Returns <c>true</c> if a host could be identified; otherwise <c>false</c>.
    /// 
    /// Supports the following connection string formats:
    /// - URIs: "postgres://user:pass@host:5432/db", "redis://host:6379"
    /// - Key-value pairs: "Host=localhost;Port=5432", "Server=tcp:host,1433"
    /// - Delimited lists: "broker1:9092,broker2:9092" (returns first broker)
    /// - Single hostnames: "localhost", "api.example.com"
    /// </summary>
    /// <param name="connectionString">The connection string to parse.</param>
    /// <param name="host">When this method returns <c>true</c>, contains the host part with surrounding brackets removed; otherwise, an empty string.</param>
    /// <param name="port">When this method returns <c>true</c>, contains the explicit port, scheme-derived default, or <c>null</c> when unavailable; otherwise, <c>null</c>.</param>
    /// <returns><c>true</c> if a host was found; otherwise, <c>false</c>.</returns>
    public static bool TryDetectHostAndPort(
        string connectionString,
        [NotNullWhen(true)] out string? host,
        out int? port)
    {
        host = null;
        port = null;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        // Strategy 1: Parse as URI (including JDBC URLs)
        // Examples: "postgres://host:5432/db", "jdbc:mysql://host/db"
        if (TryParseAsUri(connectionString, out host, out port))
        {
            return true;
        }

        // Strategy 2: Parse as key-value pairs
        // Examples: "Host=localhost;Port=5432", "Server=tcp:host,1433"
        if (TryParseAsKeyValuePairs(connectionString, out host, out port))
        {
            return true;
        }

        // Strategy 3: Use regex heuristic for host:port patterns
        // Examples: "localhost:5432", "127.0.0.1,1433", "[::1]:6379"
        if (TryParseWithRegexHeuristic(connectionString, out host, out port))
        {
            return true;
        }

        // Strategy 4: Treat as single hostname (conservative approach)
        // Examples: "localhost", "api.example.com" (but not file paths)
        if (TryParseAsSingleHost(connectionString, out host, out port))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse the connection string as a URI (including JDBC URLs).
    /// </summary>
    /// <param name="connectionString">The string to parse as a URI. Examples: "postgres://host:5432/db", "jdbc:mysql://host/db"</param>
    /// <param name="host">The extracted host name, or null if parsing failed.</param>
    /// <param name="port">The extracted port number, or null if no port was found.</param>
    /// <returns>True if a host was successfully extracted; otherwise false.</returns>
    private static bool TryParseAsUri(string connectionString, [NotNullWhen(true)] out string? host, out int? port)
    {
        host = null;
        port = null;

        // Handle JDBC URLs specially since they're not recognized by Uri.TryCreate
        // Example: "jdbc:postgresql://localhost:5432/database"
        if (connectionString.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseJdbcUrl(connectionString, out host, out port);
        }

        // Standard URI parsing for protocols like postgres://, redis://, etc.
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            host = TrimBrackets(uri.Host);
            port = uri.Port != -1 ? uri.Port : DefaultPortFromScheme(uri.Scheme);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse key-value pair connection strings.
    /// </summary>
    /// <param name="connectionString">The connection string with key-value pairs. Examples: "Host=localhost;Port=5432", "Server=tcp:host,1433"</param>
    /// <param name="host">The extracted host name, or null if parsing failed.</param>
    /// <param name="port">The extracted port number, or null if no port was found.</param>
    /// <returns>True if a host was successfully extracted; otherwise false.</returns>
    private static bool TryParseAsKeyValuePairs(string connectionString, [NotNullWhen(true)] out string? host, out int? port)
    {
        host = null;
        port = null;

        var keyValuePairs = SplitIntoDictionary(connectionString);
        
        foreach (var hostAlias in s_hostAliases)
        {
            if (keyValuePairs.TryGetValue(hostAlias, out var token))
            {
                // First, check if the token is a complete URL
                // Example: "Endpoint=https://storage.azure.com"
                if (TryParseAsUri(token, out var tokenHost, out var tokenPort))
                {
                    host = tokenHost;
                    port = tokenPort;
                    return true;
                }
                
                // Handle special case of multiple contact points (should return false to be conservative)
                // Example: "contact points=node1,node2,node3" should not be parsed
                if (hostAlias.Equals("contact points", StringComparison.OrdinalIgnoreCase) && 
                    token.Contains(',') && token.Split(',').Length > 1)
                {
                    return false;
                }
                
                // Remove protocol prefixes like "tcp:", "udp:", etc.
                // Example: "Server=tcp:localhost,1433" becomes "localhost,1433"
                token = RemoveProtocolPrefix(token);
                
                if (token.Contains(',') || token.Contains(':'))
                {
                    // Handle host:port or host,port patterns
                    // Examples: "localhost:5432", "127.0.0.1,1433", "[::1]:6379"
                    if (TryParseHostPortToken(token, keyValuePairs, out host, out port))
                    {
                        return true;
                    }
                }
                else if (!string.IsNullOrEmpty(token))
                {
                    // Single hostname without port
                    // Example: "Host=localhost"
                    host = TrimBrackets(token);
                    port = PortFromKV(keyValuePairs);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Uses regex heuristics to find host:port patterns in the connection string.
    /// </summary>
    /// <param name="connectionString">The connection string to search. Examples: "broker1:9092,broker2:9092", "localhost:5432"</param>
    /// <param name="host">The extracted host name, or null if parsing failed.</param>
    /// <param name="port">The extracted port number, or null if no port was found.</param>
    /// <returns>True if a host:port pattern was found; otherwise false.</returns>
    private static bool TryParseWithRegexHeuristic(string connectionString, [NotNullWhen(true)] out string? host, out int? port)
    {
        host = null;
        port = null;

        var match = HostPortRegex().Match(connectionString);
        if (match.Success)
        {
            var hostPart = match.Groups[1].Value;
            var portPart = match.Groups[2].Value;
            if (!string.IsNullOrEmpty(hostPart))
            {
                host = TrimBrackets(hostPart);
                port = ParseIntSafe(portPart);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to treat the entire connection string as a single hostname (conservative approach).
    /// </summary>
    /// <param name="connectionString">The string to evaluate as a hostname. Examples: "localhost", "api.example.com"</param>
    /// <param name="host">The hostname if it looks valid, or null if it appears to be a file path or other non-hostname.</param>
    /// <param name="port">Always null for single hostname parsing.</param>
    /// <returns>True if the string looks like a valid hostname; otherwise false.</returns>
    private static bool TryParseAsSingleHost(string connectionString, [NotNullWhen(true)] out string? host, out int? port)
    {
        host = null;
        port = null;

        if (LooksLikeHost(connectionString))
        {
            host = TrimBrackets(connectionString);
            port = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a host:port or host,port token, with special handling for IPv6 addresses.
    /// </summary>
    /// <param name="token">The token to parse. Examples: "localhost:5432", "[::1]:6379", "host,1433"</param>
    /// <param name="keyValuePairs">Additional key-value pairs that might contain a separate port value.</param>
    /// <param name="host">The extracted host name, or null if parsing failed.</param>
    /// <param name="port">The extracted port number, or null if no port was found.</param>
    /// <returns>True if parsing succeeded; otherwise false.</returns>
    private static bool TryParseHostPortToken(string token, Dictionary<string, string> keyValuePairs, [NotNullWhen(true)] out string? host, out int? port)
    {
        host = null;
        port = null;

        // Special handling for IPv6 addresses in brackets
        // Example: "[::1]:6379" or "[::1],6379"
        if (token.StartsWith('[') && token.Contains(']'))
        {
            var bracketEnd = token.IndexOf(']');
            if (bracketEnd > 0)
            {
                host = TrimBrackets(token[..(bracketEnd + 1)]);
                // Look for port after the bracket (could be colon or comma separated)
                var afterBracket = token[(bracketEnd + 1)..];
                if ((afterBracket.StartsWith(':') || afterBracket.StartsWith(',')) && afterBracket.Length > 1)
                {
                    port = ParseIntSafe(afterBracket[1..]) ?? PortFromKV(keyValuePairs);
                }
                else
                {
                    port = PortFromKV(keyValuePairs);
                }
                return true;
            }
        }

        // Regular host:port or host,port parsing
        var (hostPart, portPart) = SplitOnLast(token);
        if (!string.IsNullOrEmpty(hostPart))
        {
            host = TrimBrackets(hostPart);
            port = ParseIntSafe(portPart) ?? PortFromKV(keyValuePairs);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses JDBC URLs which have the format: jdbc:subprotocol://host:port/database
    /// </summary>
    /// <param name="jdbcUrl">The JDBC URL to parse. Examples: "jdbc:postgresql://localhost:5432/db", "jdbc:mysql://server/database"</param>
    /// <param name="host">The extracted host name, or null if parsing failed.</param>
    /// <param name="port">The extracted port number, or null if no port was specified.</param>
    /// <returns>True if the JDBC URL was successfully parsed; otherwise false.</returns>
    private static bool TryParseJdbcUrl(string jdbcUrl, [NotNullWhen(true)] out string? host, out int? port)
    {
        host = null;
        port = null;

        var match = JdbcUrlRegex().Match(jdbcUrl);
        if (match.Success)
        {
            host = match.Groups[1].Value;
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var portValue))
            {
                port = portValue;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes square brackets from the beginning and end of a string.
    /// </summary>
    /// <param name="s">The string to trim. Example: "[::1]" becomes "::1"</param>
    /// <returns>The string with brackets removed.</returns>
    private static string TrimBrackets(string s) => s.Trim('[', ']');

    /// <summary>
    /// Removes known protocol prefixes from connection string values.
    /// </summary>
    /// <param name="value">The value to clean. Examples: "tcp:localhost" becomes "localhost", "ssl:host:443" becomes "host:443"</param>
    /// <returns>The value with protocol prefix removed, or the original value if no known prefix is found.</returns>
    private static string RemoveProtocolPrefix(string value)
    {
        // Remove common protocol prefixes like "tcp:", "udp:", "ssl:", etc.
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var colonIndex = value.IndexOf(':');
        if (colonIndex > 0 && colonIndex < value.Length - 1)
        {
            var prefix = value[..colonIndex].ToLowerInvariant();
            // Only remove known protocol prefixes, not arbitrary single letters
            if (s_knownProtocols.Contains(prefix))
            {
                return value[(colonIndex + 1)..];
            }
        }

        return value;
    }

    /// <summary>
    /// Gets the default port number for a given URI scheme.
    /// </summary>
    /// <param name="scheme">The URI scheme. Examples: "postgres", "redis", "https"</param>
    /// <returns>The default port number for the scheme, or null if no default is known.</returns>
    private static int? DefaultPortFromScheme(string? scheme)
    {
        if (string.IsNullOrEmpty(scheme))
        {
            return null;
        }

        return s_schemeDefaultPorts.TryGetValue(scheme, out var port) ? port : null;
    }

    /// <summary>
    /// Extracts a port value from key-value pairs using the "port" key.
    /// </summary>
    /// <param name="keyValuePairs">The dictionary of key-value pairs to search.</param>
    /// <returns>The port number if found and valid, or null otherwise.</returns>
    private static int? PortFromKV(Dictionary<string, string> keyValuePairs)
    {
        return keyValuePairs.TryGetValue("port", out var portValue) ? ParseIntSafe(portValue) : null;
    }

    /// <summary>
    /// Safely parses a string as an integer port number (0-65535).
    /// </summary>
    /// <param name="s">The string to parse. Examples: "5432", "443", "invalid"</param>
    /// <returns>The parsed port number if valid, or null if parsing failed or the number is out of range.</returns>
    private static int? ParseIntSafe(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            value >= 0 && value <= 65535)
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Splits a connection string into key-value pairs using semicolon or whitespace delimiters.
    /// </summary>
    /// <param name="connectionString">The connection string to split. Examples: "Host=localhost;Port=5432", "server=host port=1433"</param>
    /// <returns>A dictionary of key-value pairs with case-insensitive keys.</returns>
    private static Dictionary<string, string> SplitIntoDictionary(string connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Split by semicolon first, then by whitespace if no semicolons found
        var parts = connectionString.Contains(';')
            ? connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            : connectionString.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var trimmedPart = part.Trim();
            var equalIndex = trimmedPart.IndexOf('=');
            if (equalIndex > 0 && equalIndex < trimmedPart.Length - 1)
            {
                var key = trimmedPart[..equalIndex].Trim();
                var value = trimmedPart[(equalIndex + 1)..].Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a token on the last occurrence of ':' or ',' to separate host and port.
    /// </summary>
    /// <param name="token">The token to split. Examples: "localhost:5432", "host,1433", "host:8080:extra"</param>
    /// <returns>A tuple with the host part and port part. Port part may be empty if no delimiter is found.</returns>
    private static (string host, string port) SplitOnLast(string token)
    {
        // Split on the last occurrence of ':' or ','
        var lastColonIndex = token.LastIndexOf(':');
        var lastCommaIndex = token.LastIndexOf(',');
        var splitIndex = Math.Max(lastColonIndex, lastCommaIndex);

        if (splitIndex > 0 && splitIndex < token.Length - 1)
        {
            return (token[..splitIndex].Trim(), token[(splitIndex + 1)..].Trim());
        }

        return (token, string.Empty);
    }

    /// <summary>
    /// Determines if a string looks like a hostname rather than a file path or other non-hostname string.
    /// Uses URI validation with conservative heuristics to avoid false positives.
    /// </summary>
    /// <param name="connectionString">The string to evaluate. Examples: "localhost" (valid), "/path/to/file.db" (invalid), "api.example.com" (valid)</param>
    /// <returns>True if the string appears to be a hostname; otherwise false.</returns>
    private static bool LooksLikeHost(string connectionString)
    {
        // Reject strings with '=' (likely key-value pairs)
        if (connectionString.Contains('='))
        {
            return false;
        }

        // Reject obvious file path indicators
        if (connectionString.StartsWith('/') || connectionString.StartsWith('\\') ||
            connectionString.StartsWith("./", StringComparison.Ordinal) || connectionString.StartsWith("../", StringComparison.Ordinal) ||
            (connectionString.Length > 2 && connectionString[1] == ':' && char.IsLetter(connectionString[0])))
        {
            return false;
        }

        // Use Uri parsing to validate hostname - create a fake URI and see if it parses
        var fakeUri = $"scheme://{connectionString.Trim()}";
        return Uri.TryCreate(fakeUri, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host);
    }
}