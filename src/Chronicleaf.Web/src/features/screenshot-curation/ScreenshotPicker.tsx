import { useCallback, useEffect, useState } from 'react';

import { listEvidence } from '../milestone-evidence/api';
import { curateScreenshot, listCurated, orderScreenshots } from './api';
import {
  CURATION_CAPTION_MAX_LENGTH,
  CurationApiError,
  type CuratedScreenshot,
} from './types';

/**
 * What the owner chose about the captured pictures for one milestone
 * (SCREENSHOT-CURATION-001/002).
 *
 * <b>This panel's subject is a choice.</b> Capturing offers pictures; this panel is where an owner
 * says which of them are evidence. Selecting publishes the picture into the milestone's evidence
 * bundle in the same server transaction, rejecting marks a bad candidate without deleting its row,
 * and only a selected picture may be the primary one. A capture re-run never moves any of this:
 * new candidates arrive as candidates however the earlier ones were curated.
 *
 * <b>Every change is conditional twice.</b> The panel reads the picture's revision and the
 * evidence revision together and sends both back; when either moved first the server answers 409
 * and this panel reads again rather than resending. A conflict is therefore never drawn as a
 * failure of the choice - it is drawn as "read what the server holds now, then decide again".
 *
 * <b>What this panel does not do.</b> It does not capture, approve or export. A selected picture
 * is evidence a human may act on; approval is the review package's, and the order stored here is
 * the sequence the selected pictures are served in, not a published artifact.
 */
export function ScreenshotPicker({
  milestoneId,
  milestoneTitle,
  archived,
  onEvidenceChanged,
}: {
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  /** An archived project's pictures are kept and cannot be curated, so no control is offered. */
  readonly archived: boolean;
  /** Called after a change advanced the milestone's evidence, so the evidence panel re-reads. */
  readonly onEvidenceChanged: () => void;
}) {
  const [pictures, setPictures] = useState<readonly CuratedScreenshot[]>([]);
  const [mediaBaseUrl, setMediaBaseUrl] = useState('/api/media');
  const [bundleRevision, setBundleRevision] = useState(0);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [failure, setFailure] = useState<CurationApiError | null>(null);
  const [failedAction, setFailedAction] = useState<string | null>(null);
  const [brokenImages, setBrokenImages] = useState<readonly string[]>([]);

  const load = useCallback(async () => {
    setReading(true);

    try {
      const page = await listCurated(milestoneId);
      setPictures(page.items);
      setMediaBaseUrl(page.mediaBaseUrl);
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The curated screenshots could not be read.',
      );
    } finally {
      setReading(false);
    }

    try {
      const evidence = await listEvidence(milestoneId);
      setBundleRevision(evidence.revision);
    } catch (thrown) {
      // A revision that cannot be read is reported with the list failure above when there is one;
      // on its own it only means the next change will be refused as stale rather than applied.
      setReadFailure((current) =>
        current ?? (thrown instanceof Error ? thrown.message : 'The evidence revision could not be read.'),
      );
    }
  }, [milestoneId]);

  useEffect(() => {
    void load();
  }, [load]);

  // The selected pictures in the owner's sequence; the rest in the capture order the server
  // publishes. Sorting here rather than asking the server, because the list route's order is the
  // capture package's contract and this panel must not change what that panel reads.
  const selected = [...pictures]
    .filter((picture) => picture.status === 'selected')
    .sort((left, right) => left.displayOrder - right.displayOrder);
  const offered = pictures.filter((picture) => picture.status !== 'selected');

  const apply = useCallback(
    async (
      picture: CuratedScreenshot,
      edit: { readonly status?: 'candidate' | 'selected' | 'rejected'; readonly caption?: string; readonly isPrimary?: boolean },
      action: string,
    ) => {
      // The evidence revision this change is decided against, read now rather than trusted from
      // mount: the evidence panel above this one appends to the same milestone.
      const decidedAgainst = bundleRevision;

      setBusyId(picture.id);
      setFailure(null);
      setFailedAction(null);

      try {
        const result = await curateScreenshot(
          picture.id,
          edit,
          picture.revision,
          decidedAgainst,
        );

        // The server's answer is what the evidence holds now, not a guess from the request: when
        // the revision advanced, the evidence panel above is told to read the bundle again.
        setBundleRevision(result.bundleRevision);

        if (result.bundleRevision !== decidedAgainst) {
          onEvidenceChanged();
        }

        await load();
      } catch (thrown) {
        if (thrown instanceof CurationApiError) {
          setFailure(thrown);
          setFailedAction(action);

          if (thrown.isRevisionConflict) {
            await load();
          }
        } else {
          setFailure(
            new CurationApiError(
              { code: 'internal_error', message: 'The request could not be completed.', traceId: '' },
              0,
            ),
          );
          setFailedAction(action);
        }
      } finally {
        setBusyId(null);
      }
    },
    [bundleRevision, load, onEvidenceChanged],
  );

  const move = useCallback(
    async (picture: CuratedScreenshot, direction: -1 | 1) => {
      const ids = selected.map((entry) => entry.id);
      const from = ids.indexOf(picture.id);
      const to = from + direction;

      if (from < 0 || to < 0 || to >= ids.length) {
        return;
      }

      const reordered = [...ids];
      reordered.splice(from, 1);
      reordered.splice(to, 0, picture.id);

      setBusyId(picture.id);
      setFailure(null);
      setFailedAction(null);

      try {
        await orderScreenshots(milestoneId, reordered, bundleRevision);
        await load();
      } catch (thrown) {
        if (thrown instanceof CurationApiError) {
          setFailure(thrown);
          setFailedAction(`reordering ${milestoneTitle}`);

          if (thrown.isRevisionConflict) {
            await load();
          }
        }
      } finally {
        setBusyId(null);
      }
    },
    [bundleRevision, load, milestoneId, milestoneTitle, selected],
  );

  return (
    <section className="evidence" data-testid="curation-panel">
      <h4 className="evidence__heading" data-testid="curation-heading">
        Selected screenshots
      </h4>

      {reading && pictures.length === 0 ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="curation-loading">
          Reading what was chosen…
        </p>
      ) : readFailure !== null && pictures.length === 0 ? (
        <p className="notice--error" role="alert" data-testid="curation-failed">
          The curated screenshots for this milestone could not be read: {readFailure}
        </p>
      ) : pictures.length === 0 ? (
        <div className="empty" data-testid="curation-empty">
          <p className="empty__title" data-testid="curation-empty-title">
            No screenshot has been captured for {milestoneTitle}
          </p>
          <p className="empty__body" data-testid="curation-empty-body">
            There is nothing to choose from yet. That is what is stored - it is not a placeholder
            and not a failed read. Ask for a capture above and the pictures will appear here once
            the job has run; choosing one publishes it into this milestone's evidence.
          </p>
        </div>
      ) : (
        <div data-testid="curation-list">
          {selected.length > 0 ? (
            <ul className="evidence__bundles" data-testid="curation-selected">
              {selected.map((picture) => (
                <PictureView
                  key={picture.id}
                  picture={picture}
                  mediaBaseUrl={mediaBaseUrl}
                  archived={archived}
                  busy={busyId === picture.id}
                  broken={brokenImages.includes(picture.id)}
                  onBroken={() =>
                    setBrokenImages((current) =>
                      current.includes(picture.id) ? current : [...current, picture.id],
                    )
                  }
                  onCurate={(edit, action) => void apply(picture, edit, action)}
                  onMove={(direction) => void move(picture, direction)}
                  canMoveUp={selected[0]?.id !== picture.id}
                  canMoveDown={selected[selected.length - 1]?.id !== picture.id}
                />
              ))}
            </ul>
          ) : (
            <p className="view__hint" data-testid="curation-none-selected">
              Nothing is selected yet. Only a picture its owner selects enters this milestone's
              evidence - a capture re-run offers new candidates and never changes this choice.
            </p>
          )}

          {offered.length > 0 ? (
            <>
              <h5 className="evidence__subheading" data-testid="curation-offered-heading">
                Offered candidates
              </h5>
              <ul className="evidence__bundles" data-testid="curation-offered">
                {offered.map((picture) => (
                  <PictureView
                    key={picture.id}
                    picture={picture}
                    mediaBaseUrl={mediaBaseUrl}
                    archived={archived}
                    busy={busyId === picture.id}
                    broken={brokenImages.includes(picture.id)}
                    onBroken={() =>
                      setBrokenImages((current) =>
                        current.includes(picture.id) ? current : [...current, picture.id],
                      )
                    }
                    onCurate={(edit, action) => void apply(picture, edit, action)}
                    onMove={null}
                    canMoveUp={false}
                    canMoveDown={false}
                  />
                ))}
              </ul>
            </>
          ) : null}
        </div>
      )}

      {/* Which evidence a choice would be published into, so the owner can see it has moved on since
          the page was opened. The revision a change carries is read again from this value at the
          moment it is made, so a stale one is refused rather than silently rebased. */}
      {pictures.length > 0 ? (
        <p className="view__hint" data-testid="curation-revision-hint">
          A choice here is published into revision {bundleRevision} of this milestone's evidence.
        </p>
      ) : null}

      {archived && pictures.length > 0 ? (
        <p className="notice notice--muted" data-testid="curation-archived">
          This project is archived, so its pictures are kept and no choice can be changed.
        </p>
      ) : null}

      {failure !== null ? (
        <p className="notice--error" role="alert" data-testid="curation-request-failed">
          {failure.isRevisionConflict
            ? `Nothing was changed: somebody else moved this first, so this was decided against revision ${bundleRevision}, which is no longer current. The list above is what the server holds now - read it, then decide again.`
            : failedAction !== null
              ? `Nothing was changed while ${failedAction}: ${failure.message}`
              : `Nothing was changed: ${failure.message}`}
          {failure.messageFor('status') !== null ? ` ${failure.messageFor('status')}` : ''}
          {failure.messageFor('caption') !== null ? ` ${failure.messageFor('caption')}` : ''}
          {failure.messageFor('isPrimary') !== null ? ` ${failure.messageFor('isPrimary')}` : ''}
          {failure.messageFor('ids') !== null ? ` ${failure.messageFor('ids')}` : ''}
        </p>
      ) : null}
    </section>
  );
}

function PictureView({
  picture,
  mediaBaseUrl,
  archived,
  busy,
  broken,
  onBroken,
  onCurate,
  onMove,
  canMoveUp,
  canMoveDown,
}: {
  readonly picture: CuratedScreenshot;
  readonly mediaBaseUrl: string;
  readonly archived: boolean;
  readonly busy: boolean;
  readonly broken: boolean;
  readonly onBroken: () => void;
  readonly onCurate: (
    edit: { readonly status?: 'candidate' | 'selected' | 'rejected'; readonly caption?: string; readonly isPrimary?: boolean },
    action: string,
  ) => void;
  readonly onMove: ((direction: -1 | 1) => void) | null;
  readonly canMoveUp: boolean;
  readonly canMoveDown: boolean;
}) {
  const [caption, setCaption] = useState(picture.caption);
  const source = `${mediaBaseUrl}/${picture.mediaId}`;
  const redirected = picture.finalUrl !== picture.sourceUrl;
  const selected = picture.status === 'selected';

  // The input follows the stored caption across reloads, but never steals what is being typed:
  // it only resets when the stored value changed underneath, which is exactly the 409 case the
  // panel already reported.
  const [seenCaption, setSeenCaption] = useState(picture.caption);

  if (seenCaption !== picture.caption) {
    setSeenCaption(picture.caption);
    setCaption(picture.caption);
  }

  const captionDirty = caption !== picture.caption;

  return (
    <li className="evidence__bundle" data-testid="curation-item" data-status={picture.status}>
      <p className="evidence__revision" data-testid="curation-item-viewport">
        {picture.viewport} · {picture.width} × {picture.height} ·{' '}
        <span className="badge" data-testid="curation-item-status">
          {picture.status}
        </span>
        {picture.isPrimary ? (
          <span className="badge badge--ok" data-testid="curation-item-primary-badge">
            primary
          </span>
        ) : null}
      </p>

      {broken ? (
        <p className="view__state" data-testid="curation-item-unavailable">
          This candidate's picture cannot be read, so no image is drawn. The choice below still
          applies to the stored row rather than to a picture that is not there.
        </p>
      ) : (
        <img
          className="capture__image"
          src={source}
          alt={`The page at ${picture.finalUrl}, captured at ${picture.viewport} width`}
          data-testid="curation-item-image"
          onError={onBroken}
        />
      )}

      <p className="capture__provenance">
        <span data-testid="curation-item-source">Asked for {picture.sourceUrl}</span>
        <br />
        <span data-testid="curation-item-final">
          {redirected ? `Ended at ${picture.finalUrl}` : picture.finalUrl}
        </span>
        <br />
        <span data-testid="curation-item-captured">Captured {picture.capturedAt}</span>
      </p>

      {picture.caption.length > 0 || selected ? (
        <p className="milestone__notes" data-testid="curation-item-caption">
          {picture.caption.length > 0 ? picture.caption : 'No caption - the evidence carries where this was captured.'}
        </p>
      ) : null}

      {archived ? null : (
        <div className="evidence__form" data-testid="curation-item-controls">
          <label className="session__label" htmlFor={`curation-caption-${picture.id}`}>
            Caption, at most {CURATION_CAPTION_MAX_LENGTH} characters (optional)
          </label>
          <input
            id={`curation-caption-${picture.id}`}
            className="session__input"
            value={caption}
            maxLength={CURATION_CAPTION_MAX_LENGTH}
            onChange={(event) => setCaption(event.target.value)}
            placeholder="What this picture shows"
            data-testid="curation-item-caption-input"
          />

          <div className="form__actions">
            <button
              type="button"
              className="action"
              disabled={busy || !captionDirty}
              onClick={() => onCurate({ caption }, `captioning ${picture.viewport}`)}
              data-testid="curation-item-caption-save"
            >
              {busy ? 'Saving…' : 'Save caption'}
            </button>

            {selected ? (
              <button
                type="button"
                className="action"
                disabled={busy}
                onClick={() =>
                  onCurate({ status: 'candidate' }, `deselecting ${picture.viewport}`)
                }
                data-testid="curation-item-deselect"
              >
                Deselect
              </button>
            ) : (
              <button
                type="button"
                className="action action--primary"
                disabled={busy}
                onClick={() =>
                  onCurate(
                    captionDirty ? { status: 'selected', caption } : { status: 'selected' },
                    `selecting ${picture.viewport}`,
                  )
                }
                data-testid="curation-item-select"
              >
                Select
              </button>
            )}

            {picture.status !== 'rejected' ? (
              <button
                type="button"
                className="action"
                disabled={busy}
                onClick={() => onCurate({ status: 'rejected' }, `rejecting ${picture.viewport}`)}
                data-testid="curation-item-reject"
              >
                Reject
              </button>
            ) : (
              <button
                type="button"
                className="action"
                disabled={busy}
                onClick={() =>
                  onCurate({ status: 'candidate' }, `re-offering ${picture.viewport}`)
                }
                data-testid="curation-item-reoffer"
              >
                Re-offer
              </button>
            )}

            {selected && !picture.isPrimary ? (
              <button
                type="button"
                className="action"
                disabled={busy}
                onClick={() => onCurate({ isPrimary: true }, `making ${picture.viewport} primary`)}
                data-testid="curation-item-primary"
              >
                Make primary
              </button>
            ) : null}

            {onMove !== null ? (
              <>
                <button
                  type="button"
                  className="action"
                  disabled={busy || !canMoveUp}
                  onClick={() => onMove(-1)}
                  data-testid="curation-item-move-up"
                >
                  Move up
                </button>
                <button
                  type="button"
                  className="action"
                  disabled={busy || !canMoveDown}
                  onClick={() => onMove(1)}
                  data-testid="curation-item-move-down"
                >
                  Move down
                </button>
              </>
            ) : null}
          </div>
        </div>
      )}
    </li>
  );
}
