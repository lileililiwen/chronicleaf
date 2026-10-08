import { useCallback, useEffect, useState } from 'react';

import { listTimeline } from './api';
import { TimelineApiError } from './types';
import type { TimelineItem } from './types';

/**
 * The private chronological project timeline (MILESTONE-TIMELINE-001/002).
 *
 * Four states are visible here and each is shown as what it is. A project with no confirmed
 * history says so rather than drawing an empty line. Confirmed records arrive oldest first under
 * the date they happened on, so the history reads from the beginning. Candidates are hidden until
 * the owner asks for them, and then they are labeled as candidates and never as confirmed,
 * because the label is the stored status rather than something this view assigns. Selecting a row
 * opens the milestone editor for that record, because the row's id is the milestone's own id.
 *
 * The instants arrive as UTC and are shown in the reader's own zone, with the UTC value on the
 * title: the API speaks UTC and the browser knows the locale. A timeline position the server did
 * not issue for this project is refused by the server and shown as its refusal, never applied to
 * the wrong history.
 */
export function ProjectTimeline({
  projectId,
  onEditMilestone,
}: {
  readonly projectId: string;
  readonly onEditMilestone: (projectId: string, milestoneId: string) => void;
}) {
  const [showCandidates, setShowCandidates] = useState(false);
  const [items, setItems] = useState<readonly TimelineItem[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [failure, setFailure] = useState<TimelineApiError | null>(null);

  const read = useCallback(
    async (cursor: string | undefined, candidates: boolean, signal: AbortSignal) => {
      const page = await listTimeline(projectId, { cursor, includeCandidates: candidates }, signal);

      return page;
    },
    [projectId],
  );

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);
    setItems([]);
    setNextCursor(null);
    setFailure(null);

    read(undefined, showCandidates, controller.signal)
      .then((page) => {
        if (!active) {
          return;
        }

        setItems(page.items);
        setNextCursor(page.nextCursor);
      })
      .catch((thrown: unknown) => {
        if (!active || isAbort(thrown)) {
          return;
        }

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
  }, [read, showCandidates]);

  const more = async () => {
    if (nextCursor === null || loadingMore) {
      return;
    }

    const cursor = nextCursor;
    setLoadingMore(true);

    try {
      const page = await listTimeline(projectId, { cursor, includeCandidates: showCandidates });

      // Rows are appended, never merged: the cursor is the position of the last row already
      // seen, so the next page holds rows strictly after it and no id appears twice.
      const seen = new Set(items.map((item) => item.id));
      setItems([...items, ...page.items.filter((item) => !seen.has(item.id))]);
      setNextCursor(page.nextCursor);
    } catch (thrown) {
      setFailure(asFailure(thrown));
    } finally {
      setLoadingMore(false);
    }
  };

  return (
    <section className="timeline" aria-label="Project timeline" data-testid="project-timeline">
      {/* WEB-A11Y-ROLES-001. The timeline is an h2 section under the view h1. */}
      <h2 className="timeline__heading" data-testid="timeline-heading">
        Timeline
      </h2>

      <label className="timeline__toggle">
        <input
          type="checkbox"
          checked={showCandidates}
          onChange={(event) => setShowCandidates(event.target.checked)}
          data-testid="timeline-candidates-toggle"
        />
        Show candidates awaiting confirmation
      </label>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="timeline-loading">
          Loading the timeline…
        </p>
      ) : failure ? (
        <p className="notice notice--error" role="alert" data-testid="timeline-error">
          {failure.message}
        </p>
      ) : items.length === 0 ? (
        <div className="empty" data-testid="timeline-empty">
          <p className="empty__title">
            {showCandidates ? 'No confirmed milestones or candidates yet.' : 'No confirmed milestones yet.'}
          </p>
          <p className="empty__body">
            Nothing in this project has been confirmed, so there is no history to tell. What you
            record and confirm becomes the timeline a story is written from.
          </p>
        </div>
      ) : (
        <>
          <ol className="timeline__list" data-testid="timeline-list">
            {groupedByDay(items).map((group) => (
              <li key={group.day} className="timeline__day" data-testid="timeline-day">
                <h3 className="timeline__date" data-testid="timeline-date">
                  {group.label}
                </h3>
                <ol className="timeline__entries">
                  {group.items.map((item) => (
                    <li
                      key={item.id}
                      className="timeline__entry"
                      data-testid="timeline-entry"
                      data-status={item.status}
                    >
                      <button
                        type="button"
                        className="timeline__open"
                        onClick={() => onEditMilestone(projectId, item.id)}
                        data-testid="timeline-open"
                        title={`Open ${item.title} in the milestone editor`}
                      >
                        <span className="timeline__title" data-testid="timeline-title">
                          {item.title}
                        </span>{' '}
                        <span
                          className={
                            item.status === 'confirmed' ? 'badge badge--ok' : 'badge badge--pending'
                          }
                          data-testid="timeline-status"
                          aria-label={`Status: ${item.status}`}
                        >
                          {/* WEB-A11Y-ROLES-004. The visible status word plus a non-color accessible name. */}
                          {item.status}
                        </span>
                      </button>
                      <span className="timeline__meta">
                        {item.version ? (
                          <span data-testid="timeline-version">{item.version} · </span>
                        ) : null}
                        <span data-testid="timeline-type">{item.type}</span>
                        {' · '}
                        <time
                          dateTime={item.occurredAt}
                          title={item.occurredAt}
                          data-testid="timeline-time"
                        >
                          {formatInstant(item.occurredAt)}
                        </time>
                      </span>
                    </li>
                  ))}
                </ol>
              </li>
            ))}
          </ol>

          {nextCursor !== null ? (
            <button
              type="button"
              className="action"
              onClick={() => void more()}
              disabled={loadingMore}
              data-testid="timeline-more"
            >
              {loadingMore ? 'Loading…' : 'Show more'}
            </button>
          ) : null}
        </>
      )}
    </section>
  );
}

interface DayGroup {
  readonly day: string;
  readonly label: string;
  readonly items: readonly TimelineItem[];
}

/** Groups rows that happened on the same local day, keeping chronological order. */
function groupedByDay(items: readonly TimelineItem[]): readonly DayGroup[] {
  const groups: DayGroup[] = [];

  for (const item of items) {
    const date = new Date(item.occurredAt);
    const day = Number.isNaN(date.getTime()) ? item.occurredAt : startOfDayKey(date);
    const label = Number.isNaN(date.getTime())
      ? item.occurredAt
      : date.toLocaleDateString(undefined, { dateStyle: 'long' });
    const current = groups[groups.length - 1];

    if (current !== undefined && current.day === day) {
      groups[groups.length - 1] = { ...current, items: [...current.items, item] };
    } else {
      groups.push({ day, label, items: [item] });
    }
  }

  return groups;
}

function startOfDayKey(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

/** An ISO instant shown in the reader's own zone, with the UTC value on the title. */
function formatInstant(iso: string): string {
  const date = new Date(iso);

  if (Number.isNaN(date.getTime()) || iso.length === 0) {
    return iso;
  }

  return date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}

function asFailure(thrown: unknown): TimelineApiError {
  if (thrown instanceof TimelineApiError) {
    return thrown;
  }

  return new TimelineApiError(
    { code: 'internal_error', message: 'The timeline could not be read.', traceId: 'unknown' },
    0,
  );
}
