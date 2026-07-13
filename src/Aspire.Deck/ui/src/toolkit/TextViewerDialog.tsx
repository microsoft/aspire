import { useEffect, useId, useState } from "react";
import { Button, IconButton } from "./Button";
import { CloseIcon, NamedIcon } from "./Icons";
import { MarkdownContent } from "./MarkdownContent";

export interface TextViewerRequest {
  title: string;
  value: string;
  format?: "text" | "json" | "markdown";
  downloadFileName?: string;
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

  const download = (): void => {
    const extension = request.format === "json" ? "json" : request.format === "markdown" ? "md" : "txt";
    const href = URL.createObjectURL(new Blob([request.value], { type: "text/plain;charset=utf-8" }));
    const anchor = document.createElement("a");
    anchor.href = href;
    anchor.download = request.downloadFileName ?? `command-result.${extension}`;
    anchor.click();
    URL.revokeObjectURL(href);
  };

  const format = request.format ?? "text";
  let displayValue = request.value;
  if (format === "json") {
    try {
      displayValue = JSON.stringify(JSON.parse(request.value), null, 2);
    } catch {
      // Preserve malformed JSON exactly so command output is never discarded.
    }
  }

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
        {format === "markdown" ? (
          <div className="text-viewer__content text-viewer__content--markdown" data-format={format}>
            <MarkdownContent markdown={request.value} />
          </div>
        ) : (
          <pre className="text-viewer__content" data-format={format}>
            <code>{displayValue}</code>
          </pre>
        )}
        <div className="modal__actions">
          <span className="text-viewer__status" role="status" aria-live="polite">{copyStatus}</span>
          <Button onClick={download}>
            <NamedIcon name="ArrowDownload" size={16} />
            Download
          </Button>
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
