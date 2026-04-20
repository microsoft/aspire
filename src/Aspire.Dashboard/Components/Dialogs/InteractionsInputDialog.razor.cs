// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class InteractionsInputDialog : IAsyncDisposable
{
    [Parameter]
    public InteractionsInputsDialogViewModel Content { get; set; } = default!;

    [CascadingParameter]
    public FluentDialog Dialog { get; set; } = default!;

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    private InteractionsInputsDialogViewModel? _content;
    private EditContext _editContext = default!;
    private ValidationMessageStore _validationMessages = default!;
    private List<InputViewModel> _inputDialogInputViewModels = default!;
    private Dictionary<InputViewModel, FluentComponentBase?> _elementRefs = default!;
    private MarkdownProcessor _markdownProcessor = default!;
    private IJSObjectReference? _jsModule;

    protected override void OnInitialized()
    {
        _editContext = new EditContext(Content);
        _validationMessages = new ValidationMessageStore(_editContext);

        _editContext.OnValidationRequested += (s, e) => ValidateModel();
        _editContext.OnFieldChanged += (s, e) => InputValueChanged(e.FieldIdentifier);

        _elementRefs = new();
        _markdownProcessor = InteractionMarkdownHelper.CreateProcessor(ControlsStringsLoc);
    }

    protected override void OnParametersSet()
    {
        if (_content != Content)
        {
            _content = Content;
            _inputDialogInputViewModels = Content.Inputs.Select(input => new InputViewModel(input)).ToList();

            // Initialize keys for @ref binding.
            // Do this in case Blazor tries to get the element from the dictionary.
            // If the input view model isn't in the dictionary then it will throw a KeyNotFoundException.
            _elementRefs.Clear();
            foreach (var inputVM in _inputDialogInputViewModels)
            {
                _elementRefs[inputVM] = null;
            }

            AddValidationErrorsFromModel();

            Content.OnInteractionUpdated = async () =>
            {
                AddValidationErrorsFromModel();

                await InvokeAsync(StateHasChanged);
            };
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Dialogs/InteractionsInputDialog.razor.js");

            // Focus the first input when the dialog loads.
            if (_inputDialogInputViewModels.Count > 0 && _elementRefs.TryGetValue(_inputDialogInputViewModels[0], out var firstInputElement))
            {
                if (firstInputElement is FluentInputBase<string> textInput)
                {
                    textInput.FocusAsync();
                }
                else if (firstInputElement is FluentInputBase<bool> boolInput)
                {
                    boolInput.FocusAsync();
                }
                else if (firstInputElement is FluentInputBase<int?> numberInput)
                {
                    numberInput.FocusAsync();
                }
                else if (firstInputElement is FluentInputBase<SelectViewModel<string>> selectInput)
                {
                    selectInput.FocusAsync();
                }
            }
        }
    }

    private void AddValidationErrorsFromModel()
    {
        for (var i = 0; i < Content.Inputs.Count; i++)
        {
            var inputModel = Content.Inputs[i];
            var inputViewModel = _inputDialogInputViewModels[i];

            inputViewModel.SetInput(inputModel);

            var field = GetFieldIdentifier(inputViewModel);
            foreach (var validationError in inputModel.ValidationErrors)
            {
                _validationMessages.Add(field, validationError);
            }
        }
    }

    private void ValidateModel()
    {
        _validationMessages.Clear();

        foreach (var inputModel in _inputDialogInputViewModels)
        {
            var field = GetFieldIdentifier(inputModel);
            if (IsMissingRequiredValue(inputModel))
            {
                _validationMessages.Add(field, $"{inputModel.Input.Label} is required.");
            }
        }

        _editContext.NotifyValidationStateChanged();
    }

    private void InputValueChanged(FieldIdentifier field)
    {
        _validationMessages.Clear(field);

        if (field.Model is InputViewModel inputModel)
        {
            if (IsMissingRequiredValue(inputModel))
            {
                _validationMessages.Add(field, $"{inputModel.Input.Label} is required.");
            }

            if (inputModel.Input.UpdateStateOnChange)
            {
                _ = Content.OnSubmitCallback(Content.Interaction, true);
            }
        }

        _editContext.NotifyValidationStateChanged();
    }

    private static FieldIdentifier GetFieldIdentifier(InputViewModel inputModel)
    {
        var fieldName = inputModel.Input.InputType switch
        {
            InputType.Boolean => nameof(inputModel.IsChecked),
            InputType.Number => nameof(inputModel.NumberValue),
            _ => nameof(inputModel.Value)
        };
        return new FieldIdentifier(inputModel, fieldName);
    }

    private static bool IsMissingRequiredValue(InputViewModel inputModel)
    {
        return inputModel.Input.Required &&
            inputModel.Input.InputType != InputType.Boolean &&
            string.IsNullOrWhiteSpace(inputModel.Value);
    }

    private async Task SubmitAsync()
    {
        // The workflow is:
        // 1. Validate the model that required fields are present.
        // 2. Run submit callback. Sends input values to the server.
        // 3. If validation on the server passes, a completion dialog is send back to the client which closes the dialog.
        // 4. If validation fails, the server sends back validation errors which are displayed in the dialog.
        if (_editContext.Validate())
        {
            await Content.OnSubmitCallback(Content.Interaction, false);
        }
    }

    private async Task CancelAsync()
    {
        await Dialog.CancelAsync();
    }

    // Default maximum number of bytes to read from an uploaded file into memory.
    private const long DefaultMaxUploadedFileBytes = 1024 * 1024; // 1 MB

    private async Task OnFileSelected(IEnumerable<FluentInputFileEventArgs> args, InputViewModel inputModel)
    {
        var file = args.FirstOrDefault();
        if (file?.Stream != null)
        {
            var maxBytes = inputModel.Input.MaxFileSize > 0 ? inputModel.Input.MaxFileSize : DefaultMaxUploadedFileBytes;
            // Wrap the file stream to prevent reading more than the configured max into memory.
            using var limitedStream = new LimitedStream(file.Stream, maxBytes);
            using var reader = new StreamReader(limitedStream);
            inputModel.Value = await reader.ReadToEndAsync();
            inputModel.FileDisplayName = file.Name;

            _editContext.NotifyFieldChanged(new FieldIdentifier(inputModel, nameof(inputModel.Value)));
            _editContext.NotifyFieldChanged(new FieldIdentifier(inputModel, nameof(inputModel.FileDisplayName)));
        }
        else
        {
            inputModel.Value = string.Empty;
            inputModel.FileDisplayName = string.Empty;

            _editContext.NotifyFieldChanged(new FieldIdentifier(inputModel, nameof(inputModel.Value)));
            _editContext.NotifyFieldChanged(new FieldIdentifier(inputModel, nameof(inputModel.FileDisplayName)));
        }
    }

    private sealed class LimitedStream(Stream innerStream, long maxBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => innerStream.Length;

        public override long Position
        {
            get => innerStream.Position;
            set => innerStream.Position = value;
        }

        public override void Flush() => innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_bytesRead >= maxBytes)
            {
                return 0;
            }

            var remaining = maxBytes - _bytesRead;
            if (count > remaining)
            {
                count = (int)remaining;
            }

            var read = innerStream.Read(buffer, offset, count);
            _bytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_bytesRead >= maxBytes)
            {
                return 0;
            }

            var remaining = maxBytes - _bytesRead;
            if (count > remaining)
            {
                count = (int)remaining;
            }

            var read = await innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            _bytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_bytesRead >= maxBytes)
            {
                return 0;
            }

            var remaining = maxBytes - _bytesRead;
            if (buffer.Length > remaining)
            {
                buffer = buffer[..(int)remaining];
            }

            var read = await innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _bytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
        public override void SetLength(long value) => innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task ToggleSecretTextVisibilityAsync(InputViewModel inputModel)
    {
        inputModel.IsSecretTextVisible = !inputModel.IsSecretTextVisible;

        if (_jsModule != null && _elementRefs.TryGetValue(inputModel, out var element) && element != null)
        {
            await _jsModule.InvokeVoidAsync("togglePasswordVisibility", element.Id);
        }
    }

    private static Icon GetSecretTextIcon(InputViewModel inputModel)
    {
        return inputModel.IsSecretTextVisible
            ? new Icons.Regular.Size16.EyeOff() 
            : new Icons.Regular.Size16.Eye();
    }

    public async ValueTask DisposeAsync()
    {
        await JSInteropHelpers.SafeDisposeAsync(_jsModule);
    }
}
