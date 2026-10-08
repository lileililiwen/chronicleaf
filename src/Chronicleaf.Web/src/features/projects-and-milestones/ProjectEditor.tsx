import { useState } from 'react';

import { MilestoneApiError, createProject, updateProject } from './api';
import type { ProjectDraft } from './types';
import { FieldCount } from '../ui-feedback/FieldCount';
import { formatRevisionConflict } from '../ui-feedback/revisionConflict';
import { useToast } from '../ui-feedback/Toast';

/**
 * Creating a project, and editing one that already exists.
 *
 * The form owns the fields the API accepts and nothing else: there is no archive switch here, no
 * connection picker and no milestone field, because those are separate decisions made elsewhere. The
 * revision the editor was opened at is held in state and sent back on save, so a save that was based
 * on a stale view is refused by the server and reported here rather than overwriting the newer edit.
 */
export function ProjectEditor({
  project,
  onSaved,
  onCancel,
}: {
  /** `null` creates a project; a project edits it. */
  readonly project: { readonly id: string; readonly revision: number; readonly name: string; readonly description: string; readonly projectUrl: string | null } | null;
  readonly onSaved: (projectId: string) => void;
  readonly onCancel: () => void;
}) {
  const [name, setName] = useState(project?.name ?? '');
  const [description, setDescription] = useState(project?.description ?? '');
  const [projectUrl, setProjectUrl] = useState(project?.projectUrl ?? '');
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<MilestoneApiError | null>(null);
  const [conflict, setConflict] = useState<string | null>(null);
  // WEB-FEED-001: outcomes are announced through the shell-level toast region.
  const { showToast } = useToast();

  const submit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    if (busy) {
      return;
    }

    const draft: ProjectDraft = { name, description, projectUrl };
    setBusy(true);
    setFailure(null);
    setConflict(null);

    try {
      const saved =
        project === null
          ? await createProject(draft)
          : await updateProject(project.id, project.revision, draft);

      showToast('Project saved.', 'ok');
      onSaved(saved.id);
      return;
    } catch (thrown) {
      const apiError = thrown instanceof MilestoneApiError ? thrown : null;

      if (apiError?.status === 409) {
        // Somebody else's edit landed first. The values in this form were not written, and the
        // owner is told that rather than being left to assume they were. WEB-FEED-003: the one
        // shared conflict sentence; the inline notice below renders the same words.
        const copy = formatRevisionConflict(apiError.storedRevision);
        setConflict(copy);
        showToast(copy, 'error');
      } else {
        setFailure(apiError);
        showToast(apiError?.message ?? 'The project was not saved.', 'error');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="view" data-testid={project === null ? 'project-create' : 'project-edit'}>
      <h1 className="view__heading">{project === null ? 'New project' : `Edit ${project.name}`}</h1>

      <form className="form" onSubmit={submit} data-testid="project-form">
        <div className="form__field">
          <label className="form__label" htmlFor="project-name">
            Name
          </label>
          <input
            className="form__input"
            id="project-name"
            name="name"
            value={name}
            maxLength={120}
            required
            aria-invalid={failure?.messageFor('name') ? 'true' : undefined}
            aria-describedby={failure?.messageFor('name') ? 'project-name-error' : undefined}
            onChange={(event) => setName(event.target.value)}
            data-testid="project-name-input"
          />
          <FieldCount value={name} max={120} testId="project-name-count" />
          {failure?.messageFor('name') ? (
            <p
              className="form__error"
              id="project-name-error"
              data-testid="project-name-error"
            >
              {failure.messageFor('name')}
            </p>
          ) : null}
        </div>

        <div className="form__field">
          <label className="form__label" htmlFor="project-description">
            Description
          </label>
          <textarea
            className="form__input form__input--area"
            id="project-description"
            name="description"
            rows={3}
            maxLength={2000}
            value={description}
            onChange={(event) => setDescription(event.target.value)}
            data-testid="project-description-input"
          />
          <FieldCount value={description} max={2000} testId="project-description-count" />
        </div>

        <div className="form__field">
          <label className="form__label" htmlFor="project-url">
            Project URL
          </label>
          <input
            className="form__input"
            id="project-url"
            name="projectUrl"
            type="url"
            placeholder="https://"
            value={projectUrl}
            aria-invalid={failure?.messageFor('projectUrl') ? 'true' : undefined}
            aria-describedby={failure?.messageFor('projectUrl') ? 'project-url-error' : undefined}
            onChange={(event) => setProjectUrl(event.target.value)}
            data-testid="project-url-input"
          />
          <p className="form__hint">
            Stored as you type it. Chronicleaf never opens this address itself.
          </p>
          {failure?.messageFor('projectUrl') ? (
            <p
              className="form__error"
              id="project-url-error"
              data-testid="project-url-error"
            >
              {failure.messageFor('projectUrl')}
            </p>
          ) : null}
        </div>

        {conflict ? (
          <p className="notice notice--conflict" role="alert" data-testid="project-conflict">
            {conflict}
          </p>
        ) : null}

        {failure && !conflict ? (
          <p className="notice notice--error" role="alert" data-testid="project-error">
            {failure.message}
          </p>
        ) : null}

        <div className="form__actions">
          <button type="submit" className="action action--primary" disabled={busy} data-testid="project-save">
            {busy ? 'Saving…' : 'Save project'}
          </button>
          <button type="button" className="action" onClick={onCancel} data-testid="project-cancel">
            Cancel
          </button>
        </div>
      </form>
    </section>
  );
}
