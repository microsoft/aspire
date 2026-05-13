# Aspire.Cli.Benchmarks

Profiling harness for `Aspire.Cli.Documentation.Docs.LlmsTxtParser`. Measures
parse time, allocations, and structural memory amplification on the live
`llms-full.txt` corpus from aspire.dev.

## Usage

The harness needs a copy of `llms-full.txt`. It looks for one in this order:

1. `--input <path>`
2. environment variable `LLMS_FULL_TXT`
3. A fresh `aspire-bench-*` temp subdirectory (downloads `llms-full.txt` from
   aspire.dev on every run and cleans it up at process exit — set `LLMS_FULL_TXT`
   or pass `--input` to reuse a local copy).

### Benchmarks (BDN)

```bash
dotnet run -c Release --project benchmarks/Aspire.Cli.Benchmarks
```

Release configuration is required — BDN rejects Debug builds. Pass BDN filters
after `--`:

```bash
dotnet run -c Release --project benchmarks/Aspire.Cli.Benchmarks -- --filter '*ParseAsync*'
```

### Refresh corpus

```bash
dotnet run -c Release --project benchmarks/Aspire.Cli.Benchmarks -- --refresh
```

## Note

Not shipped — lives under `benchmarks/` purely for measuring parser changes.
