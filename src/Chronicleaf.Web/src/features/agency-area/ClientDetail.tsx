import { useCallback, useEffect, useState } from 'react';

import {
  getClient,
  listAllProjects,
  listClientProjects,
  readReportContext,
  setProjectClient,
} from './api';
import { AgencyApiError } from './types';
import type { AgencyClient, AgencyClientProject, AgencyProject } from './types';
import { WeeklyReportPanel } from './WeeklyReportPanel';

/**
 * One client's detail (AGENCY-AREA-UI-001/002).
 *
 * The member list, the assign picker and the clear-to-personal control below
 * reach only the delivered assignment route at the project's own revision. A
 * duplicate association at the stored revision is the server's no-op, and an
 * archived client disables new assignment with the reason shown rather than a
 * control that looks available. The weekly-report panel owns everything below
 * the membership.
 */
export function ClientDetail({
  clientId,
  onNavigate,
}: {
  readonly clientId: string;
  readonly onNavigate: (to: string) => void;
}) {
  const [client, setClient] = useState<AgencyClient | null>(null);
  const [members, setMembers] = useState<readonly AgencyClientProject[] | null>(null);
  const [projects, setProjects] = useState<readonly AgencyProject[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<AgencyApiError | null>(null);
  const [assignFailure, setAssignFailure] = useState<AgencyApiError | null>(null);
  const [assigning, setAssigning] = useState(false);
  const [contextNote, setContextNote] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);

    try {
      const [read, grouped, catalogue] = await Promise.all([
        getClient(clientId),
        listClientProjects(clientId),
        listAllProjects(),
      ]);
      setClient(read);
      setMembers(grouped.items);
      setProjects(catalogue.items);
      setFailure(null);
    } catch (thrown) {
      setFailure(toAgencyError(thrown));
    } finally {
      setLoading(false);
    }
  }, [clientId]);

  useEffect(() => {
    void load();
  }, [load]);

  const assign = useCallback(
    async (project: AgencyProject) => {
      if (client === null) {
        return;
      }

      setAssigning(true);
      setAssignFailure(null);
      setContextNote(null);

      try {
        await setProjectClient(project.id, project.revision, client.id);
        await load();
      } catch (thrown) {
        setAssignFailure(toAgencyError(thrown));
      } finally {
        setAssigning(false);
      }
    },
    [client, load],
  );

  const unassign = useCallback(
    async (member: AgencyClientProject) => {
      setAssigning(true);
      setAssignFailure(null);
      setContextNote(null);

      try {
        await setProjectClient(member.id, member.revision, null);
        await load();
      } catch (thrown) {
        setAssignFailure(toAgencyError(thrown));
      } finally {
        setAssigning(false);
      }
    },
    [load],
  );

  const previewContext = useCallback(async () => {
    if (client === null || members === null) {
      return;
    }

    setAssignFailure(null);

    try {
      const context = await readReportContext(
        client.id,
        members.map((member) => member.id),
      );
      setContextNote(
        `Report context at membership generation ${context.contextRevision}: ` +
          context.projects.map((entry) => entry.projectName).join(', '),
      );
    } catch (thrown) {
      setAssignFailure(toAgencyError(thrown));
    }
  }, [client, members]);

  if (loading) {
    return (
      <p className="view__state" data-testid="client-loading">
        Loading this client…
      </p>
    );
  }

  if (failure?.status === 404 || (failure !== null && client === null)) {
    return (
      <section className="view" data-testid="client-missing">
        <h1 className="view__heading">Client not found</h1>
        <p className="view__state">
          {failure !== null ? `${failure.code}: ${failure.message}` : 'Nothing here.'}
        </p>
        <button
          type="button"
          className="action"
          onClick={() => onNavigate('/clients')}
          data-testid="client-missing-back"
        >
          Back to clients
        </button>
      </section>
    );
  }

  if (client === null) {
    return (
      <p className="view__state" data-testid="client-loading">
        Loading this client…
      </p>
    );
  }

  const archived = client.archivedAt !== null;
  const memberIds = new Set((members ?? []).map((member) => member.id));
  const candidates = (projects ?? []).filter(
    (project) => !memberIds.has(project.id) && project.archivedAt === null,
  );

  return (
    <section className="view" data-testid="client-detail">
      <p className="view__crumbs">
        <button
          type="button"
          className="link"
          onClick={() => onNavigate('/clients')}
          data-testid="client-detail-back"
        >
          ← Clients
        </button>
      </p>
      <h1 className="view__heading" data-testid="client-name">
        {client.name}
      </h1>
      <p className="view__state" data-testid="client-notes">
        {client.notes === '' ? 'No notes yet.' : client.notes}
      </p>
      {archived ? (
        <p className="notice" data-testid="client-archived-note">
          Archived: this client keeps its history and takes no new projects. Moving a project away
          stays allowed.
        </p>
      ) : null}

      {assignFailure ? (
        <p className="notice notice--error" role="alert" data-testid="client-assign-error">
          {assignFailure.code}: {assignFailure.message}
        </p>
      ) : null}

      <h2 className="view__heading">Projects</h2>
      {members !== null && members.length === 0 ? (
        <p className="view__state" data-testid="client-projects-empty">
          No projects grouped under this client yet. Assign one below to include it in weekly
          reports.
        </p>
      ) : (
        <ul className="cards" data-testid="client-projects-list">
          {(members ?? []).map((member) => (
            <li key={member.id}>
              <span className="card" data-testid="client-project-card">
                <span className="card__name" data-testid="client-project-name">
                  {member.name}
                </span>
              </span>
              <p className="card__areas">
                <button
                  type="button"
                  className="link"
                  onClick={() => void unassign(member)}
                  disabled={assigning}
                  data-testid="client-project-unassign"
                >
                  Move back to personal
                </button>
              </p>
            </li>
          ))}
        </ul>
      )}

      <h2 className="view__heading">Assign a project</h2>
      {archived ? (
        <p className="view__state" data-testid="client-assign-disabled">
          client_archived: an archived client accepts no new projects. Its history above stays
          readable.
        </p>
      ) : candidates.length === 0 ? (
        <p className="view__state" data-testid="client-assign-empty">
          Every owned project is already grouped here or archived. Create a project first to assign
          it.
        </p>
      ) : (
        <ul className="cards" data-testid="client-assign-list">
          {candidates.map((project) => (
            <li key={project.id}>
              <span className="card" data-testid="client-assign-candidate">
                <span className="card__name">{project.name}</span>
              </span>
              <p className="card__areas">
                <button
                  type="button"
                  className="link"
                  onClick={() => void assign(project)}
                  disabled={assigning}
                  data-testid="client-assign-project"
                >
                  Assign to this client
                </button>
              </p>
            </li>
          ))}
        </ul>
      )}

      <p className="view__actions">
        <button
          type="button"
          className="action"
          onClick={() => void previewContext()}
          data-testid="client-context-preview"
        >
          Preview report context
        </button>
      </p>
      {contextNote ? (
        <p className="view__state" data-testid="client-context-note">
          {contextNote}
        </p>
      ) : null}

      <WeeklyReportPanel clientId={client.id} members={members ?? []} />
    </section>
  );
}

function toAgencyError(thrown: unknown): AgencyApiError {
  if (thrown instanceof AgencyApiError) {
    return thrown;
  }

  return new AgencyApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The request could not be completed.',
      traceId: '',
    },
    0,
  );
}
