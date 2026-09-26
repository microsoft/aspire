// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public partial class LoginTests : DashboardTestContext
{
    private readonly ITestOutputHelper _testOutputHelper;

    public LoginTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public void Initialize_LongRunningAuthStateFunc_EditContextSet()
    {
        // Arrange
        SetupLoginServices();

        // This represents a long running auth state task.
        var tcs = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentTooltipProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(1);
            builder.AddAttribute(2, nameof(CascadingValue<Task<AuthenticationState>>.Value), tcs.Task);
            builder.AddAttribute(3, nameof(CascadingValue<Task<AuthenticationState>>.ChildContent), (RenderFragment)(childBuilder =>
            {
                childBuilder.OpenComponent<Components.Pages.Login>(0);
                childBuilder.CloseComponent();
            }));
            builder.CloseComponent();
        });

        var instance = cut.FindComponent<Components.Pages.Login>().Instance;
        var logger = Services.GetRequiredService<ILogger<ConsoleLogsTests>>();
        var loc = Services.GetRequiredService<IStringLocalizer<Resources.ConsoleLogs>>();

        cut.WaitForState(() => instance.EditContext != null);

        var tokenInput = cut.FindComponent<FluentTextInput>();
        Assert.Equal(TextInputType.Password, tokenInput.Instance.TextInputType);
        Assert.Equal("password", tokenInput.Find("#token-text-field").GetAttribute("type"));
    }

    [Fact]
    public void Submit_EmptyToken_RendersSingleValidationMessage()
    {
        SetupLoginServices();

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentTooltipProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Components.Pages.Login>(1);
            builder.CloseComponent();
        });

        cut.Find("form").Submit();

        var validationMessage = Assert.Single(cut.FindAll(".fluent-validation-message"));
        Assert.Equal("Token is required", validationMessage.TextContent.Trim());
        var tokenField = Assert.Single(cut.FindAll("#token-field"));
        Assert.Contains("invalid", tokenField.ClassList);
    }

    private void SetupLoginServices()
    {
        JSInterop.SetupModule("/Components/Pages/Login.razor.js");

        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentTextField(this);

        var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(_testOutputHelper);

        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<ILoggerFactory>(loggerFactory);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient());
    }
}
