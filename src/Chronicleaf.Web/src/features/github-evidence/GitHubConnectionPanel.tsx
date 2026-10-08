import { useState } from 'react';

import { CredentialApiError, readProvider, saveCredential } from '../credential-vault/api';
import { gitHubFailureLabel } from './types';
import { useGitHubConnection } from './useGitHubEvidence';

/**
 * What this project collects from, and the one form that changes it (GITHUB-EVIDENCE-001).
 *
 * It is a panel and not a settings page because a connection is a property of one project: the two
 * names it is made of belong to the project whose milestones will ask it for refs, and a route that
 * could connect a different project would be a route about a different thing than the one it names.
 *
 * Four things it deliberately does not do, each because the requirement says so:
 *
 * - It asks for an owner and a repository name and never for a URL. The host is a constant of the
 *   server's transport, and a form that accepted an address would be a form that could aim the
 *   server's next request somewhere it was not asked to go.
 * - It shows nothing about a stored token except whether one is configured. The credential is the
 *   delivered vault's business and this form has no field that could hold a value, because the one
 *   field it has is cleared the moment the value is stored.
 * - It does not render a refused repository as a failure of the project. The refusal is GitHub's own
 *   answer, shown as it arrived: a repository that is not public and a repository that does not exist
 *   are deliberately the same answer, and an owner who is told which one it was would be told a thing
 *   the server cannot know.
 * - It does not offer to disconnect. There is no delete route, because the collection history on a
 *   milestone is not a thing this panel may throw away.
 */
export function GitHubConnectionPanel({
  projectId,
  archived,
}: {
  readonly projectId: string;
  /** An archived project cannot be pointed at anything: the server refuses the write. */
  readonly archived: boolean;
}) {
  const connection = useGitHubConnection(projectId);
  const [owner, setOwner] = useState('');
  const [repository, setRepository] = useState('');
  const [token, setToken] = useState('');
  const [saving, setSaving] = useState(false);
  const [vaultFailure, setVaultFailure] = useState<CredentialApiError | null>(null);

  const connected = connection.connection;

  const submit = async () => {
    const secret = token.trim();

    // Cleared before the value is used and not restored afterwards, so a token exists in this
    // component for as long as one keystroke and no longer. There is no read anywhere in this feature
    // that could bring it back, and a reload starts with an empty field because there is nothing to
    // rehydrate from.
    setToken('');
    setVaultFailure(null);

    if (owner.trim().length === 0 || repository.trim().length === 0) {
      return;
    }

    setSaving(true);

    let credentialRef: string | null = null;

    try {
      if (secret.length > 0) {
        // Stored through the delivered vault route, under a revision read from that route rather than
        // assumed, so a token another panel replaced between reading and writing is refused instead
        // of being overwritten. The value is handed over once and dropped with the local above.
        const current = await readProvider('github');
        const stored = await saveCredential('github', secret, current.revision);
        credentialRef = stored.id;
      }
    } catch (thrown) {
      // The token could not be stored, so no connection is attempted: a connection that pointed at a
      // credential nothing holds would look configured and collect nothing.
      setVaultFailure(thrown instanceof CredentialApiError ? thrown : null);
      setSaving(false);
      return;
    }

    try {
      await connection.connect({
        owner: owner.trim(),
        repository: repository.trim(),
        credentialRef,
      });

      setOwner('');
      setRepository('');
    } finally {
      setSaving(false);
      setToken('');
    }
  };

  return (
    <section className="evidence" data-testid="github-connection-panel">
      {/* WEB-A11Y-ROLES-001. Project-level panels are h2 sections under the view h1. */}
      <h2 className="evidence__heading">Repository this project collects from</h2>

      {connection.loading && connected === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="github-connection-loading">
          Reading what this project collects from…
        </p>
      ) : connection.failure !== null ? (
        <p className="notice--error" role="alert" data-testid="github-connection-failed">
          The repository connection could not be read: {connection.failure.message}
        </p>
      ) : connected === null ? (
        <div className="empty" data-testid="github-connection-empty">
          <p className="empty__title" data-testid="github-connection-empty-title">
            No repository is connected to this project
          </p>
          <p className="empty__body" data-testid="github-connection-empty-body">
            Nothing has been collected from anywhere, and that is the whole of what is stored - it is
            not a placeholder waiting to fill in. A repository has to be named before a milestone can
            ask it for a ref; until then the milestones below offer the owner's own notes and nothing
            else.
          </p>
        </div>
      ) : (
        <div data-testid="github-connection-current">
          <p className="evidence__revision" data-testid="github-connection-repository">
            {connected.owner}/{connected.repository}
          </p>
          <p className="evidence__item-meta" data-testid="github-connection-branch">
            {connected.defaultBranch === null
              ? 'GitHub did not report a default branch for this repository.'
              : `GitHub reported ${connected.defaultBranch} as its default branch.`}
          </p>
          <p className="evidence__item-meta" data-testid="github-connection-credential">
            {connected.hasCredential
              ? 'A token is stored for this repository. Its value is not shown here, is not sent to this panel, and cannot be read back.'
              : 'No token is stored, so this repository is read as a public one. A private repository needs a token stored through the credential settings first.'}
          </p>
          <p className="evidence__item-meta" data-testid="github-connection-repository-id">
            GitHub answered for repository id {connected.repositoryId ?? 'unknown'}. A different
            repository under the same name is a different id, and a renamed repository keeps its
            original one.
          </p>
        </div>
      )}

      {archived ? (
        <p className="notice notice--muted" data-testid="github-connection-archived">
          This project is archived, so what it collected is kept and nothing else can be connected to
          it.
        </p>
      ) : (
        <form
          className="evidence__form"
          data-testid="github-connection-form"
          onSubmit={(event) => {
            event.preventDefault();
            void submit();
          }}
        >
          <p className="view__hint" data-testid="github-connection-hint">
            An owner and a repository name, never an address. Chronicleaf reads the repository from
            GitHub's own host; nothing on this page can change where it looks.
          </p>

          <div className="view">
            <label htmlFor={`github-owner-${projectId}`}>Owner</label>
            <input
              id={`github-owner-${projectId}`}
              className="session__input"
              type="text"
              value={owner}
              autoComplete="off"
              data-testid="github-owner"
              onChange={(event) => setOwner(event.target.value)}
            />
          </div>

          <div className="view">
            <label htmlFor={`github-repository-${projectId}`}>Repository</label>
            <input
              id={`github-repository-${projectId}`}
              className="session__input"
              type="text"
              value={repository}
              autoComplete="off"
              data-testid="github-repository"
              onChange={(event) => setRepository(event.target.value)}
            />
          </div>

          <div className="view">
            <label htmlFor={`github-token-${projectId}`}>Token, if the repository needs one</label>
            <input
              id={`github-token-${projectId}`}
              className="session__input"
              type="password"
              value={token}
              autoComplete="off"
              data-testid="github-token"
              onChange={(event) => setToken(event.target.value)}
            />
            <p className="evidence__item-meta">
              Optional, and gone the moment it is stored. It is written to the credential vault under
              this project's provider slot and never comes back to this page, not even to be redrawn
              - only whether one is stored is ever shown.
            </p>
          </div>

          <div className="form__actions">
            <button
              type="submit"
              className="action action--primary"
              disabled={saving || connection.connecting}
              data-testid="github-connect"
            >
              {saving || connection.connecting
                ? 'Checking the repository…'
                : connected === null
                  ? 'Connect this repository'
                  : 'Point this project at another repository'}
            </button>
          </div>

          {connection.connectNotice !== null ? (
            <p className="notice notice--ok" role="status" data-testid="github-connection-notice">
              {connection.connectNotice}
            </p>
          ) : null}

          {connection.connectFailure !== null ? (
            <p className="notice notice--error" role="alert" data-testid="github-connect-failed">
              {gitHubFailureLabel(connection.connectFailure.code)}{' '}
              <span data-testid="github-connect-failed-message">
                The server said: {connection.connectFailure.message}
              </span>{' '}
              Nothing was stored. A repository that is not public and a repository that does not exist
              are the same answer, and Chronicleaf will not tell you which one this was.
            </p>
          ) : null}

          {vaultFailure !== null ? (
            <p className="notice notice--error" role="alert" data-testid="github-token-failed">
              The token was not stored, so no repository was connected: {vaultFailure.message} The
              operator key decides whether a value can be stored at all, and nothing was written
              without it.
            </p>
          ) : null}
        </form>
      )}
    </section>
  );
}
