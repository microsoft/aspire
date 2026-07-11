import { useEffect, useId, useState } from "react";
import { Button, IconButton } from "./Button";
import { CloseIcon, NamedIcon } from "./Icons";

export interface TextViewerRequest {
  title: string;
  value: string;
  format?: "text" | "json";
}

export function TextViewerDialog({
  request,
  onClose,
}: {
  request: TextViewerRequest | null;
  onClose: () => void;
}) {
  const titleId = useId();
  const [copyStatus, setCopyStatus] = useState("");

  useEffect(() => {
    setCopyStatus("");
  }, [request?.value]);

  useEffect(() => {
    if (request === null) {
      return;
    }

    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === "Escape") {
        onClose();
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose, request]);

  if (request === null) {
    return null;
  }

  const copy = async (): Promise<void> => {
    try {
      await navigator.clipboard.writeText(request.value);
      setCopyStatus("Copied");
    } catch {
      setCopyStatus("Copy failed");
    }
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div
        className="modal text-viewer"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="text-viewer__header">
          <div className="modal__title" id={titleId}>{request.title}</div>
          <IconButton label="Close visualizer" icon={<CloseIcon size={16} />} onClick={onClose} />
        </div>
        <pre className="text-viewer__content" data-format={request.format ?? "text"}>
          <code>{request.value}</code>
        </pre>
        <div className="modal__actions">
          <span className="text-viewer__status" role="status" aria-live="polite">{copyStatus}</span>
          <Button onClick={() => void copy()}>
            <NamedIcon name="Copy" size={16} />
            Copy
          </Button>
          <Button variant="primary" onClick={onClose}>Close</Button>
        </div>
      </div>
    </div>
  );
}
