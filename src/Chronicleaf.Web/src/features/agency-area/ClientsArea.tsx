import { useCallback, useEffect, useState } from 'react';

import { archiveClient, createClient, listClients, updateClient } from './api';
import { AgencyApiError } from './types';
import type { AgencyClient } from './types';
import { FieldCount } from '../ui-feedback/FieldCount';
import { useToast } from '../ui-feedback/Toast';

/**
 * The agency Clients area (AGENCY-AREA-UI-001).
 *
 * <b>Clients organize projects; they are not a portal.</b> The list, the
 * create/edit forms and the archive control below reach only the delivered
 * client routes. No recipient, no price and no send flag exists on any call,
 * so no invoicing or email surface can be built from here by accident.
 */
export function ClientsArea({ onNavigate }: { readonly onNavigate: (to: string) => void }) {
  const [clients, setClients] = useState<readonly AgencyClient[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<AgencyApiError | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);

    try {
      const page = await listClients();
      setClients(page.items);
      setFailure(null);
    } catch (thrown) {
      setFailure(toAgencyError(thrown));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <section className="view" data-testid="clients">
      <p className="view__crumbs">
        <button
          type="button"
          className="link"
          onClick={() => onNavigate('/')}
          data-testid="clients-back"
        >
          ← Dashboard
        </button>
      </p>
      <h1 className="view__heading">Clients</h1>

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="clients-error">
          {failure.code}: {failure.message}
        </p>
      ) : null}

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="clients-loading">
          Loading clients…
        </p>
      ) : clients !== null && clients.length === 0 && !creating ? (
        <div className="empty" data-testid="clients-empty">
          <p className="empty__title">No clients yet.</p>
          <p className="view__state">
            A client groups projects for weekly reporting. Create the first client to begin.
          </p>
          <button
            type="button"
            className="action action--primary"
            onClick={() => setCreating(true)}
            data-testid="clients-create-first"
          >
            Create your first client
          </button>
        </div>
      ) : (
        <ul className="cards" data-testid="clients-list">
          {(clients ?? []).map((client) => (
            <ClientCard
              key={client.id}
              client={client}
              onOpen={() => onNavigate(`/clients/${client.id}`)}
              onChanged={load}
            />
          ))}
        </ul>
      )}

      {(clients !== null && clients.length > 0) || creating ? (
        <div className="view__actions" data-testid="clients-create-area">
          {creating ? (
            <ClientForm
              initial={null}
              onSaved={(saved) => {
                setCreating(false);
                void load().then(() => onNavigate(`/clients/${saved.id}`));
              }}
              onCancel={() => setCreating(false)}
            />
          ) : (
            <button
              type="button"
              className="action"
              onClick={() => setCreating(true)}
              data-testid="clients-new"
            >
              New client
            </button>
          )}
        </div>
      ) : null}
    </section>
  );
}

function ClientCard({
  client,
  onOpen,
  onChanged,
}: {
  readonly client: AgencyClient;
  readonly onOpen: () => void;
  readonly onChanged: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [acting, setActing] = useState(false);
  const [actionFailure, setActionFailure] = useState<AgencyApiError | null>(null);

  const archive = useCallback(async () => {
    setActing(true);
    setActionFailure(null);

    try {
      await archiveClient(client.id, client.revision);
      setConfirming(false);
      onChanged();
    } catch (thrown) {
      setActionFailure(toAgencyError(thrown));
    } finally {
      setActing(false);
    }
  }, [client.id, client.revision, onChanged]);

  if (editing) {
    return (
      <li>
        <ClientForm
          initial={client}
          onSaved={() => {
            setEditing(false);
            onChanged();
          }}
          onCancel={() => setEditing(false)}
        />
      </li>
    );
  }

  return (
    <li>
      <button type="button" className="card" onClick={onOpen} data-testid="client-card">
        <span className="card__name" data-testid="client-card-name">
          {client.name}
        </span>
        <span className="card__body" data-testid="client-card-notes">
          {client.notes === '' ? 'No notes yet.' : client.notes}
        </span>
        {client.archivedAt ? (
          <span className="badge badge--muted" data-testid="client-card-archived" aria-label="Status: Archived">
            Archived
          </span>
        ) : null}
      </button>
      <p className="card__areas">
        <button
          type="button"
          className="link"
          onClick={() => setEditing(true)}
          data-testid="client-edit"
          disabled={client.archivedAt !== null}
          title={
            client.archivedAt !== null
              ? 'An archived client keeps its history and takes no edits.'
              : undefined
          }
        >
          Edit
        </button>{' '}
        {client.archivedAt === null ? (
          confirming ? (
            <>
              <button
                type="button"
                className="link"
                onClick={() => void archive()}
                disabled={acting}
                data-testid="client-archive-confirm"
              >
                Confirm archive
              </button>{' '}
              <button
                type="button"
                className="link"
                onClick={() => setConfirming(false)}
                data-testid="client-archive-cancel"
              >
                Keep
              </button>
            </>
          ) : (
            <button
              type="button"
              className="link"
              onClick={() => setConfirming(true)}
              data-testid="client-archive"
            >
              Archive
            </button>
          )
        ) : null}
      </p>
      {actionFailure ? (
        <p className="notice notice--error" role="alert" data-testid="client-card-error">
          {actionFailure.code}: {actionFailure.message}
        </p>
      ) : null}
    </li>
  );
}

export function ClientForm({
  initial,
  onSaved,
  onCancel,
}: {
  readonly initial: AgencyClient | null;
  readonly onSaved: (saved: AgencyClient) => void;
  readonly onCancel: () => void;
}) {
  const [name, setName] = useState(initial?.name ?? '');
  const [notes, setNotes] = useState(initial?.notes ?? '');
  const [saving, setSaving] = useState(false);
  const [failure, setFailure] = useState<AgencyApiError | null>(null);
  // WEB-FEED-001: outcomes are announced through the shell-level toast region.
  const { showToast } = useToast();

  const save = useCallback(async () => {
    setSaving(true);
    setFailure(null);

    try {
      const saved =
        initial === null
          ? await createClient({ name: name.trim(), notes })
          : await updateClient(initial.id, initial.revision, { name: name.trim(), notes });
      showToast('Client saved.', 'ok');
      onSaved(saved);
    } catch (thrown) {
      const apiError = toAgencyError(thrown);
      setFailure(apiError);
      showToast(apiError.message, 'error');
    } finally {
      setSaving(false);
    }
  }, [initial, name, notes, onSaved, showToast]);

  return (
    <form
      data-testid="client-form"
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
    >
      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="client-form-error">
          {failure.code}: {failure.message}
        </p>
      ) : null}
      <p>
        <label htmlFor="client-form-name">Name</label>
        <input
          id="client-form-name"
          data-testid="client-form-name"
          value={name}
          maxLength={160}
          onChange={(event) => setName(event.target.value)}
        />
        <FieldCount value={name} max={160} testId="client-form-name-count" />
      </p>
      <p>
        <label htmlFor="client-form-notes">Notes</label>
        <textarea
          id="client-form-notes"
          data-testid="client-form-notes"
          value={notes}
          maxLength={20000}
          onChange={(event) => setNotes(event.target.value)}
        />
        <FieldCount value={notes} max={20000} testId="client-form-notes-count" />
      </p>
      <p className="view__actions">
        <button
          type="submit"
          className="action action--primary"
          disabled={saving || name.trim().length === 0}
          data-testid="client-form-save"
        >
          {initial === null ? 'Create client' : 'Save changes'}
        </button>{' '}
        <button type="button" className="action" onClick={onCancel} data-testid="client-form-cancel">
          Cancel
        </button>
      </p>
    </form>
  );
}

function toAgencyError(thrown: unknown): AgencyApiError {
  if (thrown instanceof AgencyApiError) {
    return thrown;
  }

  return new AgencyApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The request could not be completed.',
      traceId: '',
    },
    0,
  );
}
