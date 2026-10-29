// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Coordinates the AppHost's unresolved parameters interactions with the inline parameter forms
/// (<c>MissingParametersPanel</c>) of the current circuit.
/// </summary>
/// <remarks>
/// The AppHost first shows a notification, and prompts for the values with an inputs interaction once the
/// notification is accepted. A form accepts the notification when it can show every parameter the notification is
/// about, and the inputs interaction is shown in a form only while a mounted form shows all of its inputs. Otherwise
/// the interaction is shown as the usual dialog, so the AppHost is never left waiting on a prompt nobody can see.
/// All members are called from the circuit's renderer dispatcher.
/// </remarks>
public sealed class ParameterInteractionState
{
    // The inputs interaction adds this input to save the values. It isn't a parameter.
    internal const string SaveToUserSecretsInputName = "SaveToUserSecrets";

    private readonly List<PanelRegistration> _panels = [];
    private PendingNotification? _notification;
    private DialogInteraction? _dialogInteraction;

    /// <summary>
    /// Gets the inputs interaction shown inline, if any.
    /// </summary>
    public InteractionsInputsDialogViewModel? Current { get; private set; }

    /// <summary>
    /// Raised when <see cref="Current"/> or its inputs change.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when no mounted form can show <see cref="Current"/> anymore, for example after navigating away from it.
    /// <see cref="Current"/> is cleared first, and the handler shows the interaction another way.
    /// </summary>
    public event Action<InteractionsInputsDialogViewModel>? Released;

    /// <summary>
    /// Registers a mounted form. Dispose the registration when the form is removed.
    /// </summary>
    public PanelRegistration RegisterPanel()
    {
        var panel = new PanelRegistration(this);
        _panels.Add(panel);
        return panel;
    }

    /// <summary>
    /// Records the unresolved parameters notification, and how to accept it.
    /// </summary>
    public void SetNotification(int interactionId, IReadOnlyCollection<string> parameterNames, Func<Task> accept)
    {
        _notification = new PendingNotification(interactionId, parameterNames, accept);
        Changed?.Invoke();
    }

    /// <summary>
    /// Accepts the unresolved parameters notification, so the AppHost prompts for the values, when the given form can
    /// show all of the notification's parameters. A form that shows only some of them doesn't, because the prompt
    /// would ask for parameters the form can't show.
    /// </summary>
    public async Task BeginInteractionAsync(PanelRegistration panel)
    {
        if (Current is not null || _notification is not { AcceptRequested: false } notification || !panel.Shows(notification.ParameterNames))
        {
            return;
        }

        notification.AcceptRequested = true;
        await notification.Accept().ConfigureAwait(false);
    }

    /// <summary>
    /// Shows the inputs interaction inline when a mounted form shows all of its inputs.
    /// </summary>
    /// <returns><c>true</c> when the interaction is shown inline, otherwise <c>false</c>.</returns>
    public bool TrySetCurrent(InteractionsInputsDialogViewModel current)
    {
        // Only one prompt is shown inline. Another one, which the AppHost doesn't send for parameters, gets a dialog.
        if (Current is not null && Current.Interaction.InteractionId != current.Interaction.InteractionId)
        {
            return false;
        }

        var inputNames = GetParameterInputNames(current);
        if (inputNames.Count == 0 || !_panels.Any(panel => panel.Shows(inputNames)))
        {
            return false;
        }

        Current = current;
        current.OnInteractionUpdated = () =>
        {
            Changed?.Invoke();
            return Task.CompletedTask;
        };
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Records an inputs interaction that's shown in a dialog because no mounted form could show it. A form that can
    /// show it takes it from the dialog when it's mounted: after reloading the page on the form, the AppHost's prompt
    /// is replayed before the form is rendered, and the form is where the user went to enter the values.
    /// </summary>
    /// <param name="interaction">The interaction shown in the dialog.</param>
    /// <param name="closeDialog">Closes the dialog without answering the interaction.</param>
    public void SetDialogInteraction(InteractionsInputsDialogViewModel interaction, Func<Task> closeDialog)
    {
        _dialogInteraction = new DialogInteraction(interaction, closeDialog);
        TryTakeDialogInteraction();
    }

    /// <summary>
    /// Forgets the interaction shown in a dialog after the dialog closed.
    /// </summary>
    public void ClearDialogInteraction(int interactionId)
    {
        if (_dialogInteraction?.Interaction.Interaction.InteractionId == interactionId)
        {
            _dialogInteraction = null;
        }
    }

    /// <summary>
    /// Forgets the given interaction, which the AppHost completed.
    /// </summary>
    public void Clear(int interactionId)
    {
        ClearDialogInteraction(interactionId);

        var changed = false;
        if (_notification?.InteractionId == interactionId)
        {
            // Accepting or dismissing the notification completes it. A form mounted later must not accept it again.
            _notification = null;
            changed = true;
        }

        if (Current?.Interaction.InteractionId == interactionId)
        {
            Current.OnInteractionUpdated = null;
            Current = null;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static List<string> GetParameterInputNames(InteractionsInputsDialogViewModel interaction) =>
        interaction.Inputs
            .Select(input => input.Name)
            .Where(name => !string.Equals(name, SaveToUserSecretsInputName, StringComparisons.InteractionInputName))
            .ToList();

    private void OnPanelsChanged()
    {
        if (Current is not { } current)
        {
            TryTakeDialogInteraction();
            return;
        }

        if (_panels.Any(panel => panel.Shows(GetParameterInputNames(current))))
        {
            return;
        }

        current.OnInteractionUpdated = null;
        Current = null;
        Changed?.Invoke();
        Released?.Invoke(current);
    }

    private void TryTakeDialogInteraction()
    {
        if (Current is not null || _dialogInteraction is not { } dialogInteraction || !TrySetCurrent(dialogInteraction.Interaction))
        {
            return;
        }

        _dialogInteraction = null;
        _ = dialogInteraction.CloseDialog();
    }

    /// <summary>
    /// A mounted form, and the parameters it shows.
    /// </summary>
    public sealed class PanelRegistration : IDisposable
    {
        private readonly ParameterInteractionState _owner;
        private HashSet<string> _parameterNames = new(StringComparers.ResourceName);
        private bool _disposed;

        internal PanelRegistration(ParameterInteractionState owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Sets the names of the parameters the form has an editor for.
        /// </summary>
        public void SetParameterNames(IEnumerable<string> parameterNames)
        {
            var names = parameterNames.ToHashSet(StringComparers.ResourceName);
            if (_disposed || names.SetEquals(_parameterNames))
            {
                return;
            }

            _parameterNames = names;
            _owner.OnPanelsChanged();
        }

        internal bool Shows(IEnumerable<string> parameterNames) => parameterNames.All(_parameterNames.Contains);

        /// <summary>
        /// Gets a value indicating whether the form shows every input of the given interaction.
        /// </summary>
        public bool Shows(InteractionsInputsDialogViewModel interaction) => Shows(GetParameterInputNames(interaction));

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner._panels.Remove(this);
            _owner.OnPanelsChanged();
        }
    }

    private sealed record DialogInteraction(InteractionsInputsDialogViewModel Interaction, Func<Task> CloseDialog);

    private sealed class PendingNotification(int interactionId, IReadOnlyCollection<string> parameterNames, Func<Task> accept)
    {
        public int InteractionId { get; } = interactionId;
        public IReadOnlyCollection<string> ParameterNames { get; } = parameterNames;
        public Func<Task> Accept { get; } = accept;
        public bool AcceptRequested { get; set; }
    }
}
