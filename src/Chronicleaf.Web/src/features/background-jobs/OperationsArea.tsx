import { OperationsPanel } from './OperationsPanel';
import { getOperation, listOperations } from './api';
import { useOperation, useOperations } from './useOperations';

/**
 * The operations area, rendered only for a signed-in owner, like every other product area.
 *
 * Paths:
 * - `/operations` the panel over the scope's whole list
 * - `/operations/{id}` one operation on its own
 *
 * Both read the same private routes the session gate already protects. There is no path here that
 * creates work, and none that guesses at a status: the panel is a reader, and the only action it
 * offers is the stop the server already decided is allowed.
 */
export function OperationsArea({
  path,
  onNavigate,
}: {
  readonly path: string;
  readonly onNavigate: (to: string) => void;
}) {
  const segments = path.split('/').filter((segment) => segment.length > 0);

  if (segments[0] !== 'operations') {
    return (
      <section className="view" data-testid="route-not-found">
        <h1 className="view__heading">Nothing here</h1>
        <p className="view__state">
          This address is not part of Chronicleaf. Nothing was changed.
        </p>
        <button type="button" className="action" onClick={() => onNavigate('/')} data-testid="route-home">
          Go to the dashboard
        </button>
      </section>
    );
  }

  if (segments.length === 1) {
    return <OperationsList onNavigate={onNavigate} />;
  }

  if (segments.length === 2) {
    return <OperationDetail operationId={segments[1]} onNavigate={onNavigate} />;
  }

  return (
    <section className="view" data-testid="route-not-found">
      <h1 className="view__heading">Nothing here</h1>
      <p className="view__state">This address is not part of Chronicleaf. Nothing was changed.</p>
      <button
        type="button"
        className="action"
        onClick={() => onNavigate('/operations')}
        data-testid="route-home"
      >
        Go to background jobs
      </button>
    </section>
  );
}

function OperationsList({ onNavigate }: { readonly onNavigate: (to: string) => void }) {
  const page = useOperations(listOperations);
  const items = page.data?.items ?? [];

  return (
    <>
      <p className="view__crumbs">
        <button type="button" className="link" onClick={() => onNavigate('/')} data-testid="operations-back">
          ← Dashboard
        </button>
      </p>
      <OperationsPanel
        operations={items}
        loading={page.loading}
        failure={page.failure}
        onChanged={page.reload}
      />
    </>
  );
}

function OperationDetail({
  operationId,
  onNavigate,
}: {
  readonly operationId: string;
  readonly onNavigate: (to: string) => void;
}) {
  const operation = useOperation(getOperation, operationId);

  if (operation.failure?.status === 404) {
    return (
      <section className="view" data-testid="operation-missing">
        <h1 className="view__heading">Background job not found</h1>
        <p className="view__state">
          There is no such background job in this installation, or it is not yours to see.
        </p>
        <button
          type="button"
          className="action"
          onClick={() => onNavigate('/operations')}
          data-testid="operation-missing-back"
        >
          Back to background jobs
        </button>
      </section>
    );
  }

  if (operation.loading || operation.data === null) {
    return (
      <p className="view__state" aria-live="polite" aria-busy="true" data-testid="operation-loading">
        Loading this background job…
      </p>
    );
  }

  return (
    <>
      <p className="view__crumbs">
        <button
          type="button"
          className="link"
          onClick={() => onNavigate('/operations')}
          data-testid="operation-detail-back"
        >
          ← Background jobs
        </button>
      </p>
      <OperationsPanel
        operations={[operation.data]}
        loading={operation.loading}
        failure={operation.failure}
        onChanged={operation.reload}
      />
    </>
  );
}
