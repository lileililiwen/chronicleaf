import { useState } from 'react';

import {
  approveVoiceDraft,
  editVoiceDraft,
  listVoiceDrafts,
  readCatalogue,
  readVoiceDraft,
  readVoiceProfile,
} from './api';
import type { VoiceApiError } from './api';
import { VoiceCheckPanel } from './VoiceCheckPanel';
import { VoiceProfilePanel } from './VoiceProfilePanel';
import { ruleLabel } from './types';
import type { VoiceDraftPage } from './types';
import {
  useVoiceCatalogue,
  useVoiceDraft,
  useVoiceDraftPage,
  useVoiceMutation,
  useVoiceProfile,
} from './useVoice';

/**
 * The voice area for one project, rendered inside the project detail view (VOICE-PROFILES-001/002).
 *
 * Three surfaces sit behind one toggle, in the order the owner works in them: the voice the project
 * writes in, a check of some text against it, and the drafts already written under it. The drafts
 * list is the one place the boundary scenario is visible - each row names the voice its text was
 * created under, so a profile change is something an owner can see did not reach work already done.
 */
export /** Reads a project's first page of drafts. A named function, so the reader's identity is stable. */
function readDraftPage(projectId: string, signal: AbortSignal) {
  return listVoiceDrafts(projectId, undefined, undefined, signal);
}

export function VoiceArea({ projectId }: { readonly projectId: string }) {
  const [open, setOpen] = useState(false);
  const catalogue = useVoiceCatalogue(readCatalogue);
  const profile = useVoiceProfile(readVoiceProfile, projectId);
  const drafts = useVoiceDraftPage(readDraftPage, projectId, open);

  return (
    <section className="evidence" data-testid="voice-area">
      <p className="view__actions">
        <button
          type="button"
          className="action"
          data-testid="voice-toggle"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
        >
          {open ? 'Hide writing voice' : 'Writing voice'}
        </button>
      </p>

      {open ? (
        <div className="voice-panels" data-testid="voice-panels">
          {catalogue.loading ? (
            <p className="view__state" aria-live="polite" aria-busy="true" data-testid="voice-catalogue-loading">
              Loading the voice presets…
            </p>
          ) : catalogue.failure !== null ? (
            <p className="notice notice--error" role="alert" data-testid="voice-catalogue-error">
              {catalogue.failure.message}
            </p>
          ) : catalogue.data !== null ? (
            <VoiceProfilePanel projectId={projectId} catalogue={catalogue.data} />
          ) : null}

          {catalogue.data !== null ? (
            <VoiceCheckPanel
              projectId={projectId}
              profile={profile.profile}
              catalogue={catalogue.data}
              onDraftSaved={drafts.reload}
            />
          ) : null}

          <DraftList
            page={drafts.page}
            loading={drafts.loading}
            failure={drafts.failure}
            onChanged={drafts.reload}
          />
        </div>
      ) : null}
    </section>
  );
}

/**
 * The drafts already written under this project's voice.
 *
 * Each row reports how many rules its text currently breaks, so a draft that cannot be approved is
 * visible before the owner opens it. Opening one shows the text exactly as stored beside the findings
 * the checker reports for it, and approval is offered only once nothing is flagged.
 */
function DraftList({
  page,
  loading,
  failure,
  onChanged,
}: {
  readonly page: VoiceDraftPage | null;
  readonly loading: boolean;
  readonly failure: VoiceApiError | null;
  readonly onChanged: () => void;
}) {
  const [openDraftId, setOpenDraftId] = useState<string | null>(null);

  return (
    <section className="evidence" data-testid="voice-drafts">
      <h3 className="view__heading">Drafts under this voice</h3>

      {failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="voice-drafts-error">
          {failure.message}
        </p>
      ) : loading && page === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="voice-drafts-loading">
          Loading this project's drafts…
        </p>
      ) : (page ?? { items: [], nextCursor: null, pageSize: 0 }).items.length === 0 ? (
        <div className="empty" data-testid="voice-drafts-empty">
          <p className="empty__title">No drafts yet.</p>
          <p className="view__state">
            Check some text above and save it as a draft. It keeps the voice of this moment, whatever
            you change the voice to afterwards.
          </p>
        </div>
      ) : (
        <ul className="cards" data-testid="voice-drafts-list">
          {(page?.items ?? []).map((summary) => (
            <li key={summary.id}>
              <button
                type="button"
                className="card"
                data-testid="voice-draft"
                onClick={() => setOpenDraftId(summary.id)}
              >
                <span className="card__name">{summary.status}</span>
                <span className="card__body">
                  Voice {summary.snapshotFingerprint.slice(0, 12)} · {summary.findingCount} finding
                  {summary.findingCount === 1 ? '' : 's'}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}

      {openDraftId !== null ? (
        <VoiceDraftView draftId={openDraftId} onClosed={() => setOpenDraftId(null)} onChanged={onChanged} />
      ) : null}
    </section>
  );
}

/**
 * One stored draft, with its own copied voice and the findings for its stored text.
 *
 * The text area is seeded from the answer that landed and then left alone: an edit here is an edit
 * the owner wrote, and nothing in this component re-seeds it from the server while they are typing.
 * Approval presents the revision the read published and is disabled until nothing is flagged, which
 * is the same condition the server enforces.
 */
function VoiceDraftView({
  draftId,
  onClosed,
  onChanged,
}: {
  readonly draftId: string;
  readonly onClosed: () => void;
  readonly onChanged: () => void;
}) {
  const draft = useVoiceDraft(readVoiceDraft, draftId);
  const edit = useVoiceMutation();
  const approve = useVoiceMutation();

  if (draft.failure?.status === 404) {
    return (
      <p className="notice notice--error" role="alert" data-testid="voice-draft-missing">
        This draft is not in your installation, so it cannot be shown. Nothing was changed.
      </p>
    );
  }

  if (draft.loading || draft.draft === null) {
    return (
      <p className="view__state" aria-live="polite" aria-busy="true" data-testid="voice-draft-loading">
        Loading this draft…
      </p>
    );
  }

  const stored = draft.draft;

  // A successful edit or approval re-reads this draft, not just the list beside it. The status and
  // revision on screen are facts about the stored record, and a panel that still showed the
  // pre-approval ones after the button reported success would be telling the owner something false.
  const settle = () => {
    draft.reload();
    onChanged();
  };

  return (
    <section className="evidence" data-testid="voice-draft-view">
      <h4 className="view__heading" data-testid="voice-draft-title">
        {stored.title}
      </h4>
      <p className="view__state" data-testid="voice-draft-snapshot">
        Written under {stored.snapshot.presetId}, profile revision {stored.snapshot.profileRevision}{' '}
        ({stored.snapshot.fingerprint.slice(0, 12)}). Status {stored.status}, revision {stored.revision}.
      </p>

      {stored.findings.length === 0 ? (
        <p className="notice" data-testid="voice-draft-clean">
          No voice violations under that voice. Your text was not changed.
        </p>
      ) : (
        <ul className="findings" data-testid="voice-draft-findings">
          {stored.findings.map((finding, index) => (
            <li key={`${finding.rule}-${finding.start}-${index}`} className="finding" data-testid="voice-draft-finding">
              <p className="finding__rule">
                {ruleLabel(finding.rule)} in the {finding.field}
              </p>
              <p className="finding__span" data-testid="voice-draft-finding-span">
                characters {finding.start + 1}–{finding.start + finding.length}
              </p>
              <p className="finding__excerpt" data-testid="voice-draft-finding-excerpt">
                {finding.excerpt}
              </p>
              <p className="finding__message">{finding.message}</p>
            </li>
          ))}
        </ul>
      )}

      {edit.failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="voice-draft-edit-error">
          {edit.failure.message}
        </p>
      ) : null}

      {approve.failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="voice-draft-approve-error">
          {approve.failure.message}
        </p>
      ) : null}

      <p className="view__actions">
        <button type="button" className="action" data-testid="voice-draft-close" onClick={onClosed}>
          Close
        </button>
        <button
          type="button"
          className="action"
          data-testid="voice-draft-resave"
          disabled={edit.saving || stored.status === 'approved'}
          onClick={() => {
            void edit
              .run(() => editVoiceDraft(stored.id, stored.title, stored.body, stored.revision))
              .then(settle);
          }}
        >
          {edit.saving ? 'Saving…' : 'Re-save this text'}
        </button>
        <button
          type="button"
          className="action action--primary"
          data-testid="voice-draft-approve"
          disabled={approve.saving || stored.findings.length > 0 || stored.status === 'approved'}
          onClick={() => {
            void approve
              .run(() => approveVoiceDraft(stored.id, stored.revision))
              .then(settle);
          }}
        >
          {approve.saving ? 'Approving…' : 'Approve'}
        </button>
      </p>

      {stored.findings.length > 0 ? (
        <p className="form__hint" data-testid="voice-draft-approve-blocked">
          Approval is unavailable while findings remain. Fix the text and save it again - nothing here
          will rewrite it for you.
        </p>
      ) : null}
    </section>
  );
}
