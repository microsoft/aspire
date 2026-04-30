// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Aspire.DashboardService.Proto.V1;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class InputViewModelTests
{
    [Fact]
    public void InputViewModel_ChoiceWithoutPlaceholder_DefaultsToFirstOption()
    {
        // Arrange
        var input = new InteractionInput
        {
            Label = "Choose Color",
            InputType = InputType.Choice,
        };
        input.Options.Add("red", "Red");
        input.Options.Add("blue", "Blue");
        input.Options.Add("green", "Green");

        // Act
        var viewModel = new InputViewModel(input);

        // Assert
        Assert.Equal("red", viewModel.Value);
    }

    [Fact]
    public void InputViewModel_ChoiceWithPlaceholder_DoesNotDefaultToFirstOption()
    {
        // Arrange
        var input = new InteractionInput
        {
            Label = "Choose Color",
            InputType = InputType.Choice,
            Placeholder = "Select a color"
        };
        input.Options.Add("red", "Red");
        input.Options.Add("blue", "Blue");
        input.Options.Add("green", "Green");

        // Act
        var viewModel = new InputViewModel(input);

        // Assert - proto strings default to empty string, not null
        Assert.True(string.IsNullOrEmpty(viewModel.Value));
    }

    [Fact]
    public void InputViewModel_ChoiceWithExistingValue_KeepsExistingValue()
    {
        // Arrange
        var input = new InteractionInput
        {
            Label = "Choose Color",
            InputType = InputType.Choice,
            Value = "blue"
        };
        input.Options.Add("red", "Red");
        input.Options.Add("blue", "Blue");
        input.Options.Add("green", "Green");

        // Act
        var viewModel = new InputViewModel(input);

        // Assert
        Assert.Equal("blue", viewModel.Value);
    }

    [Fact]
    public void InputViewModel_ChoiceWithEmptyOptions_DoesNotSetValue()
    {
        // Arrange
        var input = new InteractionInput
        {
            Label = "Choose Color",
            InputType = InputType.Choice
        };

        // Act
        var viewModel = new InputViewModel(input);

        // Assert - proto strings default to empty string, not null
        Assert.True(string.IsNullOrEmpty(viewModel.Value));
    }

    [Fact]
    public void InputViewModel_NonChoiceInput_DoesNotSetValue()
    {
        // Arrange
        var input = new InteractionInput
        {
            Label = "Enter Text",
            InputType = InputType.Text
        };

        // Act
        var viewModel = new InputViewModel(input);

        // Assert - proto strings default to empty string, not null
        Assert.True(string.IsNullOrEmpty(viewModel.Value));
    }

    [Fact]
    public void SetInput_ChoiceWithoutPlaceholder_DefaultsToFirstOption()
    {
        // Arrange
        var initialInput = new InteractionInput
        {
            Label = "Enter Text",
            InputType = InputType.Text
        };
        var viewModel = new InputViewModel(initialInput);

        var choiceInput = new InteractionInput
        {
            Label = "Choose Color",
            InputType = InputType.Choice
        };
        choiceInput.Options.Add("red", "Red");
        choiceInput.Options.Add("blue", "Blue");

        // Act
        viewModel.SetInput(choiceInput);

        // Assert
        Assert.Equal("red", viewModel.Value);
    }

    [Fact]
    public void SetInput_ChoiceWithPlaceholder_DoesNotDefaultToFirstOption()
    {
        // Arrange
        var initialInput = new InteractionInput
        {
            Label = "Enter Text",
            InputType = InputType.Text
        };
        var viewModel = new InputViewModel(initialInput);

        var choiceInput = new InteractionInput
        {
            Label = "Choose Color",
            InputType = InputType.Choice,
            Placeholder = "Select a color"
        };
        choiceInput.Options.Add("red", "Red");
        choiceInput.Options.Add("blue", "Blue");

        // Act
        viewModel.SetInput(choiceInput);

        // Assert - proto strings default to empty string, not null
        Assert.True(string.IsNullOrEmpty(viewModel.Value));
    }

    [Fact]
    public void InputViewModel_ChoiceWithAllowCustomChoice_DoesNotDefaultToFirstOption()
    {
        // Arrange
        var input = new InteractionInput
        {
            Label = "Choose Color",
            InputType = InputType.Choice,
            AllowCustomChoice = true
        };
        input.Options.Add("red", "Red");
        input.Options.Add("blue", "Blue");
        input.Options.Add("green", "Green");

        // Act
        var viewModel = new InputViewModel(input);

        // Assert - When AllowCustomChoice is true, value should not default
        Assert.True(string.IsNullOrEmpty(viewModel.Value));
    }

    [Fact]
    public void ChoiceVersion_IncrementedWhenOptionsChange()
    {
        var input = new InteractionInput
        {
            Label = "Choose",
            InputType = InputType.Choice,
            Placeholder = "Select"
        };
        input.Options.Add("a", "A");

        var viewModel = new InputViewModel(input);
        var initialVersion = viewModel.ChoiceVersion;

        var updatedInput = new InteractionInput
        {
            Label = "Choose",
            InputType = InputType.Choice,
            Placeholder = "Select"
        };
        updatedInput.Options.Add("b", "B");
        updatedInput.Options.Add("c", "C");

        viewModel.SetInput(updatedInput);

        Assert.Equal(initialVersion + 1, viewModel.ChoiceVersion);
    }

    [Fact]
    public void ChoiceVersion_NotIncrementedForNonChoiceInput()
    {
        var input = new InteractionInput
        {
            Label = "Name",
            InputType = InputType.Text
        };

        var viewModel = new InputViewModel(input);
        var initialVersion = viewModel.ChoiceVersion;

        var updatedInput = new InteractionInput
        {
            Label = "Name",
            InputType = InputType.Text,
            Value = "test"
        };

        viewModel.SetInput(updatedInput);

        Assert.Equal(initialVersion, viewModel.ChoiceVersion);
    }

    [Fact]
    public void ChoiceVersion_NotIncrementedWhenOptionsUnchanged()
    {
        var input = new InteractionInput
        {
            Label = "Choose",
            InputType = InputType.Choice,
            Placeholder = "Select"
        };
        input.Options.Add("a", "A");
        input.Options.Add("b", "B");

        var viewModel = new InputViewModel(input);
        var initialVersion = viewModel.ChoiceVersion;

        var sameInput = new InteractionInput
        {
            Label = "Choose",
            InputType = InputType.Choice,
            Placeholder = "Select"
        };
        sameInput.Options.Add("a", "A");
        sameInput.Options.Add("b", "B");

        viewModel.SetInput(sameInput);

        Assert.Equal(initialVersion, viewModel.ChoiceVersion);
    }
}
