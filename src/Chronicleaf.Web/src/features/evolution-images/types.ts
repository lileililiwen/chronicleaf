/**
 * The wire shapes of the evolution image routes, read from the
 * screenshot-evolution-images contracts rather than guessed.
 *
 * A comparison depicts only selected screenshots, and the manifest cites the
 * media and caption revisions every cell was built from. The PNG itself lives
 * behind the preview and download routes, so this shape never carries pixels,
 * a storage key or a scope.
 */
import type { ApiFailure } from '../background-jobs/types';

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class EvolutionImageApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(`${failure.code}: ${failure.message}`);
    this.name = 'EvolutionImageApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}

/** One rendered comparison, exactly as the evolution routes publish it. */
export interface EvolutionImage {
  readonly id: string;
  readonly projectId: string;
  readonly layout: string;
  readonly manifest: unknown;
  readonly selectionHash: string;
  readonly artifactSha256: string;
  readonly width: number;
  readonly height: number;
  readonly status: string;
  readonly selectionCurrent: boolean;
  readonly created: boolean;
}

/** The listed comparisons, as an object so an empty project is a renderable answer. */
export interface EvolutionImageList {
  readonly items: readonly EvolutionImage[];
}

/** What a render POST carries: the compared milestones, the layout, and the reviewed hash. */
export interface RenderEvolutionImage {
  readonly milestoneIds: readonly string[];
  readonly layout: string;
  readonly selectedScreenshotIds?: readonly string[];
  readonly expectedSelectionHash?: string;
  readonly idempotencyKey?: string;
}
