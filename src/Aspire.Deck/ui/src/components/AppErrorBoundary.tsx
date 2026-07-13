import { Component, type ErrorInfo, type ReactNode } from "react";
import { RouteErrorPage } from "../pages/RouteErrorPage";

interface AppErrorBoundaryState {
  error: Error | null;
}

export class AppErrorBoundary extends Component<{ children: ReactNode }, AppErrorBoundaryState> {
  state: AppErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): AppErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error("The dashboard failed to render.", error, info.componentStack);
  }

  render(): ReactNode {
    if (this.state.error) {
      return (
        <main className="app-error-boundary">
          <RouteErrorPage kind="error" onHome={() => window.location.assign("/")} />
        </main>
      );
    }

    return this.props.children;
  }
}
