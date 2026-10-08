import { useState } from 'react';

import { useCredentials } from './useCredentials';
import { CREDENTIAL_PROVIDERS, providerHint, providerLabel } from './types';
import type { CredentialProvider } from './types';

/**
 * The credential settings surface, rendered only for a signed-in owner like every other product area.
 *
 * What this component can show is decided by what the server will send it. Every row says one of two
 * true things: "Not configured", or "Configured, last changed at <the server's instant>". There is no
 * masked value, no length, no suffix and no "reveal" control, because the server has no route that
 * would answer one and this component has no state that could hold what it sent. An owner who pasted a
 * token into a field here cannot see it again, cannot retrieve it, and cannot have it pre-filled on a
 * later visit - which is the honest behaviour for a credential the product deliberately cannot read
 * back.
 *
 * A row is only ever allowed to say one of those two things once the server has said it, so a row
 * whose read is still in flight says "Reading" instead. That is not decoration: a write is conditional
 * on the revision the read published, so offering the save control before the read has landed would
 * mean offering to write against a revision this component had not been told. Until the read lands the
 * save control is disabled and no remove control is offered, because neither can honestly be decided
 * from a state that has not been read.
 *
 * The inputs are uncontrolled, empty on every render, and cleared as soon as a save resolves. There is
 * no default value, no placeholder that looks like a value, and no code path that would put a stored
 * credential into the DOM.
 */
export function CredentialVaultPanel() {
  const credentials = useCredentials();
  const [drafts, setDrafts] = useState<Readonly<Record<string, string>>>({});
  const [cleared, setCleared] = useState<Readonly<Record<string, boolean>>>({});

  const draftFor = (provider: CredentialProvider): string => drafts[provider] ?? '';
  const isCleared = (provider: CredentialProvider): boolean => cleared[provider] === true;

  return (
    <section className="view" data-testid="credential-vault">
      <h1 className="view__heading">Provider credentials</h1>

      <p className="view__state" data-testid="credential-vault-intro">
        Paste a token to store it, or paste a new one to replace what is already stored. Chronicleaf
        keeps it sealed and can show you that it is configured and when it last changed. It cannot show
        you the value, and no route will return it.
      </p>

      {credentials.loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="credential-vault-loading">
          Reading what is configured…
        </p>
      ) : null}

      {credentials.failure !== null ? (
        <p className="view__state notice--error" role="alert" data-testid="credential-vault-load-failure">
          {credentials.failure.message} ({credentials.failure.code})
        </p>
      ) : null}

      <ul className="cards" data-testid="credential-vault-providers">
        {CREDENTIAL_PROVIDERS.map((provider) => {
          const state = credentials.states[provider];
          // Whether the panel has actually read what is stored for this provider. A row whose read
          // has not landed is not an empty vault, it is an unanswered question, and it is rendered
          // as one: "Not configured" is something the server said, and this component does not get
          // to say it on the server's behalf. This is also what keeps a write off an unread state,
          // because a write has to present the revision the read published.
          const read = state !== undefined;
          const configured = state?.configured === true;
          const busy = credentials.saving === provider;
          const saved = credentials.savedProvider === provider;
          const refusal =
            credentials.savingFailure !== null && busy === false
              ? credentials.savingFailure
              : null;

          return (
            <li key={provider} className="card" data-testid={`credential-${provider}`}>
              <span className="card__name" data-testid={`credential-${provider}-name`}>
                {providerLabel(provider)}
              </span>

              <span data-testid={`credential-${provider}-status`}>
                {!read ? (
                  <span className="badge badge--muted" data-testid={`credential-${provider}-unknown`}>
                    Reading
                  </span>
                ) : configured ? (
                  <span className="badge" data-testid={`credential-${provider}-configured`}>
                    Configured
                  </span>
                ) : (
                  <span
                    className="badge badge--muted"
                    data-testid={`credential-${provider}-unconfigured`}
                  >
                    Not configured
                  </span>
                )}
              </span>

              {!read ? (
                <span className="card__body" data-testid={`credential-${provider}-updated`}>
                  Reading what is stored for this provider.
                </span>
              ) : configured && state.updatedAt !== null ? (
                <span className="card__body" data-testid={`credential-${provider}-updated`}>
                  Last changed {formatInstant(state.updatedAt)}
                </span>
              ) : (
                <span className="card__body" data-testid={`credential-${provider}-updated`}>
                  Nothing has been stored for this provider in this installation.
                </span>
              )}

              <label className="form__field" htmlFor={`credential-${provider}-input`}>
                <span className="form__label">
                  {configured ? 'Replace the stored token' : 'Store a token'}
                </span>
                <input
                  id={`credential-${provider}-input`}
                  className="form__input"
                  data-testid={`credential-${provider}-input`}
                  type="password"
                  name={`credential-${provider}`}
                  autoComplete="off"
                  spellCheck={false}
                  placeholder={providerHint(provider)}
                  value={draftFor(provider)}
                  aria-invalid={refusal !== null ? 'true' : undefined}
                  aria-describedby={refusal !== null ? `credential-${provider}-failure` : undefined}
                  onChange={(event) =>
                    setDrafts((current) => ({ ...current, [provider]: event.target.value }))
                  }
                />
              </label>

              <p className="view__actions">
                <button
                  type="button"
                  className="action action--primary"
                  data-testid={`credential-${provider}-save`}
                  disabled={busy || !read || draftFor(provider).trim().length === 0}
                  onClick={async () => {
                    const stored = await credentials.save(provider, draftFor(provider));

                    if (stored) {
                      // The value leaves the field the moment it is stored, and there is nothing to put
                      // back: the server will not return it.
                      setDrafts((current) => ({ ...current, [provider]: '' }));
                      setCleared((current) => ({ ...current, [provider]: true }));
                    }
                  }}
                >
                  {busy ? 'Saving…' : configured ? 'Replace' : 'Save'}
                </button>

                {configured ? (
                  <button
                    type="button"
                    className="action"
                    data-testid={`credential-${provider}-delete`}
                    disabled={busy}
                    onClick={() => void credentials.remove(provider)}
                  >
                    Remove
                  </button>
                ) : null}
              </p>

              {isCleared(provider) && saved ? (
                <p className="view__state" data-testid={`credential-${provider}-saved`}>
                  Stored. The field above is empty because the value is not readable here.
                </p>
              ) : null}

              {refusal !== null ? (
                <p
                  className="view__state notice--error" role="alert"
                  id={`credential-${provider}-failure`}
                  data-testid={`credential-${provider}-failure`}
                >
                  {refusal.message} ({refusal.code})
                </p>
              ) : null}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

/**
 * The server's instant, in this browser's own rendering of it. It is formatted from the response and
 * never re-derived, so the panel cannot show a time the server did not send.
 */
function formatInstant(instant: string): string {
  const parsed = new Date(instant);

  return Number.isNaN(parsed.getTime()) ? instant : parsed.toISOString().replace('T', ' ').slice(0, 19) + ' UTC';
}
