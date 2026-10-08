import { useState } from 'react';

import { MilestoneApiError, createMilestone, getProject, updateMilestone } from './api';
import { useProject } from './useRecords';
import { MILESTONE_TYPES, type Milestone, type MilestoneDraft, type MilestoneType } from './types';

/**
 * The milestone editor: create one, or edit one that exists.
 *
 * Two things are worth naming. The form has no status control, because a milestone's status is not a
 * field: a record the owner types is stored confirmed by this save, and a record that arrived from a
 * source waits in the project list for the owner to confirm or dismiss it there. And the editor sends
 * the revision it loaded, so if the record changed in the meantime the save is refused and the owner
 * is told their typing was not written instead of being allowed to believe it was.
 *
 * Known limitations and next steps are one entry per line, which is a plain way to write an ordered
 * list without a control that stores a list the server never asked for.
 */
export function MilestoneEditor({
  projectId,
  milestone,
  onSaved,
  onCancel,
  onNavigate,
}: {
  readonly projectId: string;
  /** `null` creates a milestone; a milestone edits it. */
  readonly milestone: Milestone | null;
  readonly onSaved: () => void;
  readonly onCancel: () => void;
  /** WEB-RESP-006: the Projects crumb navigates through the shell's route reader. */
  readonly onNavigate: (to: string) => void;
}) {
  const [title, setTitle] = useState(milestone?.title ?? '');
  const [type, setType] = useState<MilestoneType>((milestone?.type as MilestoneType) ?? 'feature');
  const [version, setVersion] = useState(milestone?.version ?? '');
  const [occurredAt, setOccurredAt] = useState(() => toLocalInput(milestone?.occurredAt ?? new Date().toISOString()));
  const [notes, setNotes] = useState(milestone?.notes ?? '');
  const [knownLimitations, setKnownLimitations] = useState((milestone?.knownLimitations ?? []).join('\n'));
  const [nextSteps, setNextSteps] = useState((milestone?.nextSteps ?? []).join('\n'));
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<MilestoneApiError | null>(null);
  const [conflict, setConflict] = useState<string | null>(null);
  // WEB-RESP-006: the stored project name for the crumb chain above the h1.
  const project = useProject(getProject, projectId);

  const submit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    if (busy) {
      return;
    }

    const draft: MilestoneDraft = {
      title,
      type,
      version,
      occurredAt,
      notes,
      knownLimitations: toEntries(knownLimitations),
      nextSteps: toEntries(nextSteps),
    };

    setBusy(true);
    setFailure(null);
    setConflict(null);

    try {
      if (milestone === null) {
        await createMilestone(projectId, draft);
      } else {
        await updateMilestone(milestone.id, milestone.revision, draft);
      }

      onSaved();
    } catch (thrown) {
      const apiError = thrown instanceof MilestoneApiError ? thrown : null;

      if (apiError?.status === 409) {
        setConflict(
          apiError.storedRevision === null
            ? 'This milestone changed since you opened it, so your edit was not saved.'
            : `This milestone changed since you opened it (the stored revision is ${apiError.storedRevision}). Your edit was not saved.`,
        );
      } else {
        setFailure(apiError);
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="view" data-testid={milestone === null ? 'milestone-create' : 'milestone-edit'}>
      {/*
        WEB-RESP-006. The chain names where the reader is: Projects › the
        stored project name › Milestone, above the h1 in the delivered
        .view__crumbs paragraph.
      */}
      <p className="view__crumbs" data-testid="milestone-crumbs">
        <button type="button" className="link" onClick={() => onNavigate('/projects')} data-testid="milestone-projects">
          Projects
        </button>
        <span aria-hidden="true"> › </span>
        <span data-testid="milestone-crumb-name">{project.data?.name ?? '…'}</span>
        <span aria-hidden="true"> › </span>
        <span data-testid="milestone-crumb-area">Milestone</span>
      </p>
      <h1 className="view__heading">{milestone === null ? 'New milestone' : `Edit ${milestone.title}`}</h1>

      <p className="view__hint" data-testid="milestone-editor-hint">
        {milestone === null
          ? 'A milestone you record here is confirmed by this save: you wrote it, so there is nothing to review.'
          : 'Editing the fields of this milestone does not change whether it is confirmed.'}
      </p>

      <form className="form" onSubmit={submit} data-testid="milestone-form">
        <div className="form__field">
          <label className="form__label" htmlFor="milestone-title">
            Title
          </label>
          <input
            className="form__input"
            id="milestone-title"
            name="title"
            value={title}
            maxLength={160}
            required
            aria-invalid={hasField(failure, 'title') ? 'true' : undefined}
            aria-describedby={hasField(failure, 'title') ? 'milestone-title-error' : undefined}
            onChange={(event) => setTitle(event.target.value)}
            data-testid="milestone-title-input"
          />
          <FieldError failure={failure} field="title" testId="milestone-title-error" />
        </div>

        <div className="form__row">
          <div className="form__field">
            <label className="form__label" htmlFor="milestone-type">
              Type
            </label>
            <select
              className="form__input"
              id="milestone-type"
              name="type"
              value={type}
              aria-invalid={hasField(failure, 'type') ? 'true' : undefined}
              aria-describedby={hasField(failure, 'type') ? 'milestone-type-error' : undefined}
              onChange={(event) => setType(event.target.value as MilestoneType)}
              data-testid="milestone-type-input"
            >
              {MILESTONE_TYPES.map((candidate) => (
                <option key={candidate} value={candidate}>
                  {candidate}
                </option>
              ))}
            </select>
            <FieldError failure={failure} field="type" testId="milestone-type-error" />
          </div>

          <div className="form__field">
            <label className="form__label" htmlFor="milestone-version">
              Version
            </label>
            <input
              className="form__input"
              id="milestone-version"
              name="version"
              value={version}
              maxLength={80}
              placeholder="v1.0.0"
              aria-invalid={hasField(failure, 'version') ? 'true' : undefined}
              aria-describedby={hasField(failure, 'version') ? 'milestone-version-error' : undefined}
              onChange={(event) => setVersion(event.target.value)}
              data-testid="milestone-version-input"
            />
            <FieldError failure={failure} field="version" testId="milestone-version-error" />
          </div>
        </div>

        <div className="form__field">
          <label className="form__label" htmlFor="milestone-occurred-at">
            Occurred at
          </label>
          <input
            className="form__input"
            id="milestone-occurred-at"
            name="occurredAt"
            type="datetime-local"
            value={occurredAt}
            required
            aria-invalid={hasField(failure, 'occurredAt') ? 'true' : undefined}
            aria-describedby={hasField(failure, 'occurredAt') ? 'milestone-occurred-at-error' : undefined}
            onChange={(event) => setOccurredAt(event.target.value)}
            data-testid="milestone-occurred-at-input"
          />
          <p className="form__hint">Stored in UTC.</p>
          <FieldError failure={failure} field="occurredAt" testId="milestone-occurred-at-error" />
        </div>

        <div className="form__field">
          <label className="form__label" htmlFor="milestone-notes">
            Notes
          </label>
          <textarea
            className="form__input form__input--area"
            id="milestone-notes"
            name="notes"
            rows={4}
            maxLength={20000}
            value={notes}
            aria-invalid={hasField(failure, 'notes') ? 'true' : undefined}
            aria-describedby={hasField(failure, 'notes') ? 'milestone-notes-error' : undefined}
            onChange={(event) => setNotes(event.target.value)}
            data-testid="milestone-notes-input"
          />
          <FieldError failure={failure} field="notes" testId="milestone-notes-error" />
        </div>

        <div className="form__field">
          <label className="form__label" htmlFor="milestone-limitations">
            Known limitations
          </label>
          <textarea
            className="form__input form__input--area"
            id="milestone-limitations"
            name="knownLimitations"
            rows={3}
            value={knownLimitations}
            placeholder="One per line"
            aria-invalid={hasField(failure, 'knownLimitations') ? 'true' : undefined}
            aria-describedby={hasField(failure, 'knownLimitations') ? 'milestone-limitations-error' : undefined}
            onChange={(event) => setKnownLimitations(event.target.value)}
            data-testid="milestone-limitations-input"
          />
          <p className="form__hint">One per line, in the order you want them read.</p>
          <FieldError failure={failure} field="knownLimitations" testId="milestone-limitations-error" />
        </div>

        <div className="form__field">
          <label className="form__label" htmlFor="milestone-next-steps">
            Next steps
          </label>
          <textarea
            className="form__input form__input--area"
            id="milestone-next-steps"
            name="nextSteps"
            rows={3}
            value={nextSteps}
            placeholder="One per line"
            aria-invalid={hasField(failure, 'nextSteps') ? 'true' : undefined}
            aria-describedby={hasField(failure, 'nextSteps') ? 'milestone-next-steps-error' : undefined}
            onChange={(event) => setNextSteps(event.target.value)}
            data-testid="milestone-next-steps-input"
          />
          <FieldError failure={failure} field="nextSteps" testId="milestone-next-steps-error" />
        </div>

        {conflict ? (
          <p className="notice notice--conflict" role="alert" data-testid="milestone-conflict">
            {conflict}
          </p>
        ) : null}

        {failure && !conflict ? (
          <p className="notice notice--error" role="alert" data-testid="milestone-error">
            {failure.message}
          </p>
        ) : null}

        <div className="form__actions">
          <button type="submit" className="action action--primary" disabled={busy} data-testid="milestone-save">
            {busy ? 'Saving…' : 'Save milestone'}
          </button>
          <button type="button" className="action" onClick={onCancel} data-testid="milestone-cancel">
            Cancel
          </button>
        </div>
      </form>
    </section>
  );
}

function FieldError({
  failure,
  field,
  testId,
}: {
  readonly failure: MilestoneApiError | null;
  readonly field: string;
  readonly testId: string;
}) {
  const message = failure?.messageFor(field);

  return message ? (
    <p className="form__error" id={testId} data-testid={testId}>
      {message}
    </p>
  ) : null;
}

/** True when the failure carries a message for the named field. */
function hasField(failure: MilestoneApiError | null, field: string): boolean {
  return failure?.messageFor(field) !== undefined;
}

function toEntries(value: string): string[] {
  return value
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/** An ISO instant as the value a `datetime-local` input accepts, in the browser's own zone. */
function toLocalInput(iso: string): string {
  const date = new Date(iso);

  if (Number.isNaN(date.getTime())) {
    return '';
  }

  const offset = date.getTimezoneOffset() * 60_000;

  return new Date(date.getTime() - offset).toISOString().slice(0, 16);
}
