# Aspire.Dashboard.Benchmarks

## SQLite timestamp filters

```powershell
dotnet run -c Release --project benchmarks\Aspire.Dashboard.Benchmarks -- --filter '*SqliteTimestampFilterBenchmarks*'
```

The benchmarks call the dashboard's SQLite repository against a temporary database seeded with 480,000 spans and 200,000 structured logs across 100 resources. Setup validates the expected result counts and is not timed.

| Benchmark | Query | Expected matches |
|---|---|---:|
| `TraceSummariesForActivityBucket` | 30-second range, all resources | 300 |
| `TraceSummariesForSelectedResource` | Full time range, one resource with 1% of the traces | 200 |
| `LogSummariesForTimeWindow` | 30-second range, all resources, including exception and GenAI attributes | 3,000 |

Each query reads the first page of 100 results and counts all matches. Compare runs with the same fixture and machine to measure changes in query time and allocations.

## SQLite trace ingestion

```powershell
dotnet run -c Release --project benchmarks\Aspire.Dashboard.Benchmarks -- --filter '*AddTracesLargeBatch*'
```

Each iteration ingests 10,000 spans into a fresh, empty database. Schema initialization and database cleanup are excluded from the measurement.
