package com.example.worker;

import java.time.Duration;
import java.time.Instant;
import java.util.concurrent.CountDownLatch;

/// A background worker with no web framework, launched as `java -jar target/worker.jar`.
///
/// Aspire treats it as a long-running resource with no endpoints, so the only signals it produces are
/// its console logs and its lifetime.
public final class Worker {

	public static void main(String[] args) throws InterruptedException {
		var interval = Duration.ofSeconds(intervalSeconds(args));

		System.out.printf("worker starting, interval=%ds%n", interval.toSeconds());

		// A shutdown hook plus a latch, rather than a bare loop, so SIGTERM from `aspire stop` or from a
		// container runtime drains cleanly instead of killing the JVM mid-iteration.
		var stopped = new CountDownLatch(1);
		Runtime.getRuntime().addShutdownHook(new Thread(() -> {
			System.out.println("worker stopping");
			stopped.countDown();
		}));

		while (!stopped.await(interval.toMillis(), java.util.concurrent.TimeUnit.MILLISECONDS)) {
			System.out.printf("worker tick at %s%n", Instant.now());
		}
	}

	private static long intervalSeconds(String[] args) {
		// Arguments arrive after the JAR path, which is how AddJavaApp's jarPath overload passes them.
		for (var i = 0; i < args.length - 1; i++) {
			if ("--interval-seconds".equals(args[i])) {
				return Long.parseLong(args[i + 1]);
			}
		}

		return 5;
	}

	private Worker() {
	}
}
