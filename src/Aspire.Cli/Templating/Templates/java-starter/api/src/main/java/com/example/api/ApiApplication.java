package com.example.api;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;

/**
 * Sample Spring Boot API for the Aspire starter template.
 *
 * <p>Aspire injects:
 * <ul>
 *   <li>{@code SERVER_PORT} - the port the AppHost's HTTP endpoint expects this app to listen on.</li>
 *   <li>{@code OTEL_EXPORTER_OTLP_*} - the dashboard's OTLP endpoint and headers, consumed by the
 *       OpenTelemetry Java agent that {@code withOtelAgentDefaultPath()} attaches.</li>
 * </ul>
 */
@SpringBootApplication
public class ApiApplication {

	public static void main(String[] args) {
		SpringApplication.run(ApiApplication.class, args);
	}
}
