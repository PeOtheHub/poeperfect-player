export function LoadingOverlay({ message }: { message: string }) {
  return (
    <div className="loading-overlay" role="status" aria-live="polite">
      <div className="loading-panel">
        <LoadingSpinner />
        <span>{message}</span>
      </div>
    </div>
  );
}

export function LoadingInline({ message }: { message: string }) {
  return (
    <div className="loading-inline" role="status" aria-live="polite">
      <LoadingSpinner />
      <span>{message}</span>
    </div>
  );
}

export function LoadingSpinner({ size = "default" }: { size?: "default" | "small" }) {
  return (
    <span className={`loading-spinner ${size === "small" ? "small" : ""}`} aria-hidden="true">
      <span />
      <span />
      <span />
    </span>
  );
}
