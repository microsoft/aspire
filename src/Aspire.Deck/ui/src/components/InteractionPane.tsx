import { useEffect, useRef, useState } from "react";
import type { InteractionInfo, InteractionInputInfo } from "../api/types";
import { openExternal, respondInteraction } from "../api/deck";
import { CloseIcon, ComboBox, MarkdownContent, SecretInput } from "../toolkit";

// Side pane (like the resource details drawer) that renders a blocking interaction
// from the AppHost: a command-input dialog with per-field validation, or a message
// box. Inputs marked updateStateOnChange re-validate with the server live; validation
// errors come back on the same interaction and render under each field. Notifications
// are non-blocking and handled separately by NotificationStack.
export function InteractionPane({ interaction }: { interaction: InteractionInfo }) {
  const [values, setValues] = useState<Record<string, string>>(() => initValues(interaction));
  const idRef = useRef(interaction.interactionId);
  const interactionRef = useRef(interaction);

  // Reset local values only when a brand-new interaction arrives — not on the
  // validation updates that re-send the same interaction id with new errors.
  useEffect(() => {
    if (idRef.current !== interaction.interactionId) {
      idRef.current = interaction.interactionId;
      setValues(initValues(interaction));
    } else {
      const previousInputs = new Map(interactionRef.current.inputs.map((input) => [input.name, input]));
      setValues((current) => {
        const next: Record<string, string> = {};
        for (const input of interaction.inputs) {
          const previousInput = previousInputs.get(input.name);
          // A changed value from an update response is authoritative. If only options,
          // validation, or disabled state changed, retain text the user is still editing.
          next[input.name] = !previousInput || input.value !== previousInput.value
            ? input.value
            : current[input.name] ?? input.value;
        }
        return next;
      });
    }
    interactionRef.current = interaction;
  }, [interaction]);

  const close = () => respondInteraction(interaction.interactionId, "cancel", {});

  function setValue(name: string, value: string, updateOnChange: boolean) {
    const next = { ...values, [name]: value };
    setValues(next);
    if (updateOnChange) {
      respondInteraction(interaction.interactionId, "update", next);
    }
  }

  const isInputs = interaction.kind === "inputsDialog";
  const validationErrors = interaction.inputs.flatMap((input) =>
    input.validationErrors.map((error) => ({ name: input.label || input.name, error })),
  );

  return (
    <>
      <div className="drawer-overlay" onClick={close} />
      <aside className={`drawer interaction-pane interaction-pane--${toIntent(interaction.intent)}`} data-intent={interaction.intent} role="dialog" aria-modal="true" aria-label={interaction.title}>
        <div className="drawer__header">
          <div>
            <div className="drawer__title">{interaction.title || "Input required"}</div>
          </div>
          {interaction.showDismiss !== false ? (
            <button className="icon-btn" onClick={close} aria-label="Dismiss">
              <CloseIcon size={16} />
            </button>
          ) : null}
        </div>

        <div className="drawer__body">
          {interaction.message ? (
            <MarkdownContent
              markdown={interaction.message}
              enabled={interaction.enableMessageMarkdown}
              className="interaction-message"
              onLinkClick={(url) => void openExternal(url)}
            />
          ) : null}
          {isInputs ? (
            <form
              className="interaction-form"
              onSubmit={(e) => {
                e.preventDefault();
                respondInteraction(interaction.interactionId, "submit", values);
              }}
            >
              {validationErrors.length > 0 ? (
                <div className="interaction-form__validation" role="alert" aria-live="assertive">
                  <div>Correct the following errors:</div>
                  <ul>
                    {validationErrors.map(({ name, error }, index) => (
                      <li key={`${name}-${index}`}>{name}: {error}</li>
                    ))}
                  </ul>
                </div>
              ) : null}
              {interaction.inputs.map((input) => (
                <InputField
                  key={input.name}
                  input={input}
                  value={values[input.name] ?? input.value}
                  onChange={(v) => setValue(input.name, v, input.updateStateOnChange)}
                />
              ))}

              <div className="interaction-form__actions">
                {interaction.showSecondaryButton ? (
                  <button type="button" className="btn" onClick={close}>
                    {interaction.secondaryButtonText || "Cancel"}
                  </button>
                ) : null}
                <button type="submit" className="btn btn--primary">
                  {interaction.primaryButtonText || "Submit"}
                </button>
              </div>
            </form>
          ) : (
            <div className="interaction-form">
              <div className="interaction-form__actions">
                {interaction.showSecondaryButton ? (
                  <button
                    type="button"
                    className="btn"
                    onClick={() => respondInteraction(interaction.interactionId, "secondary", {})}
                  >
                    {interaction.secondaryButtonText || "No"}
                  </button>
                ) : null}
                <button
                  type="button"
                  className="btn btn--primary"
                  onClick={() => respondInteraction(interaction.interactionId, "primary", {})}
                >
                  {interaction.primaryButtonText || "OK"}
                </button>
              </div>
            </div>
          )}
        </div>
      </aside>
    </>
  );
}

function InputField({
  input,
  value,
  onChange,
}: {
  input: InteractionInputInfo;
  value: string;
  onChange: (value: string) => void;
}) {
  const hasErrors = input.validationErrors.length > 0;
  const fieldId = `int-${input.name}`;
  const descriptionId = input.description ? `${fieldId}-description` : undefined;
  const errorId = hasErrors ? `${fieldId}-errors` : undefined;
  const describedBy = [descriptionId, errorId].filter(Boolean).join(" ") || undefined;

  return (
    <div className={`field ${hasErrors ? "field--error" : ""}`}>
      {input.inputType === "boolean" ? (
        <label className="field__check" htmlFor={fieldId}>
          <input
            id={fieldId}
            type="checkbox"
            checked={value === "true"}
            disabled={input.disabled}
            aria-invalid={hasErrors || undefined}
            aria-describedby={describedBy}
            onChange={(e) => onChange(e.target.checked ? "true" : "false")}
          />
          <span>{input.label}</span>
        </label>
      ) : (
        <>
          <label className="field__label" htmlFor={fieldId}>
            {input.label}
            {input.required ? <span className="field__required"> *</span> : null}
          </label>
          {input.inputType === "choice" ? (
            <ComboBox
              id={fieldId}
              value={value}
              disabled={input.disabled}
              allowCustomValue={input.allowCustomChoice}
              placeholder={input.placeholder}
              ariaInvalid={hasErrors}
              ariaDescribedBy={describedBy}
              options={input.options.map(([optionValue, label]) => ({ value: optionValue, label }))}
              onValueChange={(nextValue) => onChange(nextValue)}
            />
          ) : input.inputType === "secretText" ? (
            <SecretInput
              id={fieldId}
              value={value}
              placeholder={input.placeholder}
              disabled={input.disabled}
              maxLength={input.maxLength > 0 ? input.maxLength : undefined}
              aria-invalid={hasErrors || undefined}
              aria-describedby={describedBy}
              onChange={(event) => onChange(event.target.value)}
            />
          ) : (
            <input
              id={fieldId}
              className="input"
              type={input.inputType === "number" ? "number" : "text"}
              value={value}
              placeholder={input.placeholder}
              disabled={input.disabled}
              maxLength={input.maxLength > 0 ? input.maxLength : undefined}
              aria-invalid={hasErrors || undefined}
              aria-describedby={describedBy}
              onChange={(e) => onChange(e.target.value)}
            />
          )}
        </>
      )}

      {input.description ? (
        <MarkdownContent
          id={descriptionId}
          markdown={input.description}
          enabled={input.enableDescriptionMarkdown}
          className="field__desc"
          onLinkClick={(url) => void openExternal(url)}
        />
      ) : null}
      {hasErrors ? (
        <div id={errorId} className="field__errors">
          {input.validationErrors.map((err, i) => <div key={i} className="field__error">{err}</div>)}
        </div>
      ) : null}
    </div>
  );
}

function toIntent(intent: InteractionInfo["intent"]): "error" | "warning" | "success" | "info" {
  switch (intent) {
    case "error": return "error";
    case "warning": return "warning";
    case "success": return "success";
    default: return "info";
  }
}

function initValues(interaction: InteractionInfo): Record<string, string> {
  const values: Record<string, string> = {};
  for (const input of interaction.inputs) {
    values[input.name] = input.value;
  }
  return values;
}
