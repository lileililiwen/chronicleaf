/**
 * The wire shapes of the evolution GIF routes, read from the
 * evolution-gif-export contracts rather than guessed.
 *
 * An animation depicts only selected screenshots, in canvas order with the
 * playback the review authorized. The GIF itself lives behind the preview and
 * download routes, so this shape never carries pixels, a storage key or a
 * scope.
 */
import type { ApiFailure } from '../background-jobs/types';

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class EvolutionGifApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(`${failure.code}: ${failure.message}`);
    this.name = 'EvolutionGifApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}

/** One rendered animation, exactly as the evolution GIF routes publish it. */
export interface EvolutionGif {
  readonly id: string;
  readonly projectId: string;
  readonly manifest: unknown;
  readonly selectionHash: string;
  readonly artifactSha256: string;
  readonly width: number;
  readonly height: number;
  readonly frameCount: number;
  readonly totalDurationMs: number;
  readonly loop: boolean;
  readonly status: string;
  readonly selectionCurrent: boolean;
  readonly created: boolean;
}

/** The listed animations, as an object so an empty project is a renderable answer. */
export interface EvolutionGifList {
  readonly items: readonly EvolutionGif[];
}

/** What a GIF render POST carries: the animated milestones and the reviewed hash. */
export interface RenderEvolutionGif {
  readonly milestoneIds: readonly string[];
  readonly loop: boolean;
  readonly expectedSelectionHash?: string;
  readonly idempotencyKey?: string;
}
