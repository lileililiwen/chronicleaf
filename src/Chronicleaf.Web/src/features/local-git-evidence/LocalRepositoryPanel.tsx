import { useId, useState } from 'react';

import { localGitFailureLabel } from './types';
import { useLocalCatalog, useLocalRepository } from './useLocalGitEvidence';

/**
 * What this project collects from the operator's own disk, and the one form that changes it
 * (LOCAL-GIT-EVIDENCE-002).
 *
 * It is a panel and not a settings page because a registration is a property of one project: the alias and
 * the path it is made of belong to the project whose milestones will ask it for refs, and a route that
 * could register a different project would be a route about a different thing than the one it names.
 *
 * Four things it deliberately does not do, each because the requirement says so:
 *
 * - It offers a picker of the operator's registered roots and a path relative to one. It is not a file
 *   browser, and there is no field that could name a directory: the registered directory lives in the
 *   operator's configuration, and a form that accepted an address would be a form that could aim the
 *   server's filesystem reads somewhere it was not asked to go.
 * - It shows no directory. Not in the picker, not in the stored registration, not in a notice. The alias is
 *   the operator's own name and the relative path is the owner's; the directory behind the alias is not this
 *   panel's to publish, and a panel that printed it would describe the machine the server runs on.
 * - It does not offer to unregister. There is no delete route, because the collection history on a
 *   milestone is not a thing this panel may throw away.
 * - It renders a deployment that may not read local directories as a decision rather than as a failure,
 *   and says which of the two empty states it is showing.
 */
export function LocalRepositoryPanel({
  projectId,
  archived,
}: {
  readonly projectId: string;
  /** An archived project cannot be pointed at anything: the server refuses the write. */
  readonly archived: boolean;
}) {
  const catalog = useLocalCatalog();
  const registration = useLocalRepository(projectId);
  const [alias, setAlias] = useState('');
  const [relativePath, setRelativePath] = useState('.');
  const [pickerId] = useId();

  const stored = registration.repository;
  const roots = catalog.catalog?.rootAliases ?? [];
  const selected = roots.includes(alias) ? alias : (roots[0] ?? '');

  const submit = async () => {
    if (selected.length === 0 || relativePath.trim().length === 0) {
      return;
    }

    await registration.register({ rootAlias: selected, relativePath: relativePath.trim() });
  };

  return (
    <section className="evidence" data-testid="local-repository-panel">
      <h4 className="evidence__heading">Local repository this project collects from</h4>

      {catalog.loading && catalog.catalog === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="local-catalog-loading">
          Reading the roots this installation collects from…
        </p>
      ) : catalog.failure !== null ? (
        <p className="notice--error" role="alert" data-testid="local-catalog-failed">
          The registered roots could not be read: {catalog.failure.message}
        </p>
      ) : catalog.unavailable ? (
        <div className="notice notice--muted" data-testid="local-catalog-unavailable">
          <p className="empty__title" data-testid="local-catalog-unavailable-title">
            This deployment does not read local repositories
          </p>
          <p className="empty__body" data-testid="local-catalog-unavailable-body">
            Local repository evidence is a single-tenant feature. On a shared deployment the directories an
            operator could register belong to other tenants, so nothing here is searched and no root list is
            published. Connect a repository over its hosting provider instead.
          </p>
        </div>
      ) : roots.length === 0 ? (
        <div className="empty" data-testid="local-catalog-empty">
          <p className="empty__title" data-testid="local-catalog-empty-title">
            No roots are registered on this installation
          </p>
          <p className="empty__body" data-testid="local-catalog-empty-body">
            Chronicleaf only reads a repository inside a directory its operator registered, and this
            installation has none. That is the whole of what is stored - it is not a placeholder waiting to
            fill in. An operator adds roots through the installation's configuration, not through this page.
          </p>
        </div>
      ) : null}

      {registration.loading && stored === null && !catalog.unavailable ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="local-repository-loading">
          Reading what this project collects from…
        </p>
      ) : registration.failure !== null ? (
        <p className="notice--error" role="alert" data-testid="local-repository-failed">
          The local repository could not be read: {registration.failure.message}
        </p>
      ) : stored === null && registration.missing ? (
        <div className="empty" data-testid="local-repository-empty">
          <p className="empty__title" data-testid="local-repository-empty-title">
            No local repository is registered to this project
          </p>
          <p className="empty__body" data-testid="local-repository-empty-body">
            A repository has to be named before a milestone can ask it for a ref; until then the milestones
            below offer the owner's own notes and nothing else.
          </p>
        </div>
      ) : stored !== null ? (
        <div data-testid="local-repository-current">
          <p className="evidence__revision" data-testid="local-repository-name">
            {stored.rootAlias}/{stored.relativePath}
          </p>
          <p className="evidence__item-meta" data-testid="local-repository-branch">
            {stored.defaultBranch === null
              ? 'The repository has no branch checked out, or its HEAD is detached.'
              : `The repository's HEAD is on ${stored.defaultBranch}.`}
          </p>
          <p className="evidence__item-meta" data-testid="local-repository-head">
            {stored.headSha === null
              ? 'The repository has no commit yet, so there is nothing to collect until it does.'
              : `HEAD resolved to revision ${stored.headSha} when this was registered. A collection resolves the ref you ask for again, and never falls back to this one.`}
          </p>
          <p className="evidence__item-meta" data-testid="local-repository-readonly">
            Chronicleaf only reads. It starts no Git command that changes a repository, and it needs no token
            for one, so there is no credential stored for this project and none to leak.
          </p>
        </div>
      ) : null}

      {archived || catalog.unavailable || roots.length === 0 ? null : (
        <form
          className="evidence__form"
          data-testid="local-repository-form"
          onSubmit={(event) => {
            event.preventDefault();
            void submit();
          }}
        >
          <p className="view__hint" data-testid="local-repository-hint">
            A root this installation already registered, and a path inside it. The directory itself is the
            operator's and is never shown or sent by this page - only the name they gave it.
          </p>

          <div className="view">
            <label htmlFor={`local-root-${projectId}`}>Registered root</label>
            <select
              id={`local-root-${projectId}`}
              className="session__input"
              value={selected}
              data-testid="local-root-alias"
              onChange={(event) => setAlias(event.target.value)}
            >
              {roots.map((root) => (
                <option key={root} value={root}>
                  {root}
                </option>
              ))}
            </select>
            <p className="evidence__item-meta">
              {roots.length} root{roots.length === 1 ? '' : 's'} configured by the operator. This is a list
              of names, not a browser: nothing on this page can name a directory that is not on it.
            </p>
          </div>

          <div className="view">
            <label htmlFor={`local-path-${projectId}`}>Path inside that root</label>
            <input
              id={`local-path-${projectId}`}
              className="session__input"
              type="text"
              value={relativePath}
              autoComplete="off"
              data-testid="local-relative-path"
              onChange={(event) => setRelativePath(event.target.value)}
            />
            <p className="evidence__item-meta">
              Relative, and <code>.</code> for the root itself. A path that leaves the root, through
              traversal or through a symbolic link, is refused before Git is asked about anything.
            </p>
          </div>

          <div className="form__actions">
            <button
              type="submit"
              className="action action--primary"
              disabled={
                registration.registering || selected.length === 0 || relativePath.trim().length === 0
              }
              data-testid="local-register"
            >
              {registration.registering
                ? 'Checking the repository…'
                : stored === null
                  ? 'Register this repository'
                  : 'Point this project at another local repository'}
            </button>
          </div>

          {registration.registerNotice !== null ? (
            <p className="notice notice--ok" role="status" data-testid="local-repository-notice">
              {registration.registerNotice}
            </p>
          ) : null}

          {registration.registerFailure !== null ? (
            <p className="notice notice--error" role="alert" data-testid="local-register-failed">
              {localGitFailureLabel(registration.registerFailure.code)}{' '}
              <span data-testid="local-register-failed-message">
                The server said: {registration.registerFailure.message}
              </span>{' '}
              Nothing was stored. Chronicleaf will not tell you which directory it looked at, because that
              directory is the operator's rather than yours.
            </p>
          ) : null}
        </form>
      )}

      {/* Kept for a test that wants to assert the picker's own identity rather than its rendered options. */}
      <input type="hidden" value={pickerId} readOnly data-testid="local-repository-picker" />
    </section>
  );
}
