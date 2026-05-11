# Aspire.Cli.Benchmarks

Profiling harness for `Aspire.Cli.Documentation.Docs.LlmsTxtParser`. Measures
parse time, allocations, and structural memory amplification on the live
`llms-full.txt` corpus from aspire.dev.

## Usage

The harness needs a copy of `llms-full.txt`. It looks for one in this order:

1. `--input <path>`
2. environment variable `LLMS_FULL_TXT`
3. `<TempPath>/llms-full.txt` (will download on first run from aspire.dev).

### Benchmarks (BDN)

```bash
dotnet run -c Release --project benchmarks/Aspire.Cli.Benchmarks
```

Release configuration is required — BDN rejects Debug builds. Pass BDN filters
after `--`:

```bash
dotnet run -c Release --project benchmarks/Aspire.Cli.Benchmarks -- --filter '*ParseAsync*'
```

### Inspect structural metrics (no BDN, fast)

```bash
dotnet run -c Release --project benchmarks/Aspire.Cli.Benchmarks -- --inspect
```

### Refresh corpus

```bash
dotnet run -c Release --project benchmarks/Aspire.Cli.Benchmarks -- --refresh
```

## Note

Not shipped — lives under `benchmarks/` purely for measuring parser changes.
