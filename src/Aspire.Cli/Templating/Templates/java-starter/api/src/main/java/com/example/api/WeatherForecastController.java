package com.example.api;

import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.List;
import java.util.concurrent.ThreadLocalRandom;
import java.util.stream.IntStream;

import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class WeatherForecastController {

	private static final List<String> Summaries = List.of(
		"Freezing", "Bracing", "Chilly", "Cool", "Mild",
		"Warm", "Balmy", "Hot", "Sweltering", "Scorching");

	/**
	 * Serialized as {@code {"date","temperatureC","temperatureF","summary"}}, which is the shape the
	 * React frontend in ../frontend expects. Records are serialized by their component names, so
	 * renaming a component here is a breaking change for the frontend.
	 */
	public record WeatherForecast(String date, int temperatureC, int temperatureF, String summary) {
	}

	@GetMapping("/api/weatherforecast")
	public List<WeatherForecast> weatherForecast() {
		var random = ThreadLocalRandom.current();

		return IntStream.rangeClosed(1, 5)
			.mapToObj(day -> {
				var temperatureC = random.nextInt(-20, 55);

				return new WeatherForecast(
					Instant.now().plus(day, ChronoUnit.DAYS).toString(),
					temperatureC,
					// Matches the Celsius-to-Fahrenheit conversion the other Aspire starter templates
					// use, so every language's starter reports the same numbers for the same input.
					32 + (int) (temperatureC / 0.5556),
					Summaries.get(random.nextInt(Summaries.size())));
			})
			.toList();
	}
}
