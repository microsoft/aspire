import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
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
  const confirm = (): void => {
    request?.onConfirm();
    onClose();
  };

  return (
    <AlertDialog open={request !== null} onOpenChange={(open) => {
      if (!open) onClose();
    }}>
      {request ? (
        <AlertDialogContent className="modal">
          <AlertDialogTitle className="modal__title">{request.title}</AlertDialogTitle>
          <AlertDialogDescription className="modal__text">{request.message}</AlertDialogDescription>
          <div className="modal__actions">
            <AlertDialogCancel asChild>
              <Button>Cancel</Button>
            </AlertDialogCancel>
            <AlertDialogAction asChild>
              <Button variant={request.danger ? "danger" : "primary"} onClick={confirm}>
                {request.confirmLabel ?? "Confirm"}
              </Button>
            </AlertDialogAction>
          </div>
        </AlertDialogContent>
      ) : null}
    </AlertDialog>
  );
}
