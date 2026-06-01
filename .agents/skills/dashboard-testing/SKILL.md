---
name: dashboard-testing
description: Guide for writing tests for the Aspire Dashboard. Use this when asked to create, modify, or debug dashboard unit tests or Blazor component tests.
---

# Aspire Dashboard Testing

This skill provides patterns and practices for writing tests for the Aspire Dashboard. There are two test projects depending on whether the code under test uses Blazor types.

## Test Project Selection

| Project | Location | Use When |
|---------|----------|----------|
| **Aspire.Dashboard.Tests** | `tests/Aspire.Dashboard.Tests/` | Testing code that does **not** use Blazor types (models, helpers, utils, OTLP services, middleware) |
| **Aspire.Dashboard.Components.Tests** | `tests/Aspire.Dashboard.Components.Tests/` | Testing code that **does** use Blazor types (pages, components, controls). Uses bUnit for in-memory rendering |

### Dashboard Source Code

The dashboard source code is in `src/Aspire.Dashboard/`. Key subdirectories:

- `Components/` — Blazor components (pages, controls, layout) → test in **Components.Tests**
- `Model/` — View models, data models, helpers → test in **Dashboard.Tests**
- `Otlp/` — OpenTelemetry protocol handling → test in **Dashboard.Tests**
- `Utils/` — Utility and helper classes → test in **Dashboard.Tests**

## Aspire.Dashboard.Tests (Non-Blazor)

Standard xUnit tests for models, helpers, utilities, middleware, and services that don't depend on Blazor rendering.

### Project Structure

```
tests/Aspire.Dashboard.Tests/
├── Model/                    # ViewModel and model tests
├── Telemetry/                # Telemetry repository tests
├── ConsoleLogsTests/         # Console log parsing tests
├── Integration/              # Integration tests (auth, OTLP, startup)
├── Markdown/                 # Markdown rendering tests
├── Mcp/                      # MCP service tests
├── Middleware/                # HTTP middleware tests
├── FormatHelpersTests.cs     # Utility function tests
├── DashboardOptionsTests.cs  # Configuration tests
└── ...
```

### Test Pattern

```csharp
using Xunit;

namespace Aspire.Dashboard.Tests;

public class FormatHelpersTests
{
    [Theory]
    [InlineData("9", 9d)]
    [InlineData("9.9", 9.9d)]
    [InlineData("0.9", 0.9d)]
    public void FormatNumberWithOptionalDecimalPlaces_InvariantCulture(string expected, double value)
    {
        Assert.Equal(expected, FormatHelpers.FormatNumberWithOptionalDecimalPlaces(value, maxDecimalPlaces: 6, CultureInfo.InvariantCulture));
    }
}
```

Key points:
- No bUnit, no DI container — direct construction and assertions
- Use `[Fact]` for single test cases, `[Theory]` with `[InlineData]` for parameterized tests
- Use `ModelTestHelpers.CreateResource(...)` from shared test utilities to build `ResourceViewModel` instances
- Use hand-rolled fakes (e.g., `MockKnownPropertyLookup`) instead of mocking frameworks

## Aspire.Dashboard.Components.Tests (Blazor/bUnit)

Uses [bUnit](https://bunit.dev) to render and test Blazor components in-memory without a browser.

### Project Structure

```
tests/Aspire.Dashboard.Components.Tests/
├── Pages/                    # Full page component tests
│   ├── ResourcesTests.cs
│   ├── ConsoleLogsTests.cs
│   ├── MetricsTests.cs
│   ├── StructuredLogsTests.cs
│   ├── TraceDetailsTests.cs
│   └── LoginTests.cs
├── Controls/                 # Individual control tests
│   ├── ResourceDetailsTests.cs
│   ├── PlotlyChartTests.cs
│   ├── ChartFiltersTests.cs
│   └── ...
├── Interactions/             # Interaction provider tests
├── Layout/                   # Layout component tests
├── Model/                    # Component model tests
├── Shared/                   # Setup helpers and test utilities
│   ├── DashboardPageTestContext.cs
│   ├── FluentUISetupHelpers.cs
│   ├── ResourceSetupHelpers.cs
│   ├── MetricsSetupHelpers.cs
│   ├── StructuredLogsSetupHelpers.cs
│   ├── IntegrationTestHelpers.cs
│   ├── TestLocalStorage.cs
│   ├── TestTimeProvider.cs
│   └── ...
└── GridColumnManagerTests.cs
```

### Base Test Class

All bUnit component tests must extend `DashboardTestContext`:

```csharp
using Bunit;

namespace Aspire.Dashboard.Components.Tests.Shared;

public abstract class DashboardTestContext : TestContext
{
    public DashboardTestContext()
    {
        // Increase from default 1 second as Helix/GitHub Actions can be slow.
        DefaultWaitTimeout = TimeSpan.FromSeconds(10);
    }
}
```

### Basic Component Test Pattern

```csharp
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.Dashboard.Model;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ResourceDetailsTests : DashboardTestContext
{
    [Fact]
    public void Render_BasicResource_DisplaysProperties()
    {
        // Arrange — register services using shared setup helpers
        ResourceSetupHelpers.SetupResourceDetails(this);

        var resource = ModelTestHelpers.CreateResource(
            resourceName: "myapp",
            state: KnownResourceState.Running);

        // Act — render the component
        var cut = RenderComponent<ResourceDetails>(builder =>
        {
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.ShowSpecOnlyToggle, true);
        });

        // Assert — query the rendered DOM
        var rows = cut.FindAll(".resource-detail-row");
        Assert.NotEmpty(rows);
    }
}
```

### Page-Level Test Pattern

```csharp
using System.Threading.Channels;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class ResourcesTests : DashboardTestContext
{
    [Fact]
    public void UpdateResources_FiltersUpdated()
    {
        // Arrange
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var initialResources = new List<ResourceViewModel>
        {
            ModelTestHelpers.CreateResource(resourceName: "Resource1", resourceType: "Type1", state: KnownResourceState.Running),
        };
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            initialResources: initialResources,
            resourceChannelProvider: () => channel);

        ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

        // Act
        var cut = RenderComponent<Components.Pages.Resources>(builder =>
        {
            builder.AddCascadingValue(viewport);
        });

        // Assert
        Assert.Collection(cut.Instance.PageViewModel.ResourceTypesToVisibility.OrderBy(kvp => kvp.Key),
            kvp => Assert.Equal("Type1", kvp.Key));
    }
}
```

## Shared Setup Helpers

Dashboard services require extensive DI setup (telemetry, storage, localization, FluentUI JS interop mocks, etc.). Reuse existing shared setup methods to avoid duplicate registration logic. **When adding tests for a new area, add a new setup helper rather than duplicating setup across test classes.**

### Setup Helper Index

| Helper | Location | Purpose |
|--------|----------|---------|
| `FluentUISetupHelpers.AddCommonDashboardServices()` | `Shared/FluentUISetupHelpers.cs` | Registers core DI services shared by all dashboard pages (localization, storage, telemetry, theme, dialog, shortcuts, etc.) |
| `FluentUISetupHelpers.SetupFluentUIComponents()` | `Shared/FluentUISetupHelpers.cs` | Calls `AddFluentUIComponents()` and configures the menu provider for tests |
| `FluentUISetupHelpers.SetupDialogInfrastructure()` | `Shared/FluentUISetupHelpers.cs` | Combines common services + FluentUI components + dialog provider JS mocks |
| `FluentUISetupHelpers.SetupFluentDataGrid()` | `Shared/FluentUISetupHelpers.cs` | Mocks FluentDataGrid JS interop |
| `FluentUISetupHelpers.SetupFluentSearch()` | `Shared/FluentUISetupHelpers.cs` | Mocks FluentSearch JS interop |
| `FluentUISetupHelpers.SetupFluentMenu()` | `Shared/FluentUISetupHelpers.cs` | Mocks FluentMenu JS interop |
| `ResourceSetupHelpers.SetupResourcesPage()` | `Shared/ResourceSetupHelpers.cs` | Full setup for the Resources page |
| `ResourceSetupHelpers.SetupResourceDetails()` | `Shared/ResourceSetupHelpers.cs` | Setup for ResourceDetails control |
| `MetricsSetupHelpers.SetupMetricsPage()` | `Shared/MetricsSetupHelpers.cs` | Full setup for the Metrics page |
| `MetricsSetupHelpers.SetupChartContainer()` | `Shared/MetricsSetupHelpers.cs` | Setup for chart container and Plotly |
| `StructuredLogsSetupHelpers.SetupStructuredLogsDetails()` | `Shared/StructuredLogsSetupHelpers.cs` | Setup for structured log details |
| `IntegrationTestHelpers.CreateLoggerFactory()` | `Shared/IntegrationTestHelpers.cs` | Creates `ILoggerFactory` wired to xUnit test output |

### FluentUI JS Interop Mocks

FluentUI Blazor components require JavaScript interop. bUnit runs without a browser, so all JS calls must be mocked. Use the helpers from `FluentUISetupHelpers`:

```csharp
// Each FluentUI component has a corresponding setup method
FluentUISetupHelpers.SetupFluentDataGrid(context);
FluentUISetupHelpers.SetupFluentSearch(context);
FluentUISetupHelpers.SetupFluentMenu(context);
FluentUISetupHelpers.SetupFluentDivider(context);
FluentUISetupHelpers.SetupFluentAnchor(context);
FluentUISetupHelpers.SetupFluentKeyCode(context);
FluentUISetupHelpers.SetupFluentToolbar(context);
FluentUISetupHelpers.SetupFluentOverflow(context);
FluentUISetupHelpers.SetupFluentTab(context);
FluentUISetupHelpers.SetupFluentList(context);
FluentUISetupHelpers.SetupFluentCheckbox(context);
FluentUISetupHelpers.SetupFluentTextField(context);
FluentUISetupHelpers.SetupFluentInputLabel(context);
FluentUISetupHelpers.SetupFluentAnchoredRegion(context);
FluentUISetupHelpers.SetupFluentDialogProvider(context);
```

### Adding a New Setup Helper

When testing a new component area, create a dedicated setup helper in `Shared/`:

```csharp
// Shared/MyFeatureSetupHelpers.cs
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class MyFeatureSetupHelpers
{
    public static void SetupMyFeaturePage(TestContext context, IDashboardClient? dashboardClient = null)
    {
        // 1. Register common dashboard services
        FluentUISetupHelpers.AddCommonDashboardServices(context);

        // 2. Setup FluentUI JS mocks for components used by the page
        FluentUISetupHelpers.SetupFluentDataGrid(context);
        FluentUISetupHelpers.SetupFluentSearch(context);
        FluentUISetupHelpers.SetupFluentMenu(context);

        // 3. Register page-specific services
        context.Services.AddSingleton<IDashboardClient>(dashboardClient ?? new TestDashboardClient());
        context.Services.AddSingleton<IconResolver>();
    }
}
```

## Shared Test Fakes

Both test projects use hand-rolled fakes — no mocking framework is used. Cross-project fakes live in `tests/Shared/` (e.g., `TestDashboardClient`, `ModelTestHelpers`), while bUnit-specific fakes live in `tests/Aspire.Dashboard.Components.Tests/Shared/` (e.g., `TestLocalStorage`, `TestTimeProvider`).

| Fake | Purpose |
|------|---------|
| `TestDashboardClient` | Configurable `IDashboardClient` with channel providers for resources, console logs, interactions, and commands |
| `TestDialogService` | Fake dialog service |
| `TestSessionStorage` | In-memory session storage |
| `TestStringLocalizer` | Pass-through string localizer |
| `TestDashboardTelemetrySender` | No-op telemetry sender |
| `TestAIContextProvider` | No-op AI context provider |
| `ModelTestHelpers.CreateResource()` | Factory for building `ResourceViewModel` instances with sensible defaults |

### Using TestDashboardClient

`TestDashboardClient` is constructor-configurable with channel providers:

```csharp
var resourceChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
var consoleLogsChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();

var dashboardClient = new TestDashboardClient(
    isEnabled: true,
    initialResources: [testResource],
    resourceChannelProvider: () => resourceChannel,
    consoleLogsChannelProvider: name => consoleLogsChannel);
```

### Using ModelTestHelpers

Create test resource view models with keyword arguments:

```csharp
using Aspire.Tests.Shared.DashboardModel;

var resource = ModelTestHelpers.CreateResource(
    resourceName: "myapp",
    resourceType: "Project",
    state: KnownResourceState.Running);
```

## Test Conventions

### DO: Use `[UseCulture("en-US")]` for Culture-Sensitive Component Tests

Apply `[UseCulture("en-US")]` to bUnit test classes that assert culture-sensitive formatting (for example, numbers or dates) so those tests run deterministically across environments:

```csharp
[UseCulture("en-US")]
public partial class ResourcesTests : DashboardTestContext
```

### DO: Reuse Shared Setup Methods

Call existing helpers instead of duplicating DI registrations:

```csharp
// DO: Use the shared helper
ResourceSetupHelpers.SetupResourcesPage(this, viewport, dashboardClient);

// DON'T: Duplicate service registration in every test class
Services.AddSingleton<TelemetryRepository>();
Services.AddSingleton<PauseManager>();
Services.AddSingleton<IDialogService, DialogService>();
// ... 20 more lines
```

### DO: Create New Setup Helpers for New Areas

If testing a new page or component area, add a setup helper in `Shared/` to consolidate the setup:

```csharp
// DO: Create a helper when multiple tests need the same setup
internal static class NewFeatureSetupHelpers
{
    public static void SetupNewFeaturePage(TestContext context) { ... }
}

// DON'T: Copy-paste setup across test methods
```

### DO: Use `WaitForAssertion` for Async State Changes

When component state updates happen asynchronously, use bUnit's `WaitForAssertion`:

```csharp
cut.WaitForAssertion(() =>
{
    var items = cut.FindAll(".resource-row");
    Assert.Equal(3, items.Count);
});
```

### DO: Use Channels to Simulate Real-Time Updates

Push changes through channels to simulate dashboard data updates:

```csharp
var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
var dashboardClient = new TestDashboardClient(
    isEnabled: true,
    initialResources: [],
    resourceChannelProvider: () => channel);

// Render the component...

// Simulate an update
channel.Writer.TryWrite([
    new ResourceViewModelChange(
        ResourceViewModelChangeType.Upsert,
        ModelTestHelpers.CreateResource("newResource"))
]);

// Wait for the UI to update
cut.WaitForAssertion(() =>
{
    Assert.Equal(1, cut.FindAll(".resource-row").Count);
});
```

### DO: Provide ViewportInformation for Responsive Components

Many dashboard pages require viewport information:

```csharp
var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

// Set on DimensionManager
var dimensionManager = Services.GetRequiredService<DimensionManager>();
dimensionManager.InvokeOnViewportInformationChanged(viewport);

// Pass as cascading parameter
var cut = RenderComponent<Components.Pages.Resources>(builder =>
{
    builder.AddCascadingValue(viewport);
});
```

### DON'T: Use Mocking Frameworks

The project uses hand-rolled fakes:

```csharp
// DON'T: No mocking frameworks
var mock = new Mock<IDashboardClient>();

// DO: Use the provided test fakes
var client = new TestDashboardClient(isEnabled: true, initialResources: resources);
```

### DON'T: Register Services Manually When a Helper Exists

```csharp
// DON'T: Manual FluentUI setup
var module = JSInterop.SetupModule("./_content/Microsoft.FluentUI.../FluentDataGrid.razor.js");
module.SetupVoid("enableColumnResizing", _ => true);

// DO: Use the helper
FluentUISetupHelpers.SetupFluentDataGrid(this);
```

## Running Dashboard Tests

```bash
# Run non-Blazor dashboard tests
dotnet test tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"

# Run Blazor component tests
dotnet test tests/Aspire.Dashboard.Components.Tests/Aspire.Dashboard.Components.Tests.csproj -- --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"

# Run a specific test
dotnet test tests/Aspire.Dashboard.Components.Tests/Aspire.Dashboard.Components.Tests.csproj -- --filter-method "*.UpdateResources_FiltersUpdated" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```
