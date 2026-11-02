// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

package dev.aspire;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import org.junit.jupiter.api.Disabled;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class ServiceTest {
    private HttpResponse<String> get(String path) throws Exception {
        var client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(10)).build();
        var request = HttpRequest.newBuilder(URI.create(System.getenv("JAVA_SERVICE_URL") + path))
            .timeout(Duration.ofSeconds(10)).build();
        return client.send(request, HttpResponse.BodyHandlers.ofString());
    }

    @Test
    void serviceResponds() throws Exception {
        var response = get("/");
        assertEquals(200, response.statusCode());
        assertEquals("Aspire CLI testing", response.body());
    }

    @Test
    void serviceIsHealthy() throws Exception {
        assertEquals(200, get("/health").statusCode());
    }

    @Test
    void controlledFailure() {
        assertFalse(Boolean.parseBoolean(System.getenv("JAVA_PLAYGROUND_FAIL")), "intentional Java playground failure");
    }

    @Test
    @Disabled("intentional skipped example")
    void skippedExample() { }
}
