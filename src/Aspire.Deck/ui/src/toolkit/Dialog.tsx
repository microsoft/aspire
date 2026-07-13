import type { ReactNode } from "react";
import {
  Dialog as FluentDialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
} from "@fluentui/react-components";

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
    <FluentDialog open={open} modalType="modal" onOpenChange={(_event, data) => { if (!data.open) onClose(); }}>
      <DialogSurface className={className}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>{children}</DialogContent>
          {actions ? <DialogActions>{actions}</DialogActions> : null}
        </DialogBody>
      </DialogSurface>
    </FluentDialog>
  );
}
