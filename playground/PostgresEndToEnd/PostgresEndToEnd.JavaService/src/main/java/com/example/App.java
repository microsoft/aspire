package com.example;

import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.Statement;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import com.azure.core.credential.AccessToken;
import com.azure.core.credential.TokenCredential;
import com.azure.core.credential.TokenRequestContext;
import com.azure.identity.DefaultAzureCredentialBuilder;
import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;
import io.javalin.Javalin;

/**
 * Minimal HTTP service used by Aspire's PostgresEndToEnd playground. Exposes a single
 * GET / endpoint that inserts a row into a "entries" table and returns every row,
 * optionally authenticating against Azure Database for PostgreSQL with Entra credentials.
 */
public final class App {
    private static final String AZURE_DB_FOR_POSTGRES_SCOPE = "https://ossrdbms-aad.database.windows.net/.default";
    private static final ObjectMapper MAPPER = new ObjectMapper();

    private App() {
    }

    public static void main(String[] args) {
        var port = Integer.parseInt(System.getenv().getOrDefault("PORT", "4567"));
        // Javalin 7 requires routes to be registered via config.routes inside the
        // create() lambda; you can no longer attach them after start().
        Javalin.create(config ->
            config.routes.get("/", ctx -> ctx.json(listEntries()))
        ).start(port);
    }

    private static Map<String, Object> listEntries() throws Exception {
        var uri = System.getenv("DB1_JDBCCONNECTIONSTRING");
        var user = System.getenv("DB1_USERNAME");
        var password = System.getenv("DB1_PASSWORD");

        // If no username is configured, fall back to Entra authentication. We extract the
        // username from the access token's claims and use the token as the password — the
        // standard pattern for Azure Database for PostgreSQL with passwordless auth.
        if (user == null || user.isEmpty()) {
            var conn = resolveEntraConnection(new DefaultAzureCredentialBuilder().build());
            user = conn.user();
            password = conn.password();
        }

        var entries = new ArrayList<String>();
        try (var connection = DriverManager.getConnection(uri, user, password)) {
            try (Statement statement = connection.createStatement()) {
                statement.execute("CREATE TABLE IF NOT EXISTS entries (id UUID PRIMARY KEY);");
            }
            try (PreparedStatement insert = connection.prepareStatement("INSERT INTO entries (id) VALUES (?);")) {
                insert.setObject(1, UUID.randomUUID());
                insert.executeUpdate();
            }
            try (Statement statement = connection.createStatement();
                 ResultSet rs = statement.executeQuery("SELECT id FROM entries;")) {
                while (rs.next()) {
                    entries.add(rs.getString("id"));
                }
            }
        }

        return Map.of("totalEntries", entries.size(), "entries", entries);
    }

    /**
     * Connection info derived from an Entra access token.
     */
    private record EntraConnection(String user, String password) {
    }

    private static EntraConnection resolveEntraConnection(TokenCredential credential) {
        var request = new TokenRequestContext().addScopes(AZURE_DB_FOR_POSTGRES_SCOPE);
        AccessToken accessToken = credential.getToken(request).block();
        if (accessToken == null) {
            throw new IllegalStateException("Failed to acquire an Entra access token.");
        }

        var token = accessToken.getToken();
        var claims = decodeJwtPayload(token);

        // Entra emits the username under different claim names depending on the account type
        // (work/school vs. personal, federated vs. cloud-only). Try the common ones in order.
        for (var name : List.of("upn", "preferred_username", "unique_name")) {
            if (claims.get(name) instanceof String value && !value.isEmpty()) {
                return new EntraConnection(value, token);
            }
        }

        throw new IllegalStateException("Could not extract a username from the access token. Have you logged in?");
    }

    private static Map<String, Object> decodeJwtPayload(String token) {
        // JWT layout: header.payload.signature, each segment base64url-encoded. We only
        // need the middle (claims) segment; the signature was verified by the issuer.
        var parts = token.split("\\.");
        if (parts.length < 2) {
            throw new IllegalArgumentException("Invalid JWT token format.");
        }
        try {
            return MAPPER.readValue(Base64.getUrlDecoder().decode(parts[1]),
                new TypeReference<Map<String, Object>>() { });
        } catch (java.io.IOException ex) {
            throw new IllegalStateException("Failed to decode JWT payload.", ex);
        }
    }
}
