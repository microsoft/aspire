import { useEffect, useRef, useState } from "react";
import type { InteractionInfo, InteractionInputInfo } from "../api/types";
import { respondInteraction } from "../api/deck";
import { CloseIcon } from "../toolkit";

// Side pane (like the resource details drawer) that renders a blocking interaction
// from the AppHost: a command-input dialog with per-field validation, or a message
// box. Inputs marked updateStateOnChange re-validate with the server live; validation
// errors come back on the same interaction and render under each field. Notifications
// are non-blocking and handled separately by NotificationStack.
export function InteractionPane({ interaction }: { interaction: InteractionInfo }) {
  const [values, setValues] = useState<Record<string, string>>(() => initValues(interaction));
  const idRef = useRef(interaction.interactionId);

  // Reset local values only when a brand-new interaction arrives — not on the
  // validation updates that re-send the same interaction id with new errors.
  useEffect(() => {
    if (idRef.current !== interaction.interactionId) {
      idRef.current = interaction.interactionId;
      setValues(initValues(interaction));
    }
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

  return (
    <>
      <div className="drawer-overlay" onClick={close} />
      <aside className="drawer interaction-pane" role="dialog" aria-modal="true" aria-label={interaction.title}>
        <div className="drawer__header">
          <div>
            <div className="drawer__title">{interaction.title || "Input required"}</div>
            {interaction.message ? <div className="drawer__subtitle">{interaction.message}</div> : null}
          </div>
          {interaction.showDismiss !== false ? (
            <button className="icon-btn" onClick={close} aria-label="Dismiss">
              <CloseIcon size={16} />
            </button>
          ) : null}
        </div>

        <div className="drawer__body">
          {isInputs ? (
            <form
              className="interaction-form"
              onSubmit={(e) => {
                e.preventDefault();
                respondInteraction(interaction.interactionId, "submit", values);
              }}
            >
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

  return (
    <div className={`field ${hasErrors ? "field--error" : ""}`}>
      {input.inputType === "boolean" ? (
        <label className="field__check" htmlFor={fieldId}>
          <input
            id={fieldId}
            type="checkbox"
            checked={value === "true"}
            disabled={input.disabled}
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
            <select
              id={fieldId}
              className="select"
              value={value}
              disabled={input.disabled}
              onChange={(e) => onChange(e.target.value)}
            >
              {input.allowCustomChoice && !input.options.some(([v]) => v === value) ? (
                <option value={value}>{value || "—"}</option>
              ) : null}
              {input.options.map(([v, display]) => (
                <option key={v} value={v}>
                  {display}
                </option>
              ))}
            </select>
          ) : (
            <input
              id={fieldId}
              className="input"
              type={input.inputType === "secretText" ? "password" : input.inputType === "number" ? "number" : "text"}
              value={value}
              placeholder={input.placeholder}
              disabled={input.disabled}
              maxLength={input.maxLength > 0 ? input.maxLength : undefined}
              onChange={(e) => onChange(e.target.value)}
            />
          )}
        </>
      )}

      {input.description ? <div className="field__desc">{input.description}</div> : null}
      {input.validationErrors.map((err, i) => (
        <div key={i} className="field__error">
          {err}
        </div>
      ))}
    </div>
  );
}

function initValues(interaction: InteractionInfo): Record<string, string> {
  const values: Record<string, string> = {};
  for (const input of interaction.inputs) {
    values[input.name] = input.value;
  }
  return values;
}
