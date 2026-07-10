import { useEffect } from "react";
import { Button } from "./Button";

export interface ConfirmRequest {
  title: string;
  message: string;
  confirmLabel?: string;
  danger?: boolean;
  onConfirm: () => void;
}

export function ConfirmDialog({
  request,
  onClose,
}: {
  request: ConfirmRequest | null;
  onClose: () => void;
}) {
  useEffect(() => {
    if (!request) {
      return;
    }

    const onKey = (event: KeyboardEvent): void => {
      if (event.key === "Escape") {
        onClose();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [request, onClose]);

  if (!request) {
    return null;
  }

  const confirm = (): void => {
    request.onConfirm();
    onClose();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal" onClick={(event) => event.stopPropagation()} role="dialog" aria-modal="true">
        <div className="modal__title">{request.title}</div>
        <div className="modal__text">{request.message}</div>
        <div className="modal__actions">
          <Button onClick={onClose}>Cancel</Button>
          <Button variant={request.danger ? "danger" : "primary"} onClick={confirm} autoFocus>
            {request.confirmLabel ?? "Confirm"}
          </Button>
        </div>
      </div>
    </div>
  );
}
