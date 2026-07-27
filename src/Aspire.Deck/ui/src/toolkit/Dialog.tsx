import type { ReactNode } from "react";
import {
  Dialog as ShadcnDialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

export function Dialog({
  open,
  title,
  children,
  actions,
  onClose,
  className,
}: {
  open: boolean;
  title: ReactNode;
  children: ReactNode;
  actions?: ReactNode;
  onClose: () => void;
  className?: string;
}) {
  return (
    <ShadcnDialog open={open} onOpenChange={(nextOpen) => { if (!nextOpen) onClose(); }}>
      <DialogContent className={className} showCloseButton={!actions}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
        </DialogHeader>
        <div className="deck-dialog__content">{children}</div>
        {actions ? <DialogFooter>{actions}</DialogFooter> : null}
      </DialogContent>
    </ShadcnDialog>
  );
}
