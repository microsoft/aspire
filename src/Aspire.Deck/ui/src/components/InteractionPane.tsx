import { useEffect, useRef, useState } from "react";
import type { InteractionInfo, InteractionInputInfo } from "../api/types";
import { openExternal, respondInteraction, uploadInteractionFile } from "../api/deck";
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
  const updateRef = useRef<Promise<void>>(Promise.resolve());
  const valuesRef = useRef(values);
  const fileUploadRef = useRef<Promise<void>>(Promise.resolve());
  const fileUploadAbortRef = useRef<AbortController | null>(null);
  const fileUploadErrorsRef = useRef<Record<string, boolean>>({});

  // Reset local values only when a brand-new interaction arrives — not on the
  // validation updates that re-send the same interaction id with new errors.
  useEffect(() => {
    if (idRef.current !== interaction.interactionId) {
      idRef.current = interaction.interactionId;
      const next = initValues(interaction);
      valuesRef.current = next;
      setValues(next);
    } else {
      const previousInputs = new Map(interactionRef.current.inputs.map((input) => [input.name, input]));
      setValues((current) => {
        const next: Record<string, string> = {};
        for (const input of interaction.inputs) {
          const previousInput = previousInputs.get(input.name);
          // A changed value from an update response is authoritative. If only options,
          // validation, or disabled state changed, retain text the user is still editing.
          next[input.name] = !previousInput || input.value !== previousInput.value
            ? initialInputValue(input)
            : current[input.name] ?? initialInputValue(input);
        }
        valuesRef.current = next;
        return next;
      });
    }
    interactionRef.current = interaction;
  }, [interaction]);

  useEffect(() => {
    const abortController = new AbortController();
    fileUploadAbortRef.current = abortController;
    fileUploadRef.current = Promise.resolve();
    updateRef.current = Promise.resolve();
    fileUploadErrorsRef.current = {};
    return () => abortController.abort();
  }, [interaction.interactionId]);

  const close = () => {
    fileUploadAbortRef.current?.abort();
    void fileUploadRef.current
      .catch(() => undefined)
      .then(() => updateRef.current.catch(() => undefined))
      .then(() => respondInteraction(interaction.interactionId, "cancel", {}));
  };

  function setValue(name: string, value: string, updateOnChange: boolean) {
    const next = { ...valuesRef.current, [name]: value };
    valuesRef.current = next;
    setValues(next);
    if (updateOnChange) {
      // Dynamic input updates and the terminal submit must reach the AppHost in order.
      // Otherwise a quick submit can race validation while dependent inputs are still loading.
      updateRef.current = updateRef.current
        .catch(() => undefined)
        .then(() => respondInteraction(interaction.interactionId, "update", next));
    }
  }

  function queueFileUpload(input: InteractionInputInfo, files: File[]): Promise<FileSelectionResult> {
    const interactionId = interaction.interactionId;
    const signal = fileUploadAbortRef.current?.signal;
    const queued = fileUploadRef.current
      .catch(() => undefined)
      .then(async () => {
        const successful: UploadedFile[] = [];
        const errors: string[] = [];
        fileUploadErrorsRef.current = { ...fileUploadErrorsRef.current, [input.name]: false };
        const selectedFiles = input.allowMultipleFiles ? files.slice(0, 100) : files.slice(0, 1);
        if (files.length > selectedFiles.length) {
          errors.push(input.allowMultipleFiles
            ? "No more than 100 files can be selected at once."
            : "Only one file can be selected.");
        }

        for (const file of selectedFiles) {
          if (input.maxFileSize > 0 && file.size > input.maxFileSize) {
            errors.push(`${file.name}: Exceeds the maximum size of ${formatFileSize(input.maxFileSize)}.`);
            continue;
          }

          try {
            const uploaded = await uploadInteractionFile(interactionId, input.name, file, signal);
            successful.push({ Id: uploaded.fileId, Name: uploaded.fileName });
          } catch (error) {
            if (signal?.aborted) {
              return { successful: [], errors: [] };
            }
            errors.push(`${file.name}: ${error instanceof Error ? error.message : "Upload failed."}`);
          }
        }

        // An AppHost update can replace the dialog while an HTTP upload is in flight. Never let
        // that stale completion mutate the next interaction, even if it reuses the same input name.
        if (signal?.aborted || idRef.current !== interactionId) {
          return { successful: [], errors: [] };
        }

        fileUploadErrorsRef.current = {
          ...fileUploadErrorsRef.current,
          [input.name]: errors.length > 0,
        };
        setValue(
          input.name,
          successful.length > 0 ? JSON.stringify(successful) : "",
          input.updateStateOnChange,
        );
        return { successful, errors };
      });

    // A later submit awaits every selection in order. Individual file failures are returned as
    // validation messages, so this chain remains usable for a subsequent retry.
    fileUploadRef.current = queued.then(() => undefined);
    return queued;
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
              onSubmit={async (e) => {
                e.preventDefault();
                await fileUploadRef.current;
                if (Object.values(fileUploadErrorsRef.current).some(Boolean)) {
                  return;
                }
                await updateRef.current.catch(() => undefined);
                await respondInteraction(interaction.interactionId, "submit", valuesRef.current);
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
                  key={`${interaction.interactionId}:${input.name}`}
                  input={input}
                  value={values[input.name] ?? input.value}
                  onChange={(v) => setValue(input.name, v, input.updateStateOnChange)}
                  onFilesSelected={(files) => queueFileUpload(input, files)}
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
                    onClick={() => void respondInteraction(interaction.interactionId, "secondary", {})}
                  >
                    {interaction.secondaryButtonText || "No"}
                  </button>
                ) : null}
                <button
                  type="button"
                  className="btn btn--primary"
                  onClick={() => void respondInteraction(interaction.interactionId, "primary", {})}
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
  onFilesSelected,
}: {
  input: InteractionInputInfo;
  value: string;
  onChange: (value: string) => void;
  onFilesSelected: (files: File[]) => Promise<FileSelectionResult>;
}) {
  const [uploading, setUploading] = useState(false);
  const [uploadedFiles, setUploadedFiles] = useState<UploadedFile[]>([]);
  const [uploadErrors, setUploadErrors] = useState<string[]>([]);
  const errors = [...input.validationErrors, ...uploadErrors];
  const hasErrors = errors.length > 0;
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
            {input.required ? <span className="field__required" aria-hidden="true"> *</span> : null}
          </label>
          {input.inputType === "file" ? (
            <>
              <input
                id={fieldId}
                className="input interaction-file-input"
                type="file"
                accept={input.fileFilter || undefined}
                multiple={input.allowMultipleFiles}
                required={input.required}
                disabled={input.disabled || uploading}
                aria-invalid={hasErrors || undefined}
                aria-describedby={describedBy}
                onChange={async (event) => {
                  const files = Array.from(event.target.files ?? []);
                  setUploading(true);
                  setUploadedFiles([]);
                  setUploadErrors([]);
                  try {
                    const result = await onFilesSelected(files);
                    setUploadedFiles(result.successful);
                    setUploadErrors(result.errors);
                  } finally {
                    setUploading(false);
                  }
                }}
              />
              {uploading ? <div className="field__status" role="status">Uploading…</div> : null}
              {uploadedFiles.length > 0 ? (
                <ul className="interaction-file-list" aria-label={`Uploaded files for ${input.label}`}>
                  {uploadedFiles.map((file) => <li key={file.Id}>{file.Name}</li>)}
                </ul>
              ) : null}
            </>
          ) : input.inputType === "choice" ? (
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
          {errors.map((err, i) => <div key={i} className="field__error">{err}</div>)}
        </div>
      ) : null}
    </div>
  );
}

interface UploadedFile {
  Id: string;
  Name: string;
}

interface FileSelectionResult {
  successful: UploadedFile[];
  errors: string[];
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${Math.round(bytes / (1024 * 1024))} MB`;
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
    values[input.name] = initialInputValue(input);
  }
  return values;
}

function initialInputValue(input: InteractionInputInfo): string {
  // Optional unchecked booleans arrive without a protobuf value. Command argument
  // validation expects the wire-format boolean "false", not an empty string.
  return input.inputType === "boolean" && input.value === "" ? "false" : input.value;
}
