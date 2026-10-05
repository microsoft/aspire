package com.example.forecast;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.random.RandomGenerator;
import java.util.stream.Collectors;
import java.util.stream.IntStream;

/// The weather data behind this playground's UI, served by a plain JAR with no web framework and
/// launched as `java -jar target/forecast-1.0.0-SNAPSHOT.jar`.
///
/// The Node API in front of it fetches `/forecast` through the `services__forecast__http__0` variable
/// that `withReference` projects, so the sample exercises a Java service participating in service
/// discovery rather than sitting next to the other resources doing nothing.
public final class ForecastService {

    private static final int SHUTDOWN_DRAIN_SECONDS = 2;

    private static final List<String> SUMMARIES = List.of(
        "Freezing", "Bracing", "Chilly", "Cool", "Mild",
        "Warm", "Balmy", "Hot", "Sweltering", "Scorching");

    public static void main(String[] args) throws IOException, InterruptedException {
        var port = port();
        var server = HttpServer.create(new InetSocketAddress(port), 0);

        // Virtual threads because the JDK server is otherwise single-threaded, which would let one slow
        // request stall the health probe Aspire uses to decide the resource is running.
        server.setExecutor(Executors.newVirtualThreadPerTaskExecutor());
        server.createContext("/forecast", ForecastService::forecast);
        server.createContext("/health", exchange -> respond(exchange, 200, "text/plain", "Healthy"));
        server.start();

        System.out.printf("forecast service listening on port %d%n", port);

        // SIGTERM is how `aspire stop` and container runtimes ask the process to go away. Draining the
        // server first lets in-flight requests finish instead of failing at the socket. The latch keeps
        // main alive rather than depending on the server's internal threads being non-daemon.
        var stopped = new CountDownLatch(1);
        Runtime.getRuntime().addShutdownHook(new Thread(() -> {
            System.out.println("forecast service stopping");
            server.stop(SHUTDOWN_DRAIN_SECONDS);
            stopped.countDown();
        }));

        stopped.await();
    }

    private static void forecast(HttpExchange exchange) throws IOException {
        if (!"GET".equals(exchange.getRequestMethod())) {
            respond(exchange, 405, "text/plain", "Method not allowed");
            return;
        }

        var random = RandomGenerator.getDefault();
        var today = Instant.now().truncatedTo(ChronoUnit.DAYS);

        var json = IntStream.rangeClosed(1, 5)
            .mapToObj(day -> {
                var temperatureC = random.nextInt(-20, 55);
                return """
                    {"date":"%s","temperatureC":%d,"temperatureF":%d,"summary":"%s"}"""
                    .formatted(
                        today.plus(day, ChronoUnit.DAYS),
                        temperatureC,
                        32 + (int) (temperatureC / 0.5556),
                        SUMMARIES.get(random.nextInt(SUMMARIES.size())));
            })
            .collect(Collectors.joining(",", "[", "]"));

        respond(exchange, 200, "application/json", json);
    }

    private static void respond(HttpExchange exchange, int status, String contentType, String body) throws IOException {
        var bytes = body.getBytes(StandardCharsets.UTF_8);
        exchange.getResponseHeaders().set("Content-Type", contentType);
        exchange.sendResponseHeaders(status, bytes.length);
        try (var output = exchange.getResponseBody()) {
            output.write(bytes);
        }
    }

    private static int port() {
        // Aspire assigns the port and hands it over as PORT, which is the variable the AppHost names in
        // withHttpEndpoint. Binding a fixed port instead would collide with whatever else is running.
        var value = System.getenv("PORT");
        return value == null || value.isBlank() ? 8080 : Integer.parseInt(value);
    }

    private ForecastService() {
    }
}
