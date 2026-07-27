import { useLayoutEffect, useRef } from "react";

export type CanvasConnector = (iframe: HTMLIFrameElement) => void | (() => void);

export function CanvasHost({
  src,
  title,
  connect,
  className,
}: {
  src: string;
  title: string;
  connect?: CanvasConnector;
  className?: string;
}) {
  const iframeRef = useRef<HTMLIFrameElement | null>(null);
  const classes = ["canvas-viewer", className].filter(Boolean).join(" ");

  // Attach the bridge during layout so a fast local canvas cannot send its ready
  // handshake before the host starts listening for messages.
  useLayoutEffect(() => {
    const iframe = iframeRef.current;
    if (!iframe || !connect) {
      return;
    }

    const dispose = connect(iframe);
    return typeof dispose === "function" ? dispose : undefined;
  }, [connect, src]);

  return (
    <div className={classes}>
      <iframe
        ref={iframeRef}
        className="canvas-viewer__frame"
        src={src}
        title={title}
        sandbox="allow-scripts"
        referrerPolicy="no-referrer"
      />
    </div>
  );
}
