import { Button, ErrorIcon, Page, PageBody } from "../toolkit";

export function RouteErrorPage({ kind, onHome }: { kind: "notFound" | "error"; onHome: () => void }) {
  const notFound = kind === "notFound";
  return (
    <Page aria-label={notFound ? "Page not found" : "Dashboard error"}>
      <PageBody>
        <div className="route-error">
          {notFound ? <div className="route-error__code">404</div> : <ErrorIcon size={42} />}
          <h1>{notFound ? "Page not found" : "Something went wrong"}</h1>
          <p>{notFound ? "The requested dashboard page does not exist." : "The dashboard could not complete this request."}</p>
          <div className="route-error__actions">
            <Button variant="primary" onClick={onHome}>Go to resources</Button>
            {!notFound ? <Button onClick={() => window.location.reload()}>Reload dashboard</Button> : null}
          </div>
        </div>
      </PageBody>
    </Page>
  );
}
