import { CredentialVaultPanel } from './CredentialVaultPanel';

/**
 * The settings area, rendered only for a signed-in owner, like every other product area.
 *
 * Path:
 * - `/settings/credentials` the credential surface
 *
 * The panel reads the same private routes the session gate already protects. There is no path here
 * that reveals a stored credential, because there is no route that would answer one: a settings URL is
 * the safest thing a browser can hold, and a history entry, a screenshot or a shared window of this
 * page reveals metadata and nothing else.
 */
export function CredentialsArea({
  path,
  onNavigate,
}: {
  readonly path: string;
  readonly onNavigate: (to: string) => void;
}) {
  const segments = path.split('/').filter((segment) => segment.length > 0);

  if (segments[0] !== 'settings' || segments[1] !== 'credentials' || segments.length !== 2) {
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

  return (
    <>
      <p className="view__crumbs">
        <button type="button" className="link" onClick={() => onNavigate('/')} data-testid="credentials-back">
          ← Dashboard
        </button>
      </p>
      <CredentialVaultPanel />
    </>
  );
}
