import { useCallback, useEffect, useRef, useState } from 'react';
import {
  createMilestonePreview,
  generate,
  GenerationApiError,
  readEvidenceHead,
  readProviders,
  resolveSettings,
} from './api';
import type {
  AiProviderCatalogue,
  AiSettingsResolution,
  EvidenceItemSummary,
  GenerationPreview,
  GenerationRun,
} from './types';

/**
 * The whole consent flow, as one hook.
 *
 * The step order is the requirement, not a UI preference: evidence is read, a provider is chosen, a
 * redacted snapshot is frozen, the owner reads it and only then may call. The hook keeps them in that
 * order and refuses to move on without what each step produced, so a surface cannot accidentally let
 * a "generate" button appear before there is a consent hash to send.
 *
 * Nothing here is a cache of a generation result. A run is held while the owner is looking at it and
 * dropped when the preview is discarded or replaced, because a draft that outlives the call that
 * produced it is a draft nobody reviewed.
 */
export interface GenerationFlow {
  readonly catalogue: AiProviderCatalogue | null;
  readonly catalogueError: string | null;
  readonly catalogueBusy: boolean;

  readonly resolution: AiSettingsResolution | null;
  readonly resolutionError: string | null;
  readonly resolutionBusy: boolean;
  checkSettings: (input: {
    readonly providerId: string;
    readonly model: string;
    readonly credentialRef: string | null;
    readonly endpointRef?: string | null;
  }) => Promise<void>;

  readonly evidenceRevision: number;
  readonly evidenceItems: readonly EvidenceItemSummary[];
  readonly evidenceError: string | null;
  readonly evidenceBusy: boolean;
  readonly selectedEvidenceIds: readonly string[];
  toggleEvidence: (id: string) => void;

  readonly preview: GenerationPreview | null;
  readonly previewError: string | null;
  readonly previewBusy: boolean;
  createPreview: (input: {
    readonly providerId: string;
    readonly model: string;
    readonly credentialRef: string | null;
  }) => Promise<void>;
  discardPreview: () => void;

  readonly run: GenerationRun | null;
  readonly runError: string | null;
  readonly runBusy: boolean;
  runGeneration: () => Promise<void>;
}

export function useGeneration(milestoneId: string): GenerationFlow {
  const [catalogue, setCatalogue] = useState<AiProviderCatalogue | null>(null);
  const [catalogueError, setCatalogueError] = useState<string | null>(null);
  const [catalogueBusy, setCatalogueBusy] = useState(false);

  const [resolution, setResolution] = useState<AiSettingsResolution | null>(null);
  const [resolutionError, setResolutionError] = useState<string | null>(null);
  const [resolutionBusy, setResolutionBusy] = useState(false);

  const [evidenceRevision, setEvidenceRevision] = useState(0);
  const [evidenceItems, setEvidenceItems] = useState<readonly EvidenceItemSummary[]>([]);
  const [evidenceError, setEvidenceError] = useState<string | null>(null);
  const [evidenceBusy, setEvidenceBusy] = useState(false);
  const [selectedEvidenceIds, setSelectedEvidenceIds] = useState<readonly string[]>([]);

  const [preview, setPreview] = useState<GenerationPreview | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);

  const [run, setRun] = useState<GenerationRun | null>(null);
  const [runError, setRunError] = useState<string | null>(null);
  const [runBusy, setRunBusy] = useState(false);

  // Held so a milestone change or an unmount aborts whatever is in flight rather than letting a
  // response for the previous milestone land in this one's state.
  const inFlight = useRef<AbortController | null>(null);

  const loadCatalogue = useCallback(async (signal?: AbortSignal) => {
    setCatalogueBusy(true);
    setCatalogueError(null);

    try {
      setCatalogue(await readProviders(signal));
    } catch (failure) {
      setCatalogueError(describe(failure));
    } finally {
      setCatalogueBusy(false);
    }
  }, []);

  const loadEvidence = useCallback(
    async (signal?: AbortSignal) => {
      setEvidenceBusy(true);
      setEvidenceError(null);

      try {
        const head = await readEvidenceHead(milestoneId, signal);
        setEvidenceRevision(head.revision);
        setEvidenceItems(head.items);
        setSelectedEvidenceIds((current) =>
          current.filter((id) => head.items.some((item) => item.id === id)),
        );
      } catch (failure) {
        setEvidenceError(describe(failure));
      } finally {
        setEvidenceBusy(false);
      }
    },
    [milestoneId],
  );

  useEffect(() => {
    const controller = new AbortController();
    inFlight.current = controller;

    void loadCatalogue(controller.signal);
    void loadEvidence(controller.signal);

    return () => {
      controller.abort();
      inFlight.current = null;
    };
  }, [loadCatalogue, loadEvidence]);

  // The evidence panel above this one appends to the same milestone, and an append is how an owner
  // most often adds the material a draft is meant to be written from. Reading the head once on mount
  // would leave this panel claiming "no evidence yet" beside a list that already has one, so it
  // keeps itself current instead.
  //
  // Bounded and cheap rather than event-driven: evidence belongs to another feature's panel, this one
  // does not own that state, and the interval is short, stops while the tab is hidden, and is torn
  // down with the panel. It stops at the same moment the panel does, so no request outlives the
  // milestone it was reading.
  useEffect(() => {
    const refresh = () => {
      if (document.visibilityState === 'visible') {
        void loadEvidence();
      }
    };

    const timer = window.setInterval(refresh, 4000);
    window.addEventListener('focus', refresh);
    document.addEventListener('visibilitychange', refresh);

    return () => {
      window.clearInterval(timer);
      window.removeEventListener('focus', refresh);
      document.removeEventListener('visibilitychange', refresh);
    };
  }, [loadEvidence]);

  useEffect(() => {
    setPreview(null);
    setPreviewError(null);
    setRun(null);
    setRunError(null);
    setResolution(null);
    setResolutionError(null);
    setSelectedEvidenceIds([]);
  }, [milestoneId]);

  const checkSettings = useCallback(
    async (input: {
      readonly providerId: string;
      readonly model: string;
      readonly credentialRef: string | null;
      readonly endpointRef?: string | null;
    }) => {
      setResolutionBusy(true);
      setResolutionError(null);

      try {
        setResolution(await resolveSettings(input));
      } catch (failure) {
        setResolution(null);
        setResolutionError(describe(failure));
      } finally {
        setResolutionBusy(false);
      }
    },
    [],
  );

  const toggleEvidence = useCallback((id: string) => {
    setSelectedEvidenceIds((current) =>
      current.includes(id) ? current.filter((value) => value !== id) : [...current, id],
    );
  }, []);

  const createPreview = useCallback(
    async (input: {
      readonly providerId: string;
      readonly model: string;
      readonly credentialRef: string | null;
    }) => {
      setPreviewBusy(true);
      setPreviewError(null);
      setResolutionError(null);
      setRun(null);
      setRunError(null);


      try {
        // Resolved first, and the ordering is the point: an unknown model, a provider with no
        // adapter and a provider that needs a credential the owner has not stored are all refused
        // before a redacted snapshot exists to authorize - so the owner finds out before there is
        // anything on screen that looks like it is about to be sent.
        setResolution(await resolveSettings(input));
      } catch (failure) {
        // The settings step owns its own message, and it is a different message from the preview's:
        // this one is about a selection that will not work, the other is about an input that will not
        // be accepted. Reporting both under one heading would leave an owner unable to tell which of
        // their two choices was the problem.
        setResolution(null);
        setPreview(null);
        setResolutionError(describe(failure));
        setPreviewBusy(false);
        return;
      }

      try {
        setPreview(
          await createMilestonePreview({
            milestoneId,
            providerId: input.providerId,
            model: input.model,
            credentialRef: input.credentialRef,
            evidenceRevision,
            selectedEvidenceIds,
          }),
        );
      } catch (failure) {
        setPreview(null);
        setPreviewError(describe(failure));
      } finally {
        setPreviewBusy(false);
      }
    },
    [evidenceRevision, milestoneId, selectedEvidenceIds],
  );

  const discardPreview = useCallback(() => {
    // The consent hash leaves the browser with the preview. It authorized one call inside a window
    // the server enforces, and nothing here needs it a second time.
    setPreview(null);
    setPreviewError(null);
    setRun(null);
    setRunError(null);
  }, []);

  const runGeneration = useCallback(async () => {
    if (preview === null) {
      return;
    }

    setRunBusy(true);
    setRunError(null);

    try {
      setRun(await generate({ previewId: preview.previewId, consentHash: preview.consentHash }));
    } catch (failure) {
      setRun(null);
      setRunError(describe(failure));

      // A consent that no longer covers the input is not something the owner can retry their way out
      // of, and the evidence may have moved. Re-read it so the next preview is against what is there
      // now rather than against what was on screen when the consent was taken.
      if (failure instanceof GenerationApiError && failure.isConsentStale) {
        await loadEvidence();
        setPreview(null);
      }
    } finally {
      setRunBusy(false);
    }
  }, [loadEvidence, preview]);

  return {
    catalogue,
    catalogueError,
    catalogueBusy,
    resolution,
    resolutionError,
    resolutionBusy,
    checkSettings,
    evidenceRevision,
    evidenceItems,
    evidenceError,
    evidenceBusy,
    selectedEvidenceIds,
    toggleEvidence,
    preview,
    previewError,
    previewBusy,
    createPreview,
    discardPreview,
    run,
    runError,
    runBusy,
    runGeneration,
  };
}

/**
 * A refusal in the owner's words.
 *
 * The envelope's message is used as-is when there is one. It is authored on the server, it names no
 * provider body and no path, and rewriting it here would be the one place a provider's own wording
 * could reach the page.
 */
function describe(failure: unknown): string {
  if (failure instanceof GenerationApiError) {
    return failure.message;
  }

  if (failure instanceof Error) {
    return failure.message;
  }

  return 'The request could not be completed.';
}
