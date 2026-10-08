import { useEffect, useState } from 'react';

import { readPublicJourney } from './api';
import { JourneyApiError, type JourneyPublic } from './types';

/**
 * The anonymous public journey view (FEATURE-TRIGGER-PANELS-006, public half).
 *
 * This view carries no session and stores nothing: it reads only the public
 * projection route and shows only what that route returns. An unknown, revoked
 * or lapsed slug is the route's own 404 page, not a redirect and not a guess.
 * A cached copy would outlive the revocation that suspends the route, so this
 * view keeps no copy beyond the rendered answer - and downloaded copies cannot
 * be recalled, which the page says.
 */
export function PublicJourneyView({ slug }: { readonly slug: string }) {
  const [journey, setJourney] = useState<JourneyPublic | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<JourneyApiError | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);
    setJourney(null);
    setFailure(null);

    readPublicJourney(slug, controller.signal)
      .then((view) => {
        if (active) {
          setJourney(view);
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
  }, [slug]);

  if (loading) {
    return (
      <section className="view" data-testid="public-journey">
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="public-journey-loading">
          Reading this journey…
        </p>
      </section>
    );
  }

  if (failure !== null || journey === null) {
    return (
      <section className="view" data-testid="public-journey">
        <h2 className="view__heading" data-testid="public-journey-missing-heading">
          No journey here
        </h2>
        <p className="view__state" data-testid="public-journey-missing">
          {failure?.message ?? 'Nothing is published at this address.'} Nothing was changed:
          this page only reads what a published journey serves.
        </p>
      </section>
    );
  }

  return (
    <section className="view" data-testid="public-journey">
      <h2 className="view__heading" data-testid="public-journey-project">
        {journey.projectName}
      </h2>
      <p className="view__state" data-testid="public-journey-revision">
        Revision {journey.revision}. A saved copy of this page outlives a revocation, which
        cannot be recalled.
      </p>

      {journey.entries.length === 0 ? (
        <p className="view__state" data-testid="public-journey-empty">
          This journey holds no entries.
        </p>
      ) : (
        <ol className="milestones" data-testid="public-journey-entries">
          {journey.entries.map((entry, index) => (
            <li key={`${entry.milestoneTitle}-${index}`} className="milestone" data-testid="public-journey-entry">
              <h3 className="milestone__title" data-testid="public-journey-title">
                {entry.milestoneTitle}
              </h3>
              <p className="milestone__meta" data-testid="public-journey-occurred-at">
                {entry.occurredAt}
              </p>
              <p className="milestone__notes" data-testid="public-journey-body">
                {entry.bodyMarkdown}
              </p>
              {entry.pictures.length > 0 ? (
                <ul data-testid="public-journey-pictures">
                  {entry.pictures.map((picture) => (
                    <li key={picture.mediaUrl} data-testid="public-journey-picture">
                      <img src={picture.mediaUrl} alt={picture.caption} />
                      <p data-testid="public-journey-caption">{picture.caption}</p>
                    </li>
                  ))}
                </ul>
              ) : null}
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}

function asFailure(thrown: unknown): JourneyApiError | null {
  return thrown instanceof JourneyApiError ? thrown : null;
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
