/**
 * The wire shapes of the grounded-draft routes, read from the server's own contracts rather than guessed.
 *
 * <b>What this module may hold.</b> An asset's identity, the body the server stored, the claims that body
 * cites and the two kinds of finding a reviewer has to look at. `bodyMarkdown` is a draft an owner has
 * not approved, which is exactly why it is here and why nothing in this client ever writes it: the panel
 * is a reader, and the review package will be the one that edits text.
 *
 * <b>What it deliberately does not do.</b> It does not decide whether a claim is true, it does not
 * render the Markdown as HTML, and it does not hold anything that could act. The body is rendered as
 * source in a `<pre>`, so a draft's raw HTML is never executed by a page that is about to ask an owner
 * to believe what it says.
 */

import type { ApiFailure, Operation } from '../background-jobs/types';
import type { AiProviderCatalogue, GenerationPreview } from '../llm-provider-contract/types';

/** The 202 a draft request answers with: a job to follow, and nothing else. */
export interface DraftAccepted {
  readonly operationId: string;
  readonly operationUrl: string;
  readonly status: string;
  /** False when this request replayed a job an earlier identical request already created. */
  readonly created: boolean;
}

/** One claim of the stored summary, under the footnote label the body cites it by. */
export interface ContentClaim {
  /** The footnote label, for example `c1`. The caret is Markdown's sigil and is not part of the id. */
  readonly id: string;
  /** Which summary section the claim came from, as the server names it. */
  readonly section: string;
  /** The claim's position inside that section, from zero. */
  readonly index: number;
  readonly text: string;
  /** The evidence items this claim cites, as GUIDs. */
  readonly evidenceIds: readonly string[];
}

/**
 * One grounding finding.
 *
 * A finding is a report and never a correction: nothing here has changed the text, and no finding
 * carries a replacement value. The rule names are a closed set the server owns, and each one means a
 * different thing to a person deciding whether to trust the draft - which is why the panel renders the
 * rule in words rather than reducing the list to a count.
 */
export interface GroundingFinding {
  readonly rule: GroundingRule;
  readonly section: string;
  readonly index: number;
  readonly evidenceId: string | null;
  readonly excerpt: string;
  readonly message: string;
}

/** The closed set of grounding rules the server reports. */
export type GroundingRule =
  | 'claim_unreferenced'
  | 'claim_cited_unavailable'
  | 'claim_evidence_missing';

/**
 * One delivered voice finding, measured against the voice the draft was written with.
 *
 * `start` and `length` address a run in the stored field text, so a panel can select exactly the span
 * rather than searching for the excerpt. The same convention the delivered voice panel uses.
 */
export interface VoiceFinding {
  readonly rule: string;
  readonly field: string;
  readonly start: number;
  readonly length: number;
  readonly excerpt: string;
  readonly message: string;
}

/**
 * One asset with its head revision, its claims and both kinds of finding.
 *
 * Every field is nullable because "this milestone has no draft yet" is a normal state on a fresh
 * installation and the route answers 200 with a null asset rather than a 404. A panel therefore has to
 * render the absent case, and making the absent case expressible is what stops it from treating one as
 * a failure.
 */
export interface ContentAsset {
  readonly assetId: string | null;
  readonly type: string | null;
  /** Always `draft`. There is no approved value yet, and this client has no control that could set one. */
  readonly status: string | null;
  readonly currentRevision: number | null;
  readonly updatedAt: string | null;
  readonly title: string | null;
  readonly summary: string | null;
  readonly bodyMarkdown: string | null;
  /** Lowercase SHA-256 over the stored body, so a reader can tell two revisions apart. */
  readonly bodyHash: string | null;
  readonly evidenceBundleId: string | null;
  readonly evidenceRevision: number | null;
  readonly voiceRevision: number | null;
  /** The voice the stored text was written under, not the profile's current fingerprint. */
  readonly voiceFingerprint: string | null;
  readonly providerId: string | null;
  readonly model: string | null;
  readonly promptVersion: string | null;
  readonly schemaVersion: string | null;
  readonly createdAt: string | null;
  readonly claims: readonly ContentClaim[];
  readonly groundingFindings: readonly GroundingFinding[];
  readonly voiceFindings: readonly VoiceFinding[];
}

/** What this panel needs from the delivered generation handshake to ask for a draft. */
export interface DraftAuthorization {
  readonly providers: AiProviderCatalogue;
  readonly preview: GenerationPreview;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class DraftApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'DraftApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /**
   * The milestone is not confirmed, so nothing was queued.
   *
   * A different answer from a grounding refusal and a different remedy: this one is a single click on
   * the milestone itself, and the panel has to say so rather than implying the evidence was the problem.
   */
  get isMilestoneNotConfirmed(): boolean {
    return this.code === 'draft_milestone_not_confirmed';
  }

  /**
   * Nothing in the selected evidence was collected, so there was nothing factual to draft from.
   *
   * Its own message rather than this one guessing: the server already names the remedy, and repeating it
   * here is how a client and a server end up disagreeing about what an owner should do.
   */
  get isNoGrounding(): boolean {
    return this.code === 'draft_no_grounding';
  }

  /** The consent no longer covers the input, so the owner authorizes it again. */
  get isConsentStale(): boolean {
    return this.code === 'consent_stale';
  }

  /** The first message recorded for a field, for showing next to that field. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}

/**
 * The job's stored failure codes, and what each one means to a person reading this panel.
 *
 * These are a closed set the server writes to the operation row, and a panel that shows a code without
 * saying what it means is asking the owner to guess. The wording below is deliberately plainer than the
 * code: a reader who cannot act on it does not have a use for the precision.
 */
export const DRAFT_JOB_CODES = {
  /**
   * The structured summary did not satisfy its schema, and the one permitted repair did not either.
   *
   * Two provider calls were spent and the second is the reason this is terminal: the design permits
   * exactly one repair, and a third call would be a third thing the owner may be billed for on the same
   * broken answer.
   */
  invalidSummary: 'invalid_summary',
  /** A claim cited an evidence id that is not in the frozen snapshot. Nothing was drafted. */
  unsupportedReference: 'content_draft_unsupported_reference',
  /** A claim, or the body, carried a figure that appears in no evidence item. */
  fabricatedMetric: 'content_draft_fabricated_metric',
  /** The body cited a footnote that is not one of this milestone's claims, or left one undefined. */
  citationUnknown: 'content_draft_citation_unknown',
  /** The body dropped a known limitation the summary had recorded. */
  limitationDropped: 'content_draft_limitation_dropped',
  /** The body carried no citation at all, so nothing in it is grounded. */
  ungroundedBody: 'content_draft_ungrounded_body',
  /** The body was over the 100 KiB Markdown bound. */
  bodyTooLarge: 'content_draft_body_too_large',
  /**
   * The evidence, the voice or the milestone moved while the job ran, so nothing was committed.
   *
   * The previous draft is untouched, which is why this is not an error in the sense of "something is
   * broken" - it is the design's own answer to "the input you authorized is no longer current".
   */
  superseded: 'content_draft_superseded',
  /** The provider declined, or answered with something unusable. Never a draft. */
  providerRefused: 'content_draft_provider_refused',
  providerInvalid: 'content_draft_provider_invalid',
  /** The provider rejected the stored credential, and will again until it is replaced. */
  credentialRejected: 'content_draft_credential_rejected',
  /** No adapter is installed for the provider the preview names, so the draft could not start. */
  adapterUnavailable: 'content_draft_adapter_unavailable',
} as const;

/**
 * What a stored job code means, in words an owner can act on, or `null` for a code this build does not
 * know - which the panel then shows verbatim rather than inventing a meaning for.
 */
export function explainDraftFailure(code: string | null | undefined): string | null {
  switch (code) {
    case DRAFT_JOB_CODES.invalidSummary:
      return 'The provider did not return a summary in the agreed shape, and the one repair this system is allowed to make did not fix it. Two calls were spent and nothing was drafted - asking again would spend a third.';
    case DRAFT_JOB_CODES.unsupportedReference:
      return 'The provider cited an evidence item that is not part of the evidence you authorized. That is either a mistake or something the system will not write down, so no draft was stored.';
    case DRAFT_JOB_CODES.fabricatedMetric:
      return 'The answer contained a figure that appears in none of the evidence for this milestone. This system does not write statistics it cannot trace, so nothing was drafted.';
    case DRAFT_JOB_CODES.citationUnknown:
      return 'The draft cited something that is not one of this milestone\u2019s claims, or left a citation undefined. The body cannot be checked against the evidence, so it was not stored.';
    case DRAFT_JOB_CODES.limitationDropped:
      return 'The draft left out a limitation the summary had recorded. A draft that quietly drops a caveat is worse than no draft, so nothing was stored.';
    case DRAFT_JOB_CODES.ungroundedBody:
      return 'The draft carried no citation at all, so nothing in it could be traced to your evidence. Nothing was stored.';
    case DRAFT_JOB_CODES.bodyTooLarge:
      return 'The draft was larger than 100 KiB of Markdown, so it was not stored.';
    case DRAFT_JOB_CODES.superseded:
      return 'The evidence, the voice or the milestone changed while the draft was being written, so nothing was committed. The draft you already have is unchanged. Read the current state and ask again.';
    case DRAFT_JOB_CODES.providerRefused:
      return 'The provider declined to write this draft. Nothing was stored.';
    case DRAFT_JOB_CODES.providerInvalid:
      return 'The provider\u2019s answer could not be used. Nothing was stored.';
    case DRAFT_JOB_CODES.credentialRejected:
      return 'The provider rejected the stored credential. Replace it in the credential settings and ask again.';
    case DRAFT_JOB_CODES.adapterUnavailable:
      return 'No adapter is installed for the provider this preview names, so nothing could be generated. The preview itself is still valid.';
    default:
      return null;
  }
}

/** True when the job stopped without storing anything and the previous draft is intact. */
export function isSuperseded(operation: Operation | null): boolean {
  return operation?.errorCode === DRAFT_JOB_CODES.superseded;
}

/** The operation statuses a job can still move out of, mirroring the sibling panels' own list. */
export const ACTIVE_OPERATION_STATUSES = ['queued', 'running', 'retrying'] as const;
