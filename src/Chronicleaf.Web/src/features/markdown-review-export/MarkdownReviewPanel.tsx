import { useCallback, useEffect, useState } from 'react';

import { readDraft, readEvidenceHead } from '../grounded-markdown-drafts/api';
import type { ContentAsset as DraftAsset } from '../grounded-markdown-drafts/types';
import { approveAsset, editAsset, exportAsset, readReview, unapproveAsset } from './api';
import type { ExportFormat } from './api';
import { renderSafeMarkdown } from './sanitize';
import { ReviewApiError, type ContentReview } from './types';
import { formatRevisionConflict } from '../ui-feedback/revisionConflict';
import { useToast } from '../ui-feedback/Toast';

/**
 * Human editing, approval and export of one milestone's Markdown draft
 * (MARKDOWN-REVIEW-EXPORT-001/002).
 *
 * <b>This panel's subject is a decision, and the decision is the owner's.</b> So four things are
 * load-bearing here and none of them is decoration:
 *
 * - <b>An edit appends a revision; it never rewrites one.</b> The textarea holds the owner's
 *   words and saving stores them as a new revision beside the generated ones. A save names the
 *   head revision it was read from in `If-Match`, so an edit against a stale head is refused
 *   rather than forked.
 * - <b>Approval names a revision and an evidence revision, and nothing else approves.</b> The
 *   approve control is a button only a press can reach - it is never a form submission, so Enter
 *   in the editor cannot approve. While deterministic findings remain the server refuses, and no
 *   acknowledgment clears that: the text is corrected and approval is asked for again.
 * - <b>The preview never executes.</b> The body is rendered by the safe subset renderer, which
 *   builds React text nodes and http/https anchors only. A script tag or an unsafe link in the
 *   source is visible as characters and reachable as nothing.
 * - <b>An export is a download of approved bytes, never a publication.</b> The server answers
 *   with the exact stored bytes as a Markdown attachment, or with the same revision rendered
 *   through the fixed sanitizing template as an HTML file - or a ZIP holding it with the
 *   approval-covered pictures and their checksum manifest - or with a 409 naming what moved.
 *   This panel hands those bytes to a download and renders nothing from them.
 */
export function MarkdownReviewPanel({
  milestoneId,
  milestoneTitle,
  archived,
  confirmed,
}: {
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  /** An archived project's reviews are kept and cannot be added to, so no edit control is offered. */
  readonly archived: boolean;
  /** A milestone the owner has not confirmed has no draft, so there is nothing to review. */
  readonly confirmed: boolean;
}) {
  const [draft, setDraft] = useState<DraftAsset | null>(null);
  const [review, setReview] = useState<ContentReview | null>(null);
  const [evidenceRevision, setEvidenceRevision] = useState(0);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);

  const [body, setBody] = useState('');
  const [title, setTitle] = useState('');
  const [dirty, setDirty] = useState(false);

  const [saving, setSaving] = useState(false);
  const [saveFailure, setSaveFailure] = useState<ReviewApiError | null>(null);
  const [approving, setApproving] = useState(false);
  const [approveFailure, setApproveFailure] = useState<ReviewApiError | null>(null);
  const [exporting, setExporting] = useState(false);
  const [exportFailure, setExportFailure] = useState<ReviewApiError | null>(null);
  // WEB-FEED-001: outcomes are announced through the shell-level toast region.
  const { showToast } = useToast();

  const load = useCallback(async () => {
    setReading(true);

    try {
      const asset = await readDraft(milestoneId);
      setDraft(asset);

      if (asset?.assetId !== null && asset?.assetId !== undefined) {
        const [state, head] = await Promise.all([
          readReview(asset.assetId as string),
          readEvidenceHead(milestoneId),
        ]);
        setReview(state);
        setEvidenceRevision(head.revision);
      } else {
        setReview(null);
      }

      setBody(asset?.bodyMarkdown ?? '');
      setTitle(asset?.title ?? '');
      setDirty(false);
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The review for this milestone could not be read.',
      );
    } finally {
      setReading(false);
    }
  }, [milestoneId]);

  useEffect(() => {
    void load();
  }, [load]);

  const save = useCallback(async () => {
    if (draft?.assetId == null || draft.currentRevision == null) {
      return;
    }

    setSaving(true);
    setSaveFailure(null);

    try {
      await editAsset(draft.assetId, draft.currentRevision, {
        bodyMarkdown: body,
        // A title that is unchanged is sent as null, so the revision keeps the stored one rather
        // than rewriting it with an identical value through a different path.
        title: title === (draft.title ?? '') ? null : title,
      });
      showToast('Saved as a new revision.', 'ok');
      await load();
    } catch (thrown) {
      const failure = asReviewError(thrown);
      setSaveFailure(failure);
      showToast(describeFailure(failure), 'error');
    } finally {
      setSaving(false);
    }
  }, [body, draft, load, showToast, title]);

  const approve = useCallback(async () => {
    if (draft?.assetId == null || draft.currentRevision == null) {
      return;
    }

    setApproving(true);
    setApproveFailure(null);

    try {
      // Both revisions are read at the moment of the press rather than the ones this panel
      // mounted with: the evidence panel above appends to the same milestone, and approving
      // against a bundle the server has already moved past must be the server's refusal rather
      // than this panel's stale guess.
      const head = await readEvidenceHead(milestoneId);
      setEvidenceRevision(head.revision);

      await approveAsset(draft.assetId, {
        contentRevision: draft.currentRevision,
        evidenceRevision: head.revision,
      });
      showToast('Revision approved.', 'ok');
      await load();
    } catch (thrown) {
      const failure = asReviewError(thrown);
      setApproveFailure(failure);
      showToast(describeFailure(failure), 'error');
    } finally {
      setApproving(false);
    }
  }, [draft, load, milestoneId, showToast]);

  const unapprove = useCallback(async () => {
    if (draft?.assetId == null) {
      return;
    }

    setApproving(true);
    setApproveFailure(null);

    try {
      await unapproveAsset(draft.assetId, null);
      showToast('Approval withdrawn.', 'ok');
      await load();
    } catch (thrown) {
      const failure = asReviewError(thrown);
      setApproveFailure(failure);
      showToast(describeFailure(failure), 'error');
    } finally {
      setApproving(false);
    }
  }, [draft, load, showToast]);

  const download = useCallback(async (format: ExportFormat) => {
    if (draft?.assetId == null) {
      return;
    }

    setExporting(true);
    setExportFailure(null);

    try {
      const answer = await exportAsset(draft.assetId, null, format);
      const url = URL.createObjectURL(answer.bytes);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = answer.fileName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch (thrown) {
      setExportFailure(asReviewError(thrown));
    } finally {
      setExporting(false);
    }
  }, [draft]);

  const revision = draft?.currentRevision ?? null;
  const approved = review?.reviewStatus === 'approved';
  const invalidated = review?.reviewStatus === 'invalidated';
  const canEdit = !archived && confirmed && draft !== null;

  return (
    <section className="evidence" data-testid="review-panel">
      {/* WEB-A11Y-ROLES-001. Detail panels are h3 under the h2 row title. */}
      <h3 className="evidence__heading" data-testid="review-heading">
        Review
      </h3>

      <p className="view__hint" data-testid="review-hint">
        A draft becomes publishable only when you say so: edit the text below, approve the exact
        revision you read, and export the approved bytes. Editing the text or the evidence after
        an approval ends that approval, and nothing here is published anywhere - an export is a
        file you download.
      </p>

      {reading && draft === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="review-loading">
          Reading the review for {milestoneTitle}…
        </p>
      ) : readFailure !== null && draft === null ? (
        <p className="notice--error" role="alert" data-testid="review-failed">
          The review for this milestone could not be read: {readFailure}
        </p>
      ) : draft === null || revision === null ? (
        <div className="empty" data-testid="review-empty">
          <p className="empty__title" data-testid="review-empty-title">
            Nothing to review for {milestoneTitle}
          </p>
          <p className="empty__body" data-testid="review-empty-body">
            A review needs a draft, and this milestone has none yet. Write one from its evidence
            first - the draft panel above does that - and it will appear here for editing and
            approval.
          </p>
        </div>
      ) : (
        <div data-testid="review-view">
          <p className="evidence__revision" data-testid="review-revision">
            Revision {revision} ·{' '}
            <span data-testid="review-status">{review?.reviewStatus ?? 'draft'}</span>
            {review?.approval !== null && review?.approval !== undefined ? (
              <>
                {' '}
                · approved <span data-testid="review-approved-at">{review.approval.approvedAt}</span>{' '}
                · body <span data-testid="review-body-hash">{review.approval.bodyHash}</span>
              </>
            ) : null}
          </p>

          {invalidated ? (
            <p className="notice notice--muted" data-testid="review-invalidated">
              The approval no longer describes the current revision, the current evidence or the
              selected pictures. Review the current state and approve it again before exporting.
            </p>
          ) : null}

          {draft.groundingFindings.length > 0 || draft.voiceFindings.length > 0 ? (
            <div className="draft__findings" data-testid="review-findings">
              <h4 className="view__subsection">Findings on this revision</h4>
              <p className="view__hint" data-testid="review-findings-hint">
                An approval is refused while any finding below remains. Correct the text - no
                acknowledgment clears this - and ask for approval again.
              </p>
              <ul>
                {draft.groundingFindings.map((finding, index) => (
                  <li key={`g-${index}`} data-testid="review-finding-item">
                    <span data-testid="review-finding-rule">{finding.rule}</span>
                    {' — '}
                    <span data-testid="review-finding-message">{finding.message}</span>
                  </li>
                ))}
                {draft.voiceFindings.map((finding, index) => (
                  <li key={`v-${index}`} data-testid="review-finding-item">
                    <span data-testid="review-finding-rule">{finding.rule}</span>
                    {' — '}
                    <span data-testid="review-finding-message">{finding.message}</span>
                  </li>
                ))}
              </ul>
            </div>
          ) : (
            <p className="view__state" data-testid="review-findings-clean">
              No findings remain on this revision. It can be approved as it stands.
            </p>
          )}

          {canEdit ? (
            <div className={dirty ? 'review__edit review__edit--dirty' : 'review__edit'} data-testid="review-editor">
              <div className="review__title-row" data-testid="review-title-row">
                <label className="session__label" htmlFor="review-title">
                  Title
                </label>
                {dirty ? (
                  <span className="badge badge--pending" data-testid="review-dirty-tag">
                    Unsaved changes
                  </span>
                ) : null}
              </div>
              <input
                id="review-title"
                className="session__input"
                value={title}
                onChange={(event) => {
                  setTitle(event.target.value);
                  setDirty(true);
                }}
                data-testid="review-title"
              />

              <label className="session__label" htmlFor="review-body">
                Body
              </label>
              <textarea
                id="review-body"
                className="session__input review__editor"
                value={body}
                rows={16}
                onChange={(event) => {
                  setBody(event.target.value);
                  setDirty(true);
                }}
                onKeyDown={(event) => {
                  // Enter in the editor edits text; it never approves. There is no form here
                  // whose default submission could approve, and the approve control below is a
                  // button only a press can reach.
                  event.stopPropagation();
                }}
                data-testid="review-body"
              />

              <div className="form__actions">
                <button
                  type="button"
                  className="action action--primary"
                  disabled={saving || !dirty}
                  onClick={() => void save()}
                  data-testid="review-save"
                >
                  {saving ? 'Saving…' : 'Save as a new revision'}
                </button>
              </div>

              {saveFailure !== null ? (
                <p className="notice--error" role="alert" data-testid="review-save-failed">
                  {describeFailure(saveFailure)}
                </p>
              ) : null}
            </div>
          ) : (
            <p className="notice notice--muted" data-testid="review-readonly">
              {archived
                ? 'This project is archived, so its reviews are kept and no edit can be saved.'
                : 'This milestone is not confirmed, so its text cannot be edited here.'}
            </p>
          )}

          <div data-testid="review-preview">
            <h4 className="view__subsection">Preview</h4>
            <p className="view__hint" data-testid="review-preview-hint">
              Rendered from exactly the text above, with raw markup shown as characters rather
              than run. What you see is what the next revision would carry - and what a Markdown
              export would download byte for byte. An HTML export renders the same text through
              the fixed sanitizing template instead.
            </p>
            <div className="review__preview" data-testid="review-preview-body">
              {renderSafeMarkdown(dirty ? body : (draft.bodyMarkdown ?? ''))}
            </div>
          </div>

          {canEdit ? (
            <div className="form__actions" data-testid="review-decision">
              {!approved ? (
                <button
                  type="button"
                  className="action action--primary"
                  disabled={approving || dirty}
                  onClick={() => void approve()}
                  data-testid="review-approve"
                >
                  {approving ? 'Approving…' : `Approve revision ${revision}`}
                </button>
              ) : (
                <button
                  type="button"
                  className="action"
                  disabled={approving}
                  onClick={() => void unapprove()}
                  data-testid="review-unapprove"
                >
                  Withdraw approval
                </button>
              )}
              {dirty && !approved ? (
                <span className="view__hint" data-testid="review-unsaved">
                  Save the edited text first - an approval names the stored revision, not the
                  words still in the editor.
                </span>
              ) : null}
            </div>
          ) : null}

          {approveFailure !== null ? (
            <p className="notice--error" role="alert" data-testid="review-approve-failed">
              {describeFailure(approveFailure)}
            </p>
          ) : null}

          <div className="form__actions" data-testid="review-export-row">
            <button
              type="button"
              className="action"
              disabled={exporting || !approved}
              onClick={() => void download('markdown')}
              data-testid="review-export"
            >
              {exporting ? 'Exporting…' : 'Export approved Markdown'}
            </button>
            <button
              type="button"
              className="action"
              disabled={exporting || !approved}
              onClick={() => void download('html')}
              data-testid="review-export-html"
            >
              {exporting ? 'Exporting…' : 'Export approved HTML'}
            </button>
            {!approved ? (
              <span className="view__hint" data-testid="review-export-hint">
                An export needs a live approval of revision {revision}
                {evidenceRevision > 0 ? ` against evidence revision ${evidenceRevision}` : ''}.
                Approve it above first.
              </span>
            ) : null}
          </div>

          {exportFailure !== null ? (
            <p className="notice--error" role="alert" data-testid="review-export-failed">
              {describeFailure(exportFailure)}
            </p>
          ) : null}
        </div>
      )}
    </section>
  );
}

function asReviewError(thrown: unknown): ReviewApiError {
  if (thrown instanceof ReviewApiError) {
    return thrown;
  }

  return new ReviewApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The request could not be completed.',
      traceId: '',
    },
    0,
  );
}

/**
 * What a refusal means, in words an owner can act on. The server's own code is named first, so
 * a reader who cannot act on the words still has the value the logs carry.
 */
function describeFailure(failure: ReviewApiError): string {
  if (failure.isApprovalRequired) {
    return `Nothing approved authorizes this (${failure.code}). Review the current revision and approve it first.`;
  }

  if (failure.isApprovalStale) {
    return `The approval no longer describes the stored state (${failure.code}). Review the current revision and approve it again.`;
  }

  if (failure.isApprovalBlocked) {
    const detail = failure.messageFor('approval');

    return `This revision carries findings that must be corrected (${failure.code}).${detail === null ? '' : ` ${detail}`}`;
  }

  if (failure.isMediaUnavailable) {
    return `An approved picture is no longer available (${failure.code}). Re-select or re-capture the picture and approve the revision again.`;
  }

  if (failure.isRevisionConflict) {
    // WEB-FEED-003: the one shared conflict sentence, byte-identical with the
    // milestone and project editors for the same stored revision.
    return formatRevisionConflict(failure.storedRevision);
  }

  return `${failure.message} (${failure.code})`;
}
