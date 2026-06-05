package com.example;

import java.sql.*;
import java.util.*;
import java.util.UUID;
import spark.Spark;
import com.azure.core.credential.AccessToken;
import com.azure.core.credential.TokenCredential;
import com.azure.core.credential.TokenRequestContext;
import com.azure.identity.DefaultAzureCredential;
import com.azure.identity.DefaultAzureCredentialBuilder;
import java.nio.charset.StandardCharsets;
import com.fasterxml.jackson.databind.ObjectMapper;

public class App {
    private static final String AZURE_DB_FOR_POSTGRES_SCOPE = "https://ossrdbms-aad.database.windows.net/.default";
    
    public static void main(String[] args) {
        int port = Integer.parseInt(System.getenv().getOrDefault("PORT", "4567"));
        System.out.println("Starting Java service on port " + port);
        
        Spark.port(port);
        
        System.out.println("Configuring routes...");
        Spark.get("/", (req, res) -> {
            System.out.println("Received request to /");
            
            String uri = System.getenv("DB1_JDBCCONNECTIONSTRING");
            String user = System.getenv("DB1_USERNAME");
            String password = System.getenv("DB1_PASSWORD");
            
            // If user is not provided, use Entra authentication
            if (user == null || user.isEmpty()) {
                System.out.println("Using Entra authentication");
                DefaultAzureCredential credential = new DefaultAzureCredentialBuilder().build();
                EntraConnInfo connInfo = getEntraConnInfo(credential);
                
                // If user is not provided, use the username from the token
                if (user == null || user.isEmpty()) {
                    user = connInfo.user;
                    System.out.println("Extracted username from token: " + user);
                }
            }
            
            System.out.println("Connecting to database: " + uri);
            List<String> entries = new ArrayList<>();
            try (Connection conn = DriverManager.getConnection(uri, user, password)) {
                System.out.println("Connected to database successfully");
                try (Statement stmt = conn.createStatement()) {
                    stmt.execute("CREATE TABLE IF NOT EXISTS entries (id UUID PRIMARY KEY);");
                    System.out.println("Table 'entries' checked/created");
                }
                try (PreparedStatement ps = conn.prepareStatement("INSERT INTO entries (id) VALUES (?);");) {
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
            } catch (Exception e) {
                System.err.println("Database error: " + e.getMessage());
                e.printStackTrace();
                throw e;
            }
            res.type("application/json");
            String response = String.format("{\"totalEntries\": %d, \"entries\": %s}", entries.size(), entries.toString());
            System.out.println("Returning response with " + entries.size() + " entries");
            return response;
        });
        
        System.out.println("Java service is ready and listening on port " + port);
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
