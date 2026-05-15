package com.example;

import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.sql.*;
import java.util.*;

import com.azure.core.credential.AccessToken;
import com.azure.core.credential.TokenCredential;
import com.azure.core.credential.TokenRequestContext;
import com.azure.identity.DefaultAzureCredential;
import com.azure.identity.DefaultAzureCredentialBuilder;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

public class App {
    private static final String AZURE_DB_FOR_POSTGRES_SCOPE = "https://ossrdbms-aad.database.windows.net/.default";

    public static void main(String[] args) throws IOException {
        int port = Integer.parseInt(System.getenv().getOrDefault("PORT", "4567"));
        System.out.println("Starting Java service on port " + port);

        // Use the JDK's built-in HTTP server instead of a third-party framework. This avoids
        // pulling in Spark + Jetty 9.x transitives that show up as Component Governance
        // security alerts. The endpoint surface here is intentionally tiny (a single GET / handler),
        // so HttpServer is sufficient.
        HttpServer server = HttpServer.create(new InetSocketAddress(port), 0);
        server.createContext("/", App::handleRoot);
        server.start();

        System.out.println("Java service is ready and listening on port " + port);
    }

    private static void handleRoot(HttpExchange exchange) throws IOException {
        URI requestUri = exchange.getRequestURI();
        System.out.println("Received " + exchange.getRequestMethod() + " request to " + requestUri.getPath());

        // Only handle exact "/" to avoid HttpServer's default prefix matching behavior
        // (it would otherwise dispatch e.g. "/anything" here as well, which we don't want).
        if (!"/".equals(requestUri.getPath())) {
            sendResponse(exchange, 404, "application/json", "{\"error\":\"not found\"}");
            return;
        }

        if (!"GET".equalsIgnoreCase(exchange.getRequestMethod())) {
            exchange.getResponseHeaders().add("Allow", "GET");
            sendResponse(exchange, 405, "application/json", "{\"error\":\"method not allowed\"}");
            return;
        }

        try {
            String response = buildEntriesResponse();
            sendResponse(exchange, 200, "application/json", response);
        } catch (Exception e) {
            System.err.println("Request error: " + e.getMessage());
            e.printStackTrace();
            sendResponse(exchange, 500, "application/json",
                String.format("{\"error\":\"%s\"}", escapeJson(e.getMessage())));
        }
    }

    private static String buildEntriesResponse() throws Exception {
        String uri = System.getenv("DB1_JDBCCONNECTIONSTRING");
        String user = System.getenv("DB1_USERNAME");
        String password = System.getenv("DB1_PASSWORD");

        // If user is not provided, use Entra authentication
        if (user == null || user.isEmpty()) {
            System.out.println("Using Entra authentication");
            DefaultAzureCredential credential = new DefaultAzureCredentialBuilder().build();
            EntraConnInfo connInfo = getEntraConnInfo(credential);

            user = connInfo.user;
            password = connInfo.password;
            System.out.println("Extracted username from token: " + user);
        }

        System.out.println("Connecting to database: " + uri);
        List<String> entries = new ArrayList<>();
        try (Connection conn = DriverManager.getConnection(uri, user, password)) {
            System.out.println("Connected to database successfully");
            try (Statement stmt = conn.createStatement()) {
                stmt.execute("CREATE TABLE IF NOT EXISTS entries (id UUID PRIMARY KEY);");
                System.out.println("Table 'entries' checked/created");
            }
            try (PreparedStatement ps = conn.prepareStatement("INSERT INTO entries (id) VALUES (?);")) {
                UUID newId = UUID.randomUUID();
                ps.setObject(1, newId);
                ps.executeUpdate();
                System.out.println("Inserted new entry: " + newId);
            }
            try (Statement stmt = conn.createStatement();
                 ResultSet rs = stmt.executeQuery("SELECT id FROM entries;")) {
                while (rs.next()) entries.add(rs.getString("id"));
            }
            System.out.println("Total entries retrieved: " + entries.size());
        }

        return String.format("{\"totalEntries\": %d, \"entries\": %s}", entries.size(), entries.toString());
    }

    private static void sendResponse(HttpExchange exchange, int status, String contentType, String body) throws IOException {
        byte[] payload = body.getBytes(StandardCharsets.UTF_8);
        exchange.getResponseHeaders().set("Content-Type", contentType);
        exchange.sendResponseHeaders(status, payload.length);
        try (OutputStream os = exchange.getResponseBody()) {
            os.write(payload);
        }
    }

    private static String escapeJson(String value) {
        if (value == null) {
            return "";
        }
        // Minimal JSON escaping for error strings. Enough for short server-generated messages
        // (e.g. SQL exception text); full RFC 8259 escaping is unnecessary here.
        return value.replace("\\", "\\\\").replace("\"", "\\\"")
            .replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t");
    }

    /**
     * Container for database connection information from Entra authentication.
     */
    static class EntraConnInfo {
        String user;
        String password;

        EntraConnInfo(String user, String password) {
            this.user = user;
            this.password = password;
        }
    }

    /**
     * Decodes a JWT token to extract its payload claims.
     */
    private static Map<String, Object> decodeJwt(String token) throws Exception {
        // JWT layout: header.payload.signature, all base64url-encoded. We only need the
        // middle segment (the claims set) to extract the username; the signature is verified
        // by the issuer when the token was minted.
        String[] parts = token.split("\\.");
        if (parts.length < 2) {
            throw new IllegalArgumentException("Invalid JWT token format");
        }

        String payload = parts[1];
        byte[] decodedBytes = Base64.getUrlDecoder().decode(payload);
        String decodedPayload = new String(decodedBytes, StandardCharsets.UTF_8);

        ObjectMapper mapper = new ObjectMapper();
        return mapper.readValue(decodedPayload, Map.class);
    }

    /**
     * Obtains connection information from Entra authentication for Azure PostgreSQL.
     * Acquires a token and extracts the username from the token claims.
     */
    private static EntraConnInfo getEntraConnInfo(TokenCredential credential) throws Exception {
        // Fetch a new token and extract the username
        TokenRequestContext request = new TokenRequestContext().addScopes(AZURE_DB_FOR_POSTGRES_SCOPE);
        AccessToken tokenResponse = credential.getToken(request).block();

        if (tokenResponse == null) {
            throw new RuntimeException("Failed to acquire token from credential");
        }

        String token = tokenResponse.getToken();
        Map<String, Object> claims = decodeJwt(token);

        String username = null;
        if (claims.containsKey("upn")) {
            username = (String) claims.get("upn");
        } else if (claims.containsKey("preferred_username")) {
            username = (String) claims.get("preferred_username");
        } else if (claims.containsKey("unique_name")) {
            username = (String) claims.get("unique_name");
        }

        if (username == null) {
            throw new RuntimeException("Could not extract username from token. Have you logged in?");
        }

        return new EntraConnInfo(username, token);
    }
}
