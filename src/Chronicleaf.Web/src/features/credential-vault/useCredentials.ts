import { useCallback, useEffect, useState } from 'react';

import { CredentialApiError, deleteCredential, readProvider, saveCredential } from './api';
import { CREDENTIAL_PROVIDERS } from './types';
import type { CredentialProvider, ProviderState } from './types';

/**
 * The state of every provider this installation could store a credential for, read from the server.
 *
 * Two properties matter more than the rest. There is no field here that can hold a secret, and there
 * is no function here that fetches one: the panel asks "is this configured, and when did it change"
 * and gets that, so there is no code path by which a stored value could reach the screen even if a
 * future edit tried to draw it. The `revision` is the token a write presents, published by the server
 * as the entity-tag of the provider's own read.
 */
export interface CredentialsLoaded {
  /**
   * What the server has said, per provider. A provider that is absent from this record has not been
   * read yet, which is not the same as a provider that was read and found unconfigured, and the
   * panel renders those two as different things.
   */
  readonly states: Readonly<Record<string, ProviderState>>;
  readonly loading: boolean;
  readonly failure: CredentialApiError | null;
  /** The provider whose write is in flight, or null. */
  readonly saving: CredentialProvider | null;
  readonly savingFailure: CredentialApiError | null;
  readonly savedProvider: CredentialProvider | null;
  /**
   * Stores a value for a provider, then drops the value. The write presents the revision that
   * provider's own read published; a provider that has not been read is not written.
   */
  readonly save: (provider: CredentialProvider, value: string) => Promise<boolean>;
  /** Removes a provider's credential, conditional on the revision that was read. */
  readonly remove: (provider: CredentialProvider) => Promise<boolean>;
  readonly reload: () => void;
}

/**
 * Reads every provider's state once, in the closed vocabulary the server fixed.
 *
 * The reads are per provider rather than a list because each one publishes the revision a write has to
 * present, and because an unconfigured provider has to be able to say so honestly: a list only ever
 * contains the providers that are configured, so asking each one is what makes "not configured" an
 * answer rather than an absence.
 */
export function useCredentials(): CredentialsLoaded {
  const [states, setStates] = useState<Readonly<Record<string, ProviderState>>>({});
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<CredentialApiError | null>(null);
  const [saving, setSaving] = useState<CredentialProvider | null>(null);
  const [savingFailure, setSavingFailure] = useState<CredentialApiError | null>(null);
  const [savedProvider, setSavedProvider] = useState<CredentialProvider | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);

    Promise.all(
      CREDENTIAL_PROVIDERS.map((provider): Promise<ProviderAnswer> =>
        readProvider(provider, controller.signal).then(
          (state) => ({ provider, state, failure: null }),
          (thrown: unknown) => {
            if (isAbort(thrown)) {
              throw thrown;
            }

            return { provider, state: null, failure: asFailure(thrown) };
          },
        ),
      ),
    )
      .then((answers) => {
        if (!active) {
          return;
        }

        const read: Record<string, ProviderState> = {};

        for (const answer of answers) {
          if (answer.state !== null) {
            read[answer.provider] = answer.state;
          }
        }

        setStates(read);

        // One refusal is enough to report, and the first provider that refused is the one named. A
        // vault with no usable key refuses every read, so this is what an operator sees.
        setFailure(answers.find((answer) => answer.failure !== null)?.failure ?? null);
      })
      .catch(() => {
        // An abort is the only rejection that reaches here, and it means the owner navigated away.
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
  }, [nonce]);

  const reread = useCallback(async (provider: CredentialProvider) => {
    const state = await readProvider(provider);

    setStates((current) => ({ ...current, [provider]: state }));
  }, []);

  const save = useCallback(
    async (provider: CredentialProvider, value: string): Promise<boolean> => {
      const state = states[provider];

      if (state === undefined) {
        // This provider has not been read yet, so there is no revision to present. Substituting 0
        // here would claim "this installation has nothing stored for this provider" without ever
        // having asked, and 0 is a real token the server publishes, not a placeholder: the write
        // would be refused as stale, and the value would be left sitting in the field beside the
        // refusal. A read that has not happened is not a revision. The panel does not offer the
        // control until the read has landed, so this is the same guard the remove path already had.
        return false;
      }

      setSaving(provider);
      setSavingFailure(null);
      setSavedProvider(null);

      try {
        // The value is passed straight through and is not kept here, and the panel clears its input
        // when this resolves. Nothing in this hook's state can hold it afterwards.
        await saveCredential(provider, value, state.revision);
        await reread(provider);
        setSavedProvider(provider);

        return true;
      } catch (thrown: unknown) {
        if (!isAbort(thrown)) {
          setSavingFailure(asFailure(thrown));

          // A conflict or an unusable vault means the server's own state is the truth, so the panel
          // re-reads rather than pretending the write landed.
          await reread(provider).catch(() => undefined);
        }

        return false;
      } finally {
        setSaving(null);
      }
    },
    [reread, states],
  );

  const remove = useCallback(
    async (provider: CredentialProvider): Promise<boolean> => {
      const state = states[provider];

      if (state === undefined || state.id === null) {
        return false;
      }

      setSaving(provider);
      setSavingFailure(null);
      setSavedProvider(null);

      try {
        await deleteCredential(state.id, state.revision);
        await reread(provider);

        return true;
      } catch (thrown: unknown) {
        if (!isAbort(thrown)) {
          setSavingFailure(asFailure(thrown));
          await reread(provider).catch(() => undefined);
        }

        return false;
      } finally {
        setSaving(null);
      }
    },
    [reread, states],
  );

  const reload = useCallback(() => {
    setNonce((current) => current + 1);
  }, []);

  return {
    states,
    loading,
    failure,
    saving,
    savingFailure,
    savedProvider,
    save,
    remove,
    reload,
  };
}

/** What one provider's read produced, or the refusal that provider answered with. */
interface ProviderAnswer {
  readonly provider: CredentialProvider;
  readonly state: ProviderState | null;
  readonly failure: CredentialApiError | null;
}

function asFailure(thrown: unknown): CredentialApiError {
  return thrown instanceof CredentialApiError
    ? thrown
    : new CredentialApiError(
        {
          code: 'internal_error',
          message: 'The request could not be completed.',
          traceId: '',
        },
        0,
      );
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
