import { useEffect, useState } from 'react';

import { createTenant, listTenants, recordImport, selectTenant, startLogin } from './api';
import { TenancyApiError, type TenantMembership } from './types';

/**
 * The hosted tenancy triggers (FEATURE-TRIGGER-PANELS-002).
 *
 * Three states are visible here and each is shown as what it is. A Community
 * single-tenant installation shows the server's own Community answers rather
 * than a pretended tenancy: no client-side flag invents tenants, and no tenant
 * is listed that the server did not serve. A refusal shows the server's own
 * code and message verbatim.
 *
 * Nothing here fires except on an explicit owner act, except the initial list
 * read that tells the owner where they stand. The hosted login only starts the
 * challenge and navigates the browser to the returned authorization address;
 * the callback itself is server-side.
 */
export function TenancyPanel() {
  const [tenants, setTenants] = useState<readonly TenantMembership[]>([]);
  const [name, setName] = useState('');
  const [selectId, setSelectId] = useState('');
  const [importId, setImportId] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<TenancyApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    listTenants(controller.signal)
      .then((listed) => {
        if (active) {
          setTenants(listed.tenants);
        }
      })
      .catch((thrown: unknown) => {
        if (active && !isAbort(thrown)) {
          setFailure(asFailure(thrown));
        }
      })
      .finally(() => {
        if (active) {
          setLoading(false);
        }
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, []);

  const run = async (work: () => Promise<string | null>) => {
    setBusy(true);
    setFailure(null);
    setNotice(null);

    try {
      const message = await work();
      setNotice(message);
      setTenants((await listTenants()).tenants);
    } catch (thrown) {
      setFailure(asFailure(thrown));
    } finally {
      setBusy(false);
    }
  };

  const create = () =>
    run(async () => {
      const tenant = await createTenant(name.trim());
      setName('');
      return `${tenant.name} is created and selected onto this session.`;
    });

  const select = (tenantId: string) =>
    run(async () => {
      const tenant = await selectTenant(tenantId);
      return `${tenant.name} is selected onto this session.`;
    });

  const login = () =>
    run(async () => {
      const started = await startLogin();
      // The only navigation this panel performs: to the authorization address the server
      // minted, carrying the state to bring back. Nothing secret travels in this document.
      window.location.assign(started.authorizationUrl);
      return null;
    });

  const record = () =>
    run(async () => {
      await recordImport(importId.trim());
      setImportId('');
      return 'The Community volume acknowledgment is recorded. No row was moved.';
    });

  return (
    <section className="view" aria-label="Hosted tenancy" data-testid="tenancy-panel">
      <h1 className="view__heading" data-testid="tenancy-heading">
        Hosted tenancy
      </h1>
      <p className="view__hint" data-testid="tenancy-hint">
        Tenants belong to the signed-in caller and are served by the server, never invented
        here. In a Community installation these same views show the server&apos;s own Community
        answers.
      </p>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="tenancy-loading">
          Reading this caller&apos;s tenants…
        </p>
      ) : failure && tenants.length === 0 ? (
        <p className="notice notice--error" role="alert" data-testid="tenancy-error">
          {failure.message}
        </p>
      ) : tenants.length === 0 ? (
        <p className="view__state" data-testid="tenancy-empty">
          No tenants. Nothing has been created for this caller, so there is nothing to select.
        </p>
      ) : (
        <ul className="milestones" data-testid="tenancy-list">
          {tenants.map((tenant) => (
            <li key={tenant.tenantId} className="milestone" data-testid="tenancy-item">
              <span data-testid="tenancy-name">{tenant.name}</span>{' '}
              <span data-testid="tenancy-role">{tenant.role}</span>
              <button
                type="button"
                className="action"
                onClick={() => void select(tenant.tenantId)}
                disabled={busy}
                data-testid="tenancy-select"
              >
                Select
              </button>
            </li>
          ))}
        </ul>
      )}

      {failure && tenants.length > 0 ? (
        <p className="notice notice--error" role="alert" data-testid="tenancy-error">
          {failure.message}
        </p>
      ) : null}

      {notice ? (
        <p className="notice notice--ok" role="status" data-testid="tenancy-notice">
          {notice}
        </p>
      ) : null}

      {/* WEB-A11Y-ROLES-001. Sections are h2 under the view h1. */}
      <h2 className="view__heading">Create a tenant</h2>
      <label className="form__label">
        Tenant name
        <input
          type="text"
          className="form__input"
          value={name}
          onChange={(event) => setName(event.target.value)}
          data-testid="tenancy-name-input"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void create()}
          disabled={busy}
          data-testid="tenancy-create"
        >
          Create
        </button>
      </div>

      <h2 className="view__heading">Select a tenant</h2>
      <label className="form__label">
        Tenant id
        <input
          type="text"
          className="form__input"
          value={selectId}
          onChange={(event) => setSelectId(event.target.value)}
          data-testid="tenancy-select-input"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => void select(selectId.trim())}
          disabled={busy}
          data-testid="tenancy-select-by-id"
        >
          Select
        </button>
      </div>

      <h2 className="view__heading">Hosted login</h2>
      <p className="view__hint">
        Starts a verified login and navigates to the authorization address. The callback is
        server-side.
      </p>
      <div className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => void login()}
          disabled={busy}
          data-testid="tenancy-login-start"
        >
          Start hosted login
        </button>
      </div>

      <h2 className="view__heading">Community import</h2>
      <label className="form__label">
        Tenant id
        <input
          type="text"
          className="form__input"
          value={importId}
          onChange={(event) => setImportId(event.target.value)}
          data-testid="tenancy-import-input"
        />
      </label>
      <div className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => void record()}
          disabled={busy}
          data-testid="tenancy-import"
        >
          Record import
        </button>
      </div>
    </section>
  );
}

function asFailure(thrown: unknown): TenancyApiError | null {
  return thrown instanceof TenancyApiError ? thrown : null;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
