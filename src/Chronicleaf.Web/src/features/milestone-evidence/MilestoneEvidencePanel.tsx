import { useEffect, useRef, useState } from 'react';

import { EvidenceApiError, appendManualEvidence, listEvidence } from './api';
import {
  availabilityLabel,
  evidenceKindLabel,
  type EvidenceBundle,
  type EvidenceItem,
} from './types';
import { useEvidence } from './useEvidence';

/**
 * What was actually collected for one milestone (MILESTONE-EVIDENCE-001/002).
 *
 * Three states are visible here and each is shown as what it is. A milestone with no evidence says
 * so plainly and offers the owner the one thing they can add by hand. A milestone with evidence
 * shows every bundle, its revision, and each item's kind, availability and source revision, because
 * a claim without its source is not evidence. And a source that could not be collected is shown as
 * unavailable, with the reason it gave, rather than quietly omitted - a story that hides its gaps
 * reads the same as a story that has none.
 *
 * The manual form is the owner's own voice and is labelled as such. It is not a way to state a fact
 * about a commit or a release: the form has no kind, no availability and no source field, so what it
 * records is a limitation or a next step and nothing else. The revision the form sends is the one
 * this panel read, so a 409 means somebody else appended first - and the refusal is shown with the
 * newer evidence re-read, never swallowed.
 *
 * <b>reloadToken is how an append by somebody else reaches this panel.</b> A bundle is append-only and
 * this panel reads it once, so anything that appends outside the form below - a collection, a second
 * tab, another person - leaves it showing a revision the server has moved past. The prop is a bare
 * number for that reason. It carries no opinion about who appended or why, so this package learns
 * nothing about the features that write bundles; absent leaves the panel reading once, as it always
 * has; unchanged means nothing happened and nothing is re-read; changed means read the server's answer
 * again. It is not polled, and the token this panel mounted with is not treated as a change, so no
 * request is paid for on the way in.
 */
export function MilestoneEvidencePanel({
  milestoneId,
  milestoneTitle,
  archived,
  reloadToken,
}: {
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  /** An archived project's evidence cannot move, so the form is not offered at all. */
  readonly archived: boolean;
  /**
   * A number the owner of this panel changes when some other surface has appended to this milestone.
   *
   * Optional, and unchanged by default. A change re-reads the bundle and leaves what is on screen
   * visible while it does, exactly as the form's own append does - the reload is this panel's existing
   * one, not a second kind of read.
   */
  readonly reloadToken?: number;
}) {
  const evidence = useEvidence(listEvidence, milestoneId);
  const [notes, setNotes] = useState('');
  const [limitations, setLimitations] = useState('');
  const [nextSteps, setNextSteps] = useState('');
  const [busy, setBusy] = useState(false);
  const [appendFailure, setAppendFailure] = useState<EvidenceApiError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // The token this panel has already reacted to, so the first one it is given is recorded rather than
  // acted on: the bundle is read on mount, and re-reading it immediately would buy the same answer
  // twice.
  const seenReloadToken = useRef<number | null>(null);
  const reload = evidence.reload;

  useEffect(() => {
    if (reloadToken === undefined) {
      return;
    }

    if (seenReloadToken.current !== null && seenReloadToken.current !== reloadToken) {
      reload();
    }

    seenReloadToken.current = reloadToken;
  }, [reload, reloadToken]);

  const revision = evidence.data?.revision ?? 0;
  const bundles = evidence.data?.items ?? [];
  const hasEvidence = bundles.length > 0;

  const submit = async () => {
    setBusy(true);
    setAppendFailure(null);
    setNotice(null);

    try {
      const saved = await appendManualEvidence(milestoneId, revision, {
        notes,
        knownLimitations: splitLines(limitations),
        nextSteps: splitLines(nextSteps),
      });

      // The server's snapshot is what is shown, not the local form: the form's own guess about what
      // was carried forward would be a second, weaker answer to the same question.
      setNotes('');
      setLimitations('');
      setNextSteps('');
      setNotice(
        saved.revision > revision
          ? `Revision ${saved.revision} now holds your note and everything collected before it.`
          : `Revision ${saved.revision} already said that. Nothing was added.`,
      );
      evidence.reload();
    } catch (thrown) {
      // Shown, not discarded. A statement the owner wrote and the server refused must not leave the
      // form looking as though it saved.
      setAppendFailure(thrown instanceof EvidenceApiError ? thrown : null);
      setNotice(null);
      evidence.reload();
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="evidence" data-testid="evidence-panel">
      <h4 className="evidence__heading" data-testid="evidence-heading">
        Evidence
      </h4>

      {evidence.loading && evidence.data === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="evidence-loading">
          Reading what was collected…
        </p>
      ) : evidence.failure !== null && evidence.data === null ? (
        <p className="notice--error" role="alert" data-testid="evidence-failed">
          The evidence for this milestone could not be read: {evidence.failure.message}
        </p>
      ) : !hasEvidence ? (
        <div className="empty" data-testid="evidence-empty">
          <p className="empty__title" data-testid="evidence-empty-title">
            Nothing has been collected for this milestone
          </p>
          <p className="empty__body" data-testid="evidence-empty-body">
            There is no bundle, so this milestone has no evidence to show. That is what is stored -
            it is not a placeholder. A source will add its own commits, releases and gaps when one is
            connected; until then the only thing that can be added here is the owner's own note.
          </p>
        </div>
      ) : (
        <ul className="evidence__bundles" data-testid="evidence-bundles">
          {bundles.map((bundle) => (
            <EvidenceBundleView key={bundle.id} bundle={bundle} />
          ))}
        </ul>
      )}

      {archived ? (
        <p className="notice notice--muted" data-testid="evidence-archived">
          This project is archived, so its evidence is kept and cannot be added to.
        </p>
      ) : (
        <form
          className="evidence__form"
          data-testid="evidence-form"
          onSubmit={(event) => {
            event.preventDefault();
            void submit();
          }}
        >
          <p className="view__hint" data-testid="evidence-form-hint">
            {hasEvidence
              ? `Adding to revision ${revision}. Earlier evidence is kept, not replaced.`
              : 'There is no evidence yet, so this becomes revision 1.'}
          </p>

          <label className="session__label" htmlFor="evidence-notes">
            Your note about {milestoneTitle}
          </label>
          <textarea
            id="evidence-notes"
            className="session__input"
            value={notes}
            onChange={(event) => setNotes(event.target.value)}
            data-testid="evidence-notes"
          />

          <label className="session__label" htmlFor="evidence-limitations">
            Known limitations, one per line
          </label>
          <textarea
            id="evidence-limitations"
            className="session__input"
            value={limitations}
            onChange={(event) => setLimitations(event.target.value)}
            data-testid="evidence-limitations"
          />

          <label className="session__label" htmlFor="evidence-next-steps">
            Next steps, one per line
          </label>
          <textarea
            id="evidence-next-steps"
            className="session__input"
            value={nextSteps}
            onChange={(event) => setNextSteps(event.target.value)}
            data-testid="evidence-next-steps"
          />

          <div className="form__actions">
            <button
              type="submit"
              className="action action--primary"
              disabled={busy}
              data-testid="evidence-submit"
            >
              {busy ? 'Adding…' : 'Add to the evidence'}
            </button>
          </div>

          {notice !== null ? (
            <p className="notice" data-testid="evidence-notice">
              {notice}
            </p>
          ) : null}

          {appendFailure !== null ? (
            <p className="notice--error" role="alert" data-testid="evidence-append-failed">
              {appendFailure.isRevisionConflict
                ? 'Nothing was added: somebody else added evidence to this milestone first, so this was written against an older revision. The newer evidence is shown below - read it, then add your note again.'
                : `Nothing was added: ${appendFailure.message}`}
              {appendFailure.messageFor('notes') !== null
                ? ` ${appendFailure.messageFor('notes')}`
                : ''}
            </p>
          ) : null}
        </form>
      )}
    </section>
  );
}

function EvidenceBundleView({ bundle }: { readonly bundle: EvidenceBundle }) {
  const gaps = bundle.items.filter((item) => item.availability !== 'complete').length;

  return (
    <li className="evidence__bundle" data-testid="evidence-bundle">
      <p className="evidence__revision" data-testid="evidence-revision">
        Revision {bundle.revision}
        {gaps > 0 ? (
          <span className="badge badge--pending" data-testid="evidence-bundle-gaps">
            {gaps} not complete
          </span>
        ) : null}
      </p>

      <ul className="evidence__items">
        {bundle.items.map((item) => (
          <EvidenceItemView key={item.id} item={item} />
        ))}
      </ul>
    </li>
  );
}

function EvidenceItemView({ item }: { readonly item: EvidenceItem }) {
  const unavailable = item.availability === 'unavailable';
  const partial = item.availability === 'partial';

  return (
    <li
      className="evidence__item"
      data-testid="evidence-item"
      data-availability={item.availability}
    >
      <p className="evidence__item-head">
        <span className="evidence__kind" data-testid="evidence-item-kind">
          {evidenceKindLabel(item.kind)}
        </span>
        <span
          className={
            unavailable
              ? 'badge badge--muted'
              : partial
                ? 'badge badge--pending'
                : 'badge badge--ok'
          }
          data-testid="evidence-item-availability"
        >
          {availabilityLabel(item.availability)}
        </span>
      </p>

      <p className="evidence__item-meta" data-testid="evidence-item-source">
        {item.sourceRef} at {item.sourceRevision}
      </p>

      {unavailable ? (
        <p className="evidence__item-text evidence__item-text--gap" data-testid="evidence-item-reason">
          Not collected. {item.text}
        </p>
      ) : (
        <p className="evidence__item-text" data-testid="evidence-item-text">
          {item.text}
        </p>
      )}

      {partial ? (
        <p className="evidence__item-meta" data-testid="evidence-item-redactions">
          Left out: {item.redactions.join(', ')}
        </p>
      ) : null}

      {item.mediaId !== null ? (
        <p className="evidence__item-meta" data-testid="evidence-item-media">
          Media {item.mediaId} is kept for this item and cannot be deleted.
        </p>
      ) : null}
    </li>
  );
}

function splitLines(value: string): string[] {
  return value
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}
