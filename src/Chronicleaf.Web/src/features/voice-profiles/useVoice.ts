import { useCallback, useEffect, useRef, useState } from 'react';

import { VoiceApiError, VoiceInputError } from './api';
import type { VoiceCatalogue, VoiceCheck, VoiceDraft, VoiceDraftPage, VoiceProfile } from './types';

/**
 * Loading state for the preset catalogue.
 *
 * The catalogue is this build's fixed vocabulary rather than stored state, so it never has an empty
 * answer and never reports a revision. It is read once per mount and then held, because a preset
 * list that changed under the editor mid-save would leave the owner comparing values from two
 * different vocabularies.
 */
export interface CatalogueLoaded {
  readonly data: VoiceCatalogue | null;
  readonly loading: boolean;
  readonly failure: VoiceApiError | null;
}

/** Reads the catalogue whenever the component mounts. */
export function useVoiceCatalogue(
  read: (signal: AbortSignal) => Promise<VoiceCatalogue>,
): CatalogueLoaded {
  const [data, setData] = useState<VoiceCatalogue | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<VoiceApiError | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    read(controller.signal)
      .then((answer) => {
        if (active) {
          setData(answer);
          setFailure(null);
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return { data, loading, failure };
}

/**
 * A project's stored voice, with the revision its next write has to present.
 *
 * The revision lives in this hook rather than being read at save time, and that is the point: it is
 * the revision of the answer the owner is actually looking at. A save is only offered once it exists,
 * so a first save can never present a placeholder that the server refuses - which would strand the
 * owner's edits behind a conflict they did not cause.
 */
export interface ProfileLoaded {
  readonly profile: VoiceProfile | null;
  /** The revision the next write must present, or null while it is unknown. */
  readonly revision: number | null;
  readonly loading: boolean;
  readonly failure: VoiceApiError | null;
  readonly reload: () => void;
}

/**
 * Reads a project's voice, and refuses to write state after the view has gone or the project has
 * changed - so a slow answer for a project the owner has left cannot overwrite the current one.
 */
export function useVoiceProfile(
  read: (projectId: string, signal: AbortSignal) => Promise<{ profile: VoiceProfile; revision: number }>,
  projectId: string,
): ProfileLoaded {
  const [profile, setProfile] = useState<VoiceProfile | null>(null);
  const [revision, setRevision] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<VoiceApiError | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);

    read(projectId, controller.signal)
      .then((answer) => {
        if (!active) {
          return;
        }

        setProfile(answer.profile);
        setRevision(answer.revision);
        setFailure(null);
      })
      .catch((thrown: unknown) => {
        if (!active || isAbort(thrown)) {
          return;
        }

        setProfile(null);
        setRevision(null);
        setFailure(asFailure(thrown));
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, nonce]);

  return {
    profile,
    revision,
    loading,
    failure,
    reload: useCallback(() => setNonce((value) => value + 1), []),
  };
}

/** The outcome of one mutation, and the function that starts the next. */
export interface Mutation<T> {
  readonly saving: boolean;
  readonly failure: VoiceApiError | VoiceInputError | null;
  readonly lastSaved: T | null;
  readonly run: (action: () => Promise<T>) => Promise<void>;
  readonly clear: () => void;
}

/**
 * One mutation's state.
 *
 * A mutation is never fire-and-forget here: `run` returns a promise the caller can await, and the
 * state it sets afterwards always describes this mutation rather than whichever one happened to
 * finish last. That is what keeps a failed save's refusal from being overwritten by a stale success.
 */
export function useVoiceMutation<T>(): Mutation<T> {
  const [saving, setSaving] = useState(false);
  const [failure, setFailure] = useState<VoiceApiError | VoiceInputError | null>(null);
  const [lastSaved, setLastSaved] = useState<T | null>(null);

  const run = useCallback(async (action: () => Promise<T>) => {
    setSaving(true);
    setFailure(null);

    try {
      const answer = await action();
      setLastSaved(answer);
      setSaving(false);
    } catch (thrown: unknown) {
      if (isAbort(thrown)) {
        setSaving(false);
        return;
      }

      setFailure(asFailure(thrown));
      setSaving(false);
    }
  }, []);

  // Dismisses a refusal without touching what the owner typed, so correcting and retrying does not
  // mean retyping.
  const clear = useCallback(() => setFailure(null), []);

  return { saving, failure, lastSaved, run, clear };
}

/** The state of a check that has not been run yet. */
export interface CheckState {
  readonly result: VoiceCheck | null;
  readonly checking: boolean;
  readonly failure: VoiceApiError | null;
  readonly run: (title: string, body: string, projectId: string) => Promise<void>;
  readonly clear: () => void;
}

/**
 * Runs the stateless voice check and holds the answer.
 *
 * A finding is held exactly as the server produced it: the offset, the length, the excerpt and the
 * term that matched. This hook never rewrites the text it was given, never trims a finding's span
 * and never offers a replacement - the owner sees where a rule fired and decides what to write.
 */
export function useVoiceCheck(
  check: (projectId: string, title: string, body: string, signal: AbortSignal) => Promise<VoiceCheck>,
): CheckState {
  const [result, setResult] = useState<VoiceCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [failure, setFailure] = useState<VoiceApiError | null>(null);
  // One check at a time: a second run abandons the first rather than racing it, so a slow answer for
  // older text cannot land on top of a newer one.
  const inFlight = useRef<AbortController | null>(null);

  const run = useCallback(
    async (title: string, body: string, projectId: string) => {
      inFlight.current?.abort();

      const controller = new AbortController();
      inFlight.current = controller;

      setChecking(true);
      setFailure(null);

      try {
        const answer = await check(projectId, title, body, controller.signal);
        setResult(answer);
        setChecking(false);
      } catch (thrown: unknown) {
        if (isAbort(thrown)) {
          return;
        }

        setFailure(asFailure(thrown));
        setChecking(false);
      }
    },
    [check],
  );

  const clear = useCallback(() => {
    inFlight.current?.abort();
    inFlight.current = null;
    setResult(null);
    setFailure(null);
  }, []);

  return { result, checking, failure, run, clear };
}

/** One draft and the functions that change it. */
export interface DraftLoaded {
  readonly draft: VoiceDraft | null;
  readonly loading: boolean;
  readonly failure: VoiceApiError | null;
  readonly reload: () => void;
}

/** Reads one draft, with the voice it was created under and the findings for its stored text. */
export function useVoiceDraft(
  read: (draftId: string, signal: AbortSignal) => Promise<VoiceDraft>,
  draftId: string,
): DraftLoaded {
  const [draft, setDraft] = useState<VoiceDraft | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<VoiceApiError | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);

    read(draftId, controller.signal)
      .then((answer) => {
        if (active) {
          setDraft(answer);
          setFailure(null);
        }
      })
      .catch((thrown: unknown) => {
        if (active && !isAbort(thrown)) {
          setDraft(null);
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draftId, nonce]);

  return { draft, loading, failure, reload: useCallback(() => setNonce((value) => value + 1), []) };
}

/** The state of a project's draft list. */
export interface DraftPageLoaded {
  readonly page: VoiceDraftPage | null;
  readonly loading: boolean;
  readonly failure: VoiceApiError | null;
  readonly reload: () => void;
}

/**
 * Reads a project's drafts while the area is open.
 *
 * The read is skipped entirely while `active` is false, because a list nobody can see is a round trip
 * for nothing; and it is skipped while the project id is empty, so a project that has not loaded
 * cannot produce a request for a project that does not exist. Neither case is reported as an error -
 * "not asked yet" is a state, not a failure.
 */
export function useVoiceDraftPage(
  read: (projectId: string, signal: AbortSignal) => Promise<VoiceDraftPage>,
  projectId: string,
  active: boolean,
): DraftPageLoaded {
  const [page, setPage] = useState<VoiceDraftPage | null>(null);
  const [loading, setLoading] = useState(false);
  const [failure, setFailure] = useState<VoiceApiError | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    if (!active || projectId.length === 0) {
      return;
    }

    const controller = new AbortController();
    let alive = true;

    setLoading(true);

    read(projectId, controller.signal)
      .then((answer) => {
        if (alive) {
          setPage(answer);
          setFailure(null);
        }
      })
      .catch((thrown: unknown) => {
        if (alive && !isAbort(thrown)) {
          setPage(null);
          setFailure(asFailure(thrown));
        }
      })
      .finally(() => {
        if (alive) {
          setLoading(false);
        }
      });

    return () => {
      alive = false;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, active, nonce]);

  return {
    page,
    loading,
    failure,
    reload: useCallback(() => setNonce((value) => value + 1), []),
  };
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof Error && thrown.name === 'AbortError';
}

function asFailure(thrown: unknown): VoiceApiError {
  if (thrown instanceof VoiceApiError) {
    return thrown;
  }

  return new VoiceApiError(
    {
      code: 'internal_error',
      message:
        thrown instanceof Error && thrown.message.length > 0
          ? thrown.message
          : 'The request could not be completed.',
      traceId: '',
    },
    0,
  );
}
