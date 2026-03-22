# Test Review Guidelines: Flaky Test Patterns

When reviewing test code or writing new tests, watch for these common patterns that lead to flaky tests. These patterns apply broadly across the Aspire test suite and are useful for **code review**, **test authoring**, and **root cause analysis** of intermittent failures.

## Common Flaky Test Patterns

| Pattern | Symptom | What to look for | Fix |
|---------|---------|-------------------|-----|
| **Thread-unsafe collections** | `Assert.Contains()` missing items; assertion sees partial data | Test fakes/mocks using `List<T>`, `Dictionary<T>`, or other non-thread-safe types that are mutated from concurrent callers (e.g., pipeline steps running via `Task.WhenAll`) | Add `lock` synchronization to mutation methods, or use `ConcurrentBag<T>` / `ConcurrentDictionary<T>`. Check **all** test fakes used by the test, not just the one in the stack trace. |
| **Race condition on startup** | Intermittent timeout or "not started" errors | `WaitForTextAsync("Application started.")` — log-based readiness checks are fragile under CI contention because log lines can arrive out of order or be delayed | Use `WaitForHealthyAsync()` or structured readiness checks instead of log text matching |
| **Shared timeout budget** | `TaskCanceledException` in fixture `InitializeAsync`; one phase starves the other | A single `CancellationTokenSource` shared across startup and readiness phases | Use separate `CancellationTokenSource` for each phase |
| **Sequential service waits** | `TaskCanceledException` in `WaitReadyStateAsync`; timeout under CI load | Waiting for multiple services one after another, where total wait = sum of individual waits | Wait for services in parallel with `Task.WhenAll` instead of sequentially |
| **Port conflicts** | `AddressInUseException` or `SocketException` | `randomizePorts: false` or hardcoded port numbers in test setup | Ensure `randomizePorts: true` in `TestDistributedApplicationBuilder.Create()` |
| **File locking (Windows)** | `IOException: The process cannot access the file` on Windows CI | Tests that create/read temp files without proper cleanup or exclusive access | Add retry logic, use unique temp directories per test, or ensure proper `IDisposable` cleanup |
| **Order-dependent state** | Test passes alone, fails when run with other tests | Static state, shared singletons, or environment variable mutations without cleanup | Ensure proper test isolation; use fresh instances in each test; clean up static state |
| **Contention-only failure** | Passes 100% in isolation, fails 10–20% in quarantine runs | Shared resources (ports, CTS, fixtures); the test only fails when running alongside other tests | Look for shared resources; parallelize waits; add timing margins; consider test isolation improvements |
| **Snapshot drift** | `VerifyException` with unexpected diff in Verify snapshots | Test output includes timestamps, GUIDs, or environment-specific paths | Use Verify scrubbers to normalize dynamic content; update snapshots with `dotnet verify accept -y` |

## Review Checklist for Test PRs

When reviewing PRs that add or modify tests, check for:

- [ ] **Thread safety**: Are test fakes/mocks mutated from concurrent code paths? If so, are they synchronized?
- [ ] **Readiness checks**: Does the test use `WaitForHealthyAsync()` rather than `WaitForTextAsync()` for service readiness?
- [ ] **Timeout isolation**: Does each phase (startup, readiness, assertion) have its own timeout budget?
- [ ] **Port randomization**: Is `randomizePorts: true` used (or is the default relied upon)?
- [ ] **Test isolation**: Does the test clean up all shared state? No static mutations without restoration?
- [ ] **Platform considerations**: Will this test work on Windows, Linux, and macOS? Watch for path separators, file locking, and encoding differences.
