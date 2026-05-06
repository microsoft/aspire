// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class DashboardCommandExecutorTests
{
    [Fact]
    public void CreateCommandArguments_CreatesTypedObjectFromInputs()
    {
        var arguments = DashboardCommandExecutor.CreateCommandArguments(
            [
                new InteractionInput { Name = "selector", InputType = InputType.Text, Value = "#submit" },
                new InteractionInput { Name = "count", InputType = InputType.Number, Value = "1.5" },
                new InteractionInput { Name = "enabled", InputType = InputType.Boolean, Value = "true" },
                new InteractionInput { Name = "mode", InputType = InputType.Choice, Value = "fast" },
                new InteractionInput { Name = "secret", InputType = InputType.SecretText, Value = "password" },
                new InteractionInput { Name = "empty", InputType = InputType.Text },
                new InteractionInput { Name = "invalidNumber", InputType = InputType.Number, Value = "not-a-number" },
            ]);

        Assert.Equal(Value.KindOneofCase.StructValue, arguments.KindCase);
        var fields = arguments.StructValue.Fields;
        Assert.Equal("#submit", fields["selector"].StringValue);
        Assert.Equal(1.5, fields["count"].NumberValue);
        Assert.True(fields["enabled"].BoolValue);
        Assert.Equal("fast", fields["mode"].StringValue);
        Assert.Equal("password", fields["secret"].StringValue);
        Assert.DoesNotContain("empty", fields.Keys);
        Assert.DoesNotContain("invalidNumber", fields.Keys);
    }
}
