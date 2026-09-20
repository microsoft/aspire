---
applyTo: "src/Aspire.Dashboard/**/*.{cs,razor,js}"
---

# Dashboard Review Patterns

- Dashboard subscription/watch callbacks can run concurrently; protect shared mutable state with locking or concurrent collections.
- Prefer FluentUI for standard interactive controls. Raw HTML is fine when semantics, performance, or UX require it; if working around a FluentUI limitation, cite the FluentUI issue.
- Use `ViewportInformation.IsDesktop` / `IsUltraLowHeight` cascading parameter for responsive layout; throttle (not debounce) resize events to avoid excessive re-renders of the entire component tree.
- Use `@onclick:stopPropagation="true"` on interactive elements inside `FluentDataGrid` rows that have row-click handlers to prevent unintended navigation.
- Prefer JS interop for browser-only, latency-sensitive interactions (clipboard, global DOM listeners). If you register a persistent JS listener, keep a handle and unregister in `DisposeAsync`.
- For high-throughput log/trace/metric streams with a fixed cap, use `CircularBuffer<T>` instead of repeatedly removing the first item from a `List<T>`.
- For bounded channels feeding one consumer, prefer `BoundedChannelFullMode.DropOldest` and set `SingleReader = true`.
- Use `FormatHelpers` for culture-aware date/time/number display. Reserve invariant formatting for intentionally fixed diagnostic formats.
- Localize user-visible dashboard text with resource-backed localizers. Prefer typed localizers and `nameof` keys when practical, but existing model/helpers also generate localized UI text.

## Blazor components

- Avoid `@code` blocks and substantial C# logic in `.razor` files. Keep `.razor` files focused on markup, directives, and simple binding or event expressions; put component state, lifecycle methods, event handlers, and other logic in the matching `.razor.cs` code-behind partial class for better IDE and compiler support.
- Declare injected component dependencies as public, required, init-only properties:

	```csharp
	[Inject]
	public required IDashboardClient DashboardClient { get; init; }
	```

- `public` keeps dependencies visible to component and test infrastructure, `required` expresses that the component cannot operate without the service, and `init` prevents reassignment after component activation.
- Do not use non-public injected properties, mutable `set` accessors, or null-forgiving initializers such as `= null!;`. These weaken compile-time validation and hide missing dependencies when components are constructed in tests.
