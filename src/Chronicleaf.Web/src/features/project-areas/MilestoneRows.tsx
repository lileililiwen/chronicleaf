import { useState } from 'react';

import {
  MilestoneApiError,
  confirmMilestone,
  dismissMilestone,
  listMilestones,
} from '../projects-and-milestones/api';
import { MilestoneEvidencePanel } from '../milestone-evidence/MilestoneEvidencePanel';
import { GitHubSyncPanel } from '../github-evidence/GitHubSyncPanel';
import { LocalGitSyncPanel } from '../local-git-evidence/LocalGitSyncPanel';
import { LlmProviderArea } from '../llm-provider-contract/LlmProviderArea';
import { GroundedMarkdownDraftPanel } from '../grounded-markdown-drafts/GroundedMarkdownDraftPanel';
import { MarkdownReviewPanel } from '../markdown-review-export/MarkdownReviewPanel';
import { ContentVariantPanel } from '../content-asset-variants/ContentVariantPanel';
import { LinkedInPackPanel } from '../content-pack-panels/LinkedInPackPanel';
import { MediumPackPanel } from '../content-pack-panels/MediumPackPanel';
import { XiaohongshuPackPanel } from '../content-pack-panels/XiaohongshuPackPanel';
import { ScreenshotCapturePanel } from '../screenshot-capture/ScreenshotCapturePanel';
import { ScreenshotPicker } from '../screenshot-curation/ScreenshotPicker';
import { VideoStoryPanel } from '../video-story-panel/VideoStoryPanel';
import {
  milestoneTypeLabel,
  type Milestone,
  type MilestoneStatus,
} from '../projects-and-milestones/types';
import { useMilestones } from '../projects-and-milestones/useRecords';

/**
 * Which area's detail panels a milestone row renders (PROJECT-AREA-IA-001).
 *
 * `none` is the Overview row: chrome, filter context, and the owner's
 * confirm/dismiss actions, with no detail panel. Each other value renders
 * exactly the panels the move map assigns to that area — so every
 * per-milestone panel below renders in one area and one area only.
 */
export type MilestoneDetail = 'none' | 'evidence' | 'content' | 'evolution';

/**
 * The milestones stored under a project, with area-filtered detail.
 *
 * The status filter renders only where `showFilter` is set (Overview):
 * the other areas show every milestone regardless of status, because a
 * suite or an owner arriving at Evidence for one record must not lose
 * the others to a filter they did not set. Confirm/dismiss actions and
 * their notice live here because they act on milestones, not on areas.
 */
export function MilestoneList({
  projectId,
  archived,
  detail,
  showFilter,
  onEditMilestone,
  onNavigate,
}: {
  readonly projectId: string;
  readonly archived: boolean;
  readonly detail: MilestoneDetail;
  readonly showFilter: boolean;
  readonly onEditMilestone: (projectId: string, milestoneId: string) => void;
  readonly onNavigate: (to: string) => void;
}) {
  const [status, setStatus] = useState<MilestoneStatus | 'all'>('all');
  const milestones = useMilestones(listMilestones, projectId, showFilter ? status : 'all');
  const [actionFailure, setActionFailure] = useState<MilestoneApiError | null>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const act = async (milestone: Milestone, action: 'confirm' | 'dismiss') => {
    setBusyId(milestone.id);
    setActionFailure(null);
    setActionNotice(null);

    try {
      const saved =
        action === 'confirm'
          ? await confirmMilestone(milestone.id, milestone.revision)
          : await dismissMilestone(milestone.id, milestone.revision);

      // The list is re-read rather than patched locally: the record that is now stored is the one
      // the owner sees, including any revision the action advanced.
      milestones.reload();
      setActionNotice(
        action === 'confirm'
          ? `“${saved.title}” is now confirmed.`
          : `“${saved.title}” is dismissed and kept. Confirm it to bring it back.`,
      );
    } catch (thrown) {
      setActionFailure(thrown instanceof MilestoneApiError ? thrown : null);
      milestones.reload();
    } finally {
      setBusyId(null);
    }
  };

  return (
    // WEB-PERF-003 (web-perf-and-animation). The list owns no section of its
    // own — it renders a fragment — so the container signal needs one. This
    // wrapper carries the busy state and nothing else: no class, no heading,
    // no moved DOM.
    <section data-testid="milestone-list-panel" aria-busy={milestones.loading}>
      {actionNotice ? (
        <p className="notice notice--ok" role="status" data-testid="project-notice">
          {actionNotice}
        </p>
      ) : null}

      {actionFailure ? (
        <p className="notice notice--error" role="alert" data-testid="project-action-error">
          {actionFailure.message}
        </p>
      ) : null}

      {showFilter ? (
        <div className="filters" role="group" aria-label="Filter milestones by status">
          {(['all', 'confirmed', 'candidate', 'dismissed'] as const).map((candidate) => (
            <button
              key={candidate}
              type="button"
              className={candidate === status ? 'filter filter--active' : 'filter'}
              aria-pressed={candidate === status}
              onClick={() => setStatus(candidate)}
              data-testid={`milestone-filter-${candidate}`}
            >
              {candidate === 'all' ? 'All' : candidate}
            </button>
          ))}
        </div>
      ) : null}

      {milestones.loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="milestones-loading">
          Loading milestones…
        </p>
      ) : milestones.failure ? (
        <p className="notice notice--error" role="alert" data-testid="milestones-error">
          {milestones.failure.message}
        </p>
      ) : (milestones.data?.items.length ?? 0) === 0 ? (
        <div className="empty" data-testid="milestones-empty">
          <p className="empty__title">
            {showFilter && status !== 'all' ? `No ${status} milestones.` : 'No milestones recorded yet.'}
          </p>
          <p className="empty__body">
            {showFilter && status !== 'all'
              ? 'Change the filter to see the other milestones.'
              : 'Nothing has been recorded for this project. What you record here becomes the history a story is written from.'}
          </p>
        </div>
      ) : (
        <ul className="milestones" data-testid="milestone-list">
          {milestones.data?.items.map((milestone) => (
            <MilestoneRow
              key={milestone.id}
              milestone={milestone}
              archived={archived}
              busy={busyId === milestone.id}
              detail={detail}
              onEdit={() => onEditMilestone(projectId, milestone.id)}
              onConfirm={() => void act(milestone, 'confirm')}
              onDismiss={() => void act(milestone, 'dismiss')}
              onNavigate={onNavigate}
            />
          ))}
        </ul>
      )}
    </section>
  );
}

function MilestoneRow({
  milestone,
  archived,
  busy,
  detail,
  onEdit,
  onConfirm,
  onDismiss,
  onNavigate,
}: {
  readonly milestone: Milestone;
  readonly archived: boolean;
  readonly busy: boolean;
  readonly detail: MilestoneDetail;
  readonly onEdit: () => void;
  readonly onConfirm: () => void;
  readonly onDismiss: () => void;
  readonly onNavigate: (to: string) => void;
}) {
  // How many collections this milestone has finished, and therefore how many times the evidence
  // panel above has been told to read the bundle again. The count lives here because this row is the
  // only place both panels are in the same scope: neither feature has to know the other exists, and
  // the row is a plain number passed to one and a plain function passed to the other.
  const [settledCollections, setSettledCollections] = useState(0);

  return (
    <li className="milestone" data-testid="milestone" data-status={milestone.status}>
      <div className="milestone__head">
        {/* WEB-A11Y-ROLES-001. Row titles are h2 sections under the view h1. */}
        <h2 className="milestone__title" data-testid="milestone-title">
          {milestone.title}
        </h2>
        <span
          className={
            milestone.status === 'confirmed'
              ? 'badge badge--ok'
              : milestone.status === 'candidate'
                ? 'badge badge--pending'
                : 'badge badge--muted'
          }
          data-testid="milestone-status"
          aria-label={`Status: ${milestone.status}`}
        >
          {/* WEB-A11Y-ROLES-004. The visible status word plus a non-color accessible name. */}
          {milestone.status}
        </span>
      </div>

      <p className="milestone__meta">
        <span data-testid="milestone-type">{milestoneTypeLabel(milestone.type)}</span>
        {milestone.version ? <span data-testid="milestone-version"> · {milestone.version}</span> : null}
        <span data-testid="milestone-occurred-at"> · {formatInstant(milestone.occurredAt)}</span>
        <span data-testid="milestone-revision"> · revision {milestone.revision}</span>
        {milestone.sourceKey ? (
          <span data-testid="milestone-source">
            {' '}
            · from {milestone.source} ({milestone.sourceKey})
          </span>
        ) : null}
      </p>

      {milestone.notes ? <p className="milestone__notes" data-testid="milestone-notes">{milestone.notes}</p> : null}

      {milestone.knownLimitations.length > 0 ? (
        <div className="milestone__list" data-testid="milestone-limitations">
          <h3>Known limitations</h3>
          <ul>
            {milestone.knownLimitations.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </div>
      ) : null}

      {milestone.nextSteps.length > 0 ? (
        <div className="milestone__list" data-testid="milestone-next-steps">
          <h3>Next steps</h3>
          <ul>
            {milestone.nextSteps.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        </div>
      ) : null}

      {detail === 'evidence' ? (
        <>
          {/* What was actually collected for this milestone, read from the server on its own route. The
              reload token is the one thing passed in from below: a collection that finishes appends a
              revision this panel did not ask for, and a token that changed is how it hears about it. */}
          <MilestoneEvidencePanel
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            reloadToken={settledCollections}
          />

          {/* GITHUB-EVIDENCE-001/002. Asking this milestone's repository for a ref, and following the job
              that does it. It goes directly under the evidence panel because the two are the same subject:
              what is collected is appended to the bundle above, and the revision this form sends is the
              revision that bundle is on. Above the draft below, because a draft is written from evidence
              and a collection is how more of it arrives.

              When the job reaches a status it will not leave, once, the evidence panel above is told to
              read the bundle again - so an owner watching a collection land sees what the server holds
              without reloading the page, and no panel here starts polling for a change this row is
              already told about. */}
          <GitHubSyncPanel
            projectId={milestone.projectId}
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            onSettled={() => setSettledCollections((value) => value + 1)}
          />

          {/* LOCAL-GIT-EVIDENCE-001/002. Asking this milestone's local repository for a ref, and following the
              job that does it. It goes directly under the evidence panel for the same reason the panel above does,
              and it raises the same one-shot reload signal when its job reaches a status it will not leave - so a
              background collection of a local checkout refreshes the evidence without any panel here polling for
              a change it has already been told about. */}
          <LocalGitSyncPanel
            projectId={milestone.projectId}
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            onSettled={() => setSettledCollections((value) => value + 1)}
          />

          {/* SCREENSHOT-CURATION-001/002. What the owner chose about those pictures: which are selected
              evidence, which are rejected, which one is primary, in what sequence, and with what words.
              Selecting publishes the picture into the evidence panel above in the same server
              transaction, so a change that advances the bundle tells that panel to read again on its own
              terms - the same token the sync panel already uses, because neither feature has to know the
              other exists. */}
          <ScreenshotPicker
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            onEvidenceChanged={() => setSettledCollections((value) => value + 1)}
          />
        </>
      ) : null}

      {detail === 'content' ? (
        <>
          {/* A bounded draft from exactly that evidence, through a provider the owner named. The panel is
              absent for an archived milestone: a generation is a draft for a live record, and offering
              one for something that can no longer change would invite work nobody can finish. */}
          <LlmProviderArea
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
          />

          {/* GROUNDED-MARKDOWN-DRAFTS-001/002. A draft written from exactly the evidence above, through a
              provider the owner names, and shown with its citations and both lists of findings. It sits under
              the generation panel because it consumes that panel's contract - the consent handshake - and
              drives it itself so an owner sees one flow rather than being sent somewhere else to authorize
              one.

              The panel offers no approval and no edit. A draft is text a human has not reviewed, its body is
              rendered as source rather than as HTML, and a job that stops says the server's own refusal in
              words while re-reading the asset from the server rather than inferring the outcome from a
              status. Human editing and approval are the review package's, bound to a revision and a body
              hash. */}
          <GroundedMarkdownDraftPanel
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            confirmed={milestone.status === 'confirmed'}
          />

          {/* MARKDOWN-REVIEW-EXPORT-001/002. Human editing, approval and export of exactly that
              draft. It sits under the draft panel because it consumes that panel's read contract -
              the stored body, its claims and both finding lists - and adds the three things the
              draft panel deliberately has not: an editor that appends human revisions, an approval
              bound to a revision and a body hash, and an export that downloads approved bytes as a
              file. The preview renders the text as inert characters rather than as a document, and
              the approve control is a button only a press can reach. */}
          <MarkdownReviewPanel
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            confirmed={milestone.status === 'confirmed'}
          />

          {/* CONTENT-ASSET-VARIANTS-001/002. Reusable formats derived from exactly that approved
              story. It sits under the review panel because it consumes that panel's decision - the
              live approval - and adds the one thing neither the draft nor the review panel has: the
              same facts rendered in another shape. Deriving calls no provider, so there is no consent
              to authorize and no job to watch; each derived asset is a draft with its own approve
              control, because approving the story never approves the variant. The preview renders the
              text as inert characters through the review package's own renderer, and the readme-update
              format names its proposal notice rather than offering any control that could write a
              repository. */}
          <ContentVariantPanel
            projectId={milestone.projectId}
            milestoneId={milestone.id}
            archived={archived}
            confirmed={milestone.status === 'confirmed'}
          />

          {/* CONTENT-PACK-PANELS-001/002/003. Read/export panels for the three
              delivered content-pack backends. They sit under the variant panel
              because they consume its subject - derived assets of an approved
              story - and add the one thing it has not: the pack shapes with
              their own derive, inert preview, approval, exact-byte download
              and verbatim publish refusal. Nothing here sends anything. */}
          <LinkedInPackPanel
            projectId={milestone.projectId}
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            confirmed={milestone.status === 'confirmed'}
          />
          <MediumPackPanel
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            confirmed={milestone.status === 'confirmed'}
          />
          <XiaohongshuPackPanel
            projectId={milestone.projectId}
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            confirmed={milestone.status === 'confirmed'}
          />
        </>
      ) : null}

      {detail === 'evolution' ? (
        <>
          {/* Bounded screenshots of one public page for that milestone, which are candidates and nothing
              else. The panel asks for a job rather than producing a picture, and says so while it waits:
              a capture is browser work in the delivered queue, so the owner is told the work was accepted
              and nothing more. It offers no way to select a candidate or attach one to evidence, because
              screenshot-curation owns that and reads these rows below. On a host that cannot confine a
              browser the job answers capture_sandbox_unavailable before any browser starts, and the panel
              reports that refusal as an answer rather than as a spinner that stopped. */}
          <ScreenshotCapturePanel
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
          />

          {/* VIDEO-STORY-PANEL-001/002. The reviewed shot list for that milestone's approved
              story and selected screenshots: duration and source-material pickers over
              delivered routes, an inert shot-order preview, a press-only approval and an
              approval-gated exact-byte download. It sits under the capture panel because it
              depicts the pictures curation selected below it in the evidence area - scripts
              only, no synthesis, no footage, no publication. */}
          <VideoStoryPanel
            projectId={milestone.projectId}
            milestoneId={milestone.id}
            milestoneTitle={milestone.title}
            archived={archived}
            confirmed={milestone.status === 'confirmed'}
          />
        </>
      ) : null}

      {/*
        WORKFLOW-WAYFINDING-001. The workflow-order cross-links: an
        Evidence row reaches the Evolution area where this milestone's
        pages are photographed (captures stay candidates until curation
        selects them), and an Evolution row reaches the Evidence area
        where the choice is published into the bundle. Navigation only —
        no request fires — so these render on archived rows too, unlike
        the acting controls below which stay hidden there.
      */}
      {detail === 'evidence' ? (
        <p className="view__state" data-testid="workflow-evidence-wrap">
          <button
            type="button"
            className="link"
            onClick={() => onNavigate(`/projects/${milestone.projectId}/evolution`)}
            data-testid="workflow-evidence-to-evolution"
          >
            Request screenshots in Evolution
          </button>
        </p>
      ) : null}
      {detail === 'evolution' ? (
        <p className="view__state" data-testid="workflow-evolution-wrap">
          <button
            type="button"
            className="link"
            onClick={() => onNavigate(`/projects/${milestone.projectId}/evidence`)}
            data-testid="workflow-evolution-to-evidence"
          >
            Select into evidence in Evidence
          </button>
        </p>
      ) : null}

      {archived ? null : (
        <div className="milestone__actions">
          <button type="button" className="action" onClick={onEdit} data-testid="milestone-edit">
            Edit
          </button>
          {milestone.status === 'confirmed' ? (
            <button
              type="button"
              className="action"
              onClick={onDismiss}
              disabled={busy}
              data-testid="milestone-dismiss"
            >
              Dismiss
            </button>
          ) : (
            <button
              type="button"
              className="action action--primary"
              onClick={onConfirm}
              disabled={busy}
              data-testid="milestone-confirm"
            >
              Confirm
            </button>
          )}
        </div>
      )}
    </li>
  );
}

/** An ISO instant shown in the reader's own zone, with the UTC value available in the title. */
function formatInstant(iso: string): string {
  const date = new Date(iso);

  if (Number.isNaN(date.getTime()) || iso.length === 0) {
    return iso;
  }

  return date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

