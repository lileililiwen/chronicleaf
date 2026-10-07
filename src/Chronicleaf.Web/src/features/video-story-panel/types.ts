import type { ApiFailure } from '../background-jobs/types';

/** The runtimes a storyboard may cover, in seconds. The server refuses any other value. */
export const VIDEO_STORY_DURATIONS = [15, 30, 60] as const;

export type VideoStoryDuration = (typeof VIDEO_STORY_DURATIONS)[number];

/** How one shot hands off to the next. The server refuses any other value. */
export const VIDEO_STORY_TRANSITIONS = ['cut', 'fade'] as const;

export type VideoStoryTransition = (typeof VIDEO_STORY_TRANSITIONS)[number];

/** The shortest shot the server stores, in milliseconds. The composer never offers a shorter split. */
export const VIDEO_STORY_MIN_SHOT_MS = 500;

/** How many shots one script may hold. The server refuses a longer list. */
export const VIDEO_STORY_MAX_SHOTS = 30;

/** One milestone's video-story asset, as the discovery read answers it. */
export interface VideoStorySummary {
  readonly assetId: string | null;
  readonly revision: number;
  readonly bodyHash: string;
  readonly durationSeconds: number;
  readonly shotCount: number;
  readonly evidenceRevision: number;
  readonly voiceRevision: number;
  readonly updatedAt: string;
}

/** One shot the owner composes, before the server derives its timings. */
export interface ComposedShot {
  readonly key: string;
  readonly screenshotMediaId: string;
  readonly caption: string;
  readonly narration: string;
  readonly transition: VideoStoryTransition;
  readonly sourceEvidenceIds: readonly string[];
}

/** One stored shot, as the canonical storyboard JSON carries it. */
export interface StoredShot {
  readonly startMs: number;
  readonly endMs: number;
  readonly screenshotMediaId: string;
  readonly caption: string;
  readonly narration: string;
  readonly transition: string;
  readonly sourceEvidenceIds: readonly string[];
}

/** One stored storyboard, as the canonical JSON carries it. Null when the bytes are not one. */
export interface StoredStoryboard {
  readonly durationSeconds: number;
  readonly shots: readonly StoredShot[];
  readonly limitations: readonly string[];
}

/** One scripted video story, as the generate route answers it. */
export interface GeneratedVideoStory {
  readonly assetId: string;
  readonly revision: number;
  readonly bodyHash: string;
  readonly durationSeconds: number;
  readonly shotCount: number;
  readonly totalMs: number;
  readonly sourceAssetId: string;
  readonly sourceRevision: number;
  readonly evidenceRevision: number;
  readonly voiceRevision: number;
  readonly imageOrder: readonly string[];
}

/**
 * Reads a stored storyboard back. Anything the server did not write as
 * canonical JSON is unreadable, not a story — and the panel says so rather
 * than drawing half a script.
 */
export function parseStoryboard(body: string | null): StoredStoryboard | null {
  if (body === null || body.trim() === '') {
    return null;
  }

  let parsed: unknown;

  try {
    parsed = JSON.parse(body) as unknown;
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }

  const root = parsed as Record<string, unknown>;

  if (root['format'] !== 'video-story' || typeof root['durationSeconds'] !== 'number') {
    return null;
  }

  if (!Array.isArray(root['shots'])) {
    return null;
  }

  const shots: StoredShot[] = [];

  for (const entry of root['shots'] as readonly unknown[]) {
    if (typeof entry !== 'object' || entry === null) {
      return null;
    }

    const shot = entry as Record<string, unknown>;

    if (
      typeof shot['startMs'] !== 'number' ||
      typeof shot['endMs'] !== 'number' ||
      typeof shot['screenshotMediaId'] !== 'string' ||
      typeof shot['caption'] !== 'string' ||
      typeof shot['narration'] !== 'string' ||
      typeof shot['transition'] !== 'string' ||
      !Array.isArray(shot['sourceEvidenceIds']) ||
      !(shot['sourceEvidenceIds'] as readonly unknown[]).every((id) => typeof id === 'string')
    ) {
      return null;
    }

    shots.push({
      startMs: shot['startMs'] as number,
      endMs: shot['endMs'] as number,
      screenshotMediaId: shot['screenshotMediaId'] as string,
      caption: shot['caption'] as string,
      narration: shot['narration'] as string,
      transition: shot['transition'] as string,
      sourceEvidenceIds: shot['sourceEvidenceIds'] as readonly string[],
    });
  }

  const limitations = Array.isArray(root['limitations'])
    ? (root['limitations'] as readonly unknown[]).filter(
        (limitation): limitation is string => typeof limitation === 'string',
      )
    : [];

  return {
    durationSeconds: root['durationSeconds'] as number,
    shots,
    limitations,
  };
}

/**
 * Derives exact contiguous timings for N shots over the runtime: an even
 * split with the leftover milliseconds dealt one each to the earliest shots,
 * so the first starts at zero, neighbours meet exactly, and the last ends
 * the runtime — the coverage the server demands.
 */
export function deriveTimings(
  durationSeconds: VideoStoryDuration,
  shotCount: number,
): readonly { readonly startMs: number; readonly endMs: number }[] {
  const totalMs = durationSeconds * 1000;
  const base = Math.floor(totalMs / shotCount);
  const remainder = totalMs - base * shotCount;

  const timings: { startMs: number; endMs: number }[] = [];
  let cursor = 0;

  for (let index = 0; index < shotCount; index += 1) {
    const length = base + (index < remainder ? 1 : 0);
    timings.push({ startMs: cursor, endMs: cursor + length });
    cursor += length;
  }

  return timings;
}

/** Formats milliseconds as an SRT-style timestamp, so timings read as time rather than counts. */
export function formatTimestamp(totalMs: number): string {
  const hours = Math.floor(totalMs / 3_600_000);
  const minutes = Math.floor((totalMs % 3_600_000) / 60_000);
  const seconds = Math.floor((totalMs % 60_000) / 1000);
  const millis = totalMs % 1000;

  const pad = (value: number, width: number) => String(value).padStart(width, '0');
  return `${pad(hours, 2)}:${pad(minutes, 2)}:${pad(seconds, 2)},${pad(millis, 3)}`;
}

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class VideoStoryApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(failure.message);
    this.name = 'VideoStoryApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }

  /** The first message the server attached to this field, if it named one. */
  messageFor(field: string): string | null {
    return this.fieldErrors[field]?.[0] ?? null;
  }
}
