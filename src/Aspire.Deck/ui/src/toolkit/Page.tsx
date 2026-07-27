import { forwardRef, type HTMLAttributes } from "react";

function classes(base: string, className?: string): string {
  return [base, className].filter(Boolean).join(" ");
}

export function Page({ className, ...props }: HTMLAttributes<HTMLElement>) {
  return <section {...props} className={classes("page", className)} />;
}

export function PageHeader({ className, ...props }: HTMLAttributes<HTMLElement>) {
  return <header {...props} className={classes("page__header", className)} />;
}

export function PageHeading({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div {...props} className={classes("page__heading", className)} />;
}

export function PageTitle({
  as: Heading = "h1",
  className,
  ...props
}: HTMLAttributes<HTMLHeadingElement> & { as?: "h1" | "h2" | "h3" }) {
  return <Heading {...props} className={classes("page__title", className)} />;
}

export function PageSubtitle({ className, ...props }: HTMLAttributes<HTMLParagraphElement>) {
  return <p {...props} className={classes("page__subtitle", className)} />;
}

export function PageActions({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div {...props} className={classes("page__actions", className)} />;
}

export function PageToolbar({
  ariaLabel,
  className,
  ...props
}: HTMLAttributes<HTMLDivElement> & { ariaLabel: string }) {
  return (
    <div
      {...props}
      role="toolbar"
      aria-label={ariaLabel}
      className={classes("page__toolbar", className)}
    />
  );
}

export const PageBody = forwardRef<HTMLDivElement, HTMLAttributes<HTMLDivElement>>(function PageBody({ className, ...props }, ref) {
  return <div ref={ref} {...props} className={classes("page__body", className)} />;
});
