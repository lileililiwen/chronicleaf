import { useEffect, useId, useState } from 'react';

import { getOperation, listOperations } from '../background-jobs/api';
import type { Operation } from '../background-jobs/types';
import { GitHubEvidenceApiError, readConnection } from '../github-evidence/api';
import type { GitHubConnection } from '../github-evidence/types';
import {
  GitHubCandidateApiError,
  readSubscription,
  requestSync,
} from '../github-release-candidates/api';
import type { GitHubWebhookSubscription } from '../github-release-candidates/types';
import { MilestoneApiError, getProject } from '../projects-and-milestones/api';
import {
  SyncScheduleApiError,
  putSchedule,
  readSchedule,
  type SyncSchedule,
} from './automationsApi';

/**
 * The Automations view (PROJECT-AREA-IA-002).
 *
 * The automatic subsystems, named in one place: connection status,
 * candidate sync (last run + trigger), scheduled sync cadence
 * (view/edit), webhook status, recent jobs with statuses, and publish
 * history. READ and TRIGGER only, through already delivered routes —
 * the one read with no delivered route (per-project publish history)
 * is reported and shown honestly rather than invented (see
 * PublishHistory below). Nothing fires on render or navigation: every
 * route call here is either the initial read that tells the owner what
 * is armed, or an explicit owner act behind its own button.
 */
export function ProjectAutomations({
  projectId,
  archived,
  onNavigate,
}: {
  readonly projectId: string;
  readonly archived: boolean;
  readonly onNavigate: (to: string) => void;
}) {
  return (
    <section className="view" data-testid="automations-view">
      <ConnectionSection projectId={projectId} onNavigate={onNavigate} />
      <CandidateSyncSection projectId={projectId} archived={archived} onNavigate={onNavigate} />
      <ScheduleSection projectId={projectId} archived={archived} />
      <WebhookSection projectId={projectId} onNavigate={onNavigate} />
      <RecentJobsSection projectId={projectId} onNavigate={onNavigate} />
      <PublishHistorySection />
    </section>
  );
}

/** What this project collects from, read from the delivered connection route. */
function ConnectionSection({
  projectId,
  onNavigate,
}: {
  readonly projectId: string;
  readonly onNavigate: (to: string) => void;
}) {
  const [connection, setConnection] = useState<GitHubConnection | null>(null);
  const [missing, setMissing] = useState(false);
  const [failure, setFailure] = useState<GitHubEvidenceApiError | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);
    readConnection(projectId, controller.signal)
      .then((read) => {
        if (active) {
          setConnection(read);
          setMissing(false);
          setFailure(null);
        }
      })
      .catch((thrown: unknown) => {
        if (!active || isAbort(thrown)) {
          return;
        }

        if (thrown instanceof GitHubEvidenceApiError && thrown.isConnectionMissing) {
          setConnection(null);
          setMissing(true);
          setFailure(null);
        } else {
          setFailure(thrown instanceof GitHubEvidenceApiError ? thrown : null);
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
  }, [projectId]);

  return (
    <section className="section" data-testid="automations-connection">
      <h2 className="section__heading">GitHub connection</h2>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="automations-connection-loading">
          Reading the connection…
        </p>
      ) : failure ? (
        <p className="notice notice--error" role="alert" data-testid="automations-connection-error">
          {failure.code}: {failure.message}
        </p>
      ) : missing || connection === null ? (
        <div className="empty" data-testid="automations-connection-empty">
          <p className="empty__title">No repository is connected to this project</p>
          <p className="empty__body" data-testid="automations-connection-payoff">
            Connecting unlocks the automatic subsystems below: candidate sync proposes
            milestones from releases, tags and merged pull requests, and scheduled sync
            runs that poll on a cadence without being asked. Until then there is nothing
            automatic to show.
          </p>
          <button
            type="button"
            className="action"
            onClick={() => onNavigate(`/projects/${projectId}/evidence`)}
            data-testid="automations-connection-connect"
          >
            Connect a repository in Evidence
          </button>
        </div>
      ) : (
        <dl className="facts" data-testid="automations-connection-facts">
          <div>
            <dt>Repository</dt>
            <dd data-testid="automations-connection-repo">
              {connection.owner}/{connection.repository}
            </dd>
          </div>
          <div>
            <dt>Default branch</dt>
            <dd data-testid="automations-connection-branch">{connection.defaultBranch ?? '—'}</dd>
          </div>
          <div>
            <dt>Credential</dt>
            <dd data-testid="automations-connection-credential">
              {connection.hasCredential ? 'Stored in the vault' : 'Public access, no credential'}
            </dd>
          </div>
        </dl>
      )}
    </section>
  );
}

/**
 * The last candidate poll for this project, and the one button that asks
 * for another (PROJECT-AREA-IA-002 Success: 202 with a watched
 * operation).
 */
function CandidateSyncSection({
  projectId,
  archived,
  onNavigate,
}: {
  readonly projectId: string;
  readonly archived: boolean;
  readonly onNavigate: (to: string) => void;
}) {
  const [last, setLast] = useState<Operation | null>(null);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<string | null>(null);
  const [accepted, setAccepted] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const reload = async (signal?: AbortSignal) => {
    const page = await listOperations(signal);
    const runs = page.items.filter(
      (item) => item.kind === 'project.github-candidates-sync' && item.targetId === projectId,
    );

    return runs.length > 0 ? (runs[0] as Operation) : null;
  };

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);
    reload(controller.signal)
      .then((run) => {
        if (active) {
          setLast(run);
          setFailure(null);
        }
      })
      .catch(() => {
        if (active) {
          setFailure('The recent sync runs could not be read.');
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  const syncNow = async () => {
    setBusy(true);
    setFailure(null);
    setAccepted(null);

    try {
      // The poll is conditional on the project as the server last published it, following the
      // candidate panel's own contract: the revision travels as If-Match, not in the body.
      const record = await getProject(projectId);
      const answer = await requestSync(projectId, {
        since: null,
        idempotencyKey: crypto.randomUUID(),
        revision: record.revision,
      });

      setAccepted(answer.operationId);
      setLast(await getOperation(answer.operationId));
    } catch (thrown) {
      if (thrown instanceof GitHubCandidateApiError) {
        setFailure(`${thrown.code}: ${thrown.message}`);
      } else if (thrown instanceof MilestoneApiError) {
        setFailure(`${thrown.code}: ${thrown.message}`);
      } else {
        setFailure('The sync request could not be completed.');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="section" data-testid="automations-sync">
      <h2 className="section__heading">Candidate sync</h2>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="automations-sync-loading">
          Reading the recent sync runs…
        </p>
      ) : failure && last === null && accepted === null ? (
        <p className="notice notice--error" role="alert" data-testid="automations-sync-error">
          {failure}
        </p>
      ) : last === null ? (
        <div className="empty" data-testid="automations-sync-empty">
          <p className="empty__title">Candidate sync has never run for this project.</p>
          <p className="empty__body">
            A sync polls the connected repository for releases, tags and merged pull
            requests and proposes milestones from them. Nothing automatic has happened yet.
          </p>
        </div>
      ) : (
        <dl className="facts" data-testid="automations-sync-last">
          <div>
            <dt>Last run</dt>
            <dd data-testid="automations-sync-status">{last.status}</dd>
          </div>
          <div>
            <dt>Finished</dt>
            <dd data-testid="automations-sync-completed">{last.completedAt ?? '—'}</dd>
          </div>
          {last.errorCode ? (
            <div>
              <dt>Failure code</dt>
              <dd data-testid="automations-sync-error-code">{last.errorCode}</dd>
            </div>
          ) : null}
        </dl>
      )}

      {failure && (last !== null || accepted !== null) ? (
        <p className="notice notice--error" role="alert" data-testid="automations-sync-error">
          {failure}
        </p>
      ) : null}

      {accepted ? (
        <p className="notice notice--ok" role="status" data-testid="automations-sync-accepted">
          Sync requested as operation {accepted}.
        </p>
      ) : null}

      {archived ? null : (
        <p className="view__actions">
          <button
            type="button"
            className="action action--primary"
            onClick={() => void syncNow()}
            disabled={busy}
            data-testid="automations-sync-now"
          >
            {busy ? 'Requesting…' : 'Sync now'}
          </button>
          {accepted ? (
            <button
              type="button"
              className="action"
              onClick={() => onNavigate(`/operations/${accepted}`)}
              data-testid="automations-sync-watch"
            >
              Watch the operation
            </button>
          ) : null}
        </p>
      )}
    </section>
  );
}

/** The stored cadence and the form that arms it, over the delivered schedule routes. */
function ScheduleSection({
  projectId,
  archived,
}: {
  readonly projectId: string;
  readonly archived: boolean;
}) {
  const [stored, setStored] = useState<SyncSchedule | null>(null);
  const [missing, setMissing] = useState(false);
  const [loading, setLoading] = useState(true);
  const [failure, setFailure] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [enabled, setEnabled] = useState(true);
  const [interval, setInterval] = useState('1440');
  const [intervalId] = useId();

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);
    readSchedule(projectId, controller.signal)
      .then((read) => {
        if (active) {
          setStored(read);
          setMissing(false);
          setFailure(null);
          setEnabled(read.enabled);
          setInterval(String(read.intervalMinutes));
        }
      })
      .catch((thrown: unknown) => {
        if (!active || isAbort(thrown)) {
          return;
        }

        if (thrown instanceof SyncScheduleApiError && thrown.isMissing) {
          setStored(null);
          setMissing(true);
          setFailure(null);
        } else if (thrown instanceof SyncScheduleApiError) {
          setFailure(`${thrown.code}: ${thrown.message}`);
        } else {
          setFailure('The schedule could not be read.');
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
  }, [projectId]);

  const save = async () => {
    setBusy(true);
    setFailure(null);
    setNotice(null);

    try {
      const minutes = Number.parseInt(interval, 10);
      const saved = await putSchedule(
        projectId,
        { enabled, intervalMinutes: minutes },
        stored?.revision ?? 0,
      );

      setStored(saved);
      setMissing(false);
      setEnabled(saved.enabled);
      setInterval(String(saved.intervalMinutes));
      setNotice(
        saved.enabled
          ? `Scheduled sync runs every ${saved.intervalMinutes} minutes.`
          : 'Scheduled sync is stored but switched off.',
      );
    } catch (thrown) {
      if (thrown instanceof SyncScheduleApiError) {
        setFailure(`${thrown.code}: ${thrown.message}`);
      } else {
        setFailure('The schedule could not be saved.');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="section" data-testid="automations-schedule">
      <h2 className="section__heading">Scheduled sync</h2>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="automations-schedule-loading">
          Reading the schedule…
        </p>
      ) : (
        <>
          {missing || stored === null ? (
            <div className="empty" data-testid="automations-schedule-empty">
              <p className="empty__title">No scheduled sync is armed for this project.</p>
              <p className="empty__body">
                Arm one below and the project polls its repository on a cadence without
                being asked. Nothing runs until then.
              </p>
            </div>
          ) : (
            <dl className="facts" data-testid="automations-schedule-state">
              <div>
                <dt>Armed</dt>
                <dd data-testid="automations-schedule-enabled">{stored.enabled ? 'On' : 'Off'}</dd>
              </div>
              <div>
                <dt>Cadence</dt>
                <dd data-testid="automations-schedule-interval">
                  Every {stored.intervalMinutes} minutes
                </dd>
              </div>
              <div>
                <dt>Next run</dt>
                <dd data-testid="automations-schedule-next">{stored.nextRunAt}</dd>
              </div>
              <div>
                <dt>Last tick run</dt>
                <dd data-testid="automations-schedule-last">
                  {stored.lastOperation
                    ? `${stored.lastOperation.status}${stored.lastOperation.errorCode ? ` (${stored.lastOperation.errorCode})` : ''}`
                    : 'No tick has fired yet'}
                </dd>
              </div>
            </dl>
          )}

          {notice ? (
            <p className="notice notice--ok" role="status" data-testid="automations-schedule-notice">
              {notice}
            </p>
          ) : null}

          {failure ? (
            <p className="notice notice--error" role="alert" data-testid="automations-schedule-error">
              {failure}
            </p>
          ) : null}

          {archived ? null : (
            <div className="form" data-testid="automations-schedule-form">
              <label className="form__row" htmlFor={intervalId}>
                <span className="form__label">Cadence in minutes (15 to 10080)</span>
                <input
                  id={intervalId}
                  className="form__input"
                  type="number"
                  min={15}
                  max={10080}
                  value={interval}
                  onChange={(event) => setInterval(event.target.value)}
                  data-testid="automations-schedule-interval-input"
                />
              </label>
              <label className="form__row" htmlFor={`${intervalId}-enabled`}>
                <span className="form__label">Armed</span>
                <input
                  id={`${intervalId}-enabled`}
                  className="form__check"
                  type="checkbox"
                  checked={enabled}
                  onChange={(event) => setEnabled(event.target.checked)}
                  data-testid="automations-schedule-enabled-input"
                />
              </label>
              <p className="view__actions">
                <button
                  type="button"
                  className="action action--primary"
                  onClick={() => void save()}
                  disabled={busy}
                  data-testid="automations-schedule-save"
                >
                  {busy ? 'Saving…' : stored === null ? 'Arm scheduled sync' : 'Save the schedule'}
                </button>
              </p>
            </div>
          )}
        </>
      )}
    </section>
  );
}

/** Whether the connection is listening for signed deliveries. */
function WebhookSection({
  projectId,
  onNavigate,
}: {
  readonly projectId: string;
  readonly onNavigate: (to: string) => void;
}) {
  const [subscription, setSubscription] = useState<GitHubWebhookSubscription | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);
    readSubscription(projectId, controller.signal)
      .then((read) => {
        if (active) {
          // A 404 is the ordinary unlistening state, which the client turns into null.
          setSubscription(read);
          setFailure(null);
        }
      })
      .catch(() => {
        if (active) {
          setFailure('The webhook subscription could not be read.');
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
  }, [projectId]);

  return (
    <section className="section" data-testid="automations-webhook">
      <h2 className="section__heading">Webhook</h2>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="automations-webhook-loading">
          Reading the webhook subscription…
        </p>
      ) : failure ? (
        <p className="notice notice--error" role="alert" data-testid="automations-webhook-error">
          {failure}
        </p>
      ) : subscription === null || subscription.enabled !== true ? (
        <div className="empty" data-testid="automations-webhook-empty">
          <p className="empty__title">This project is not listening for deliveries.</p>
          <p className="empty__body">
            Listening turns a release, a tag or a merged pull request into a candidate the
            moment it happens, instead of waiting for the next sync. It is switched on from
            the candidates panel in Evidence.
          </p>
          <button
            type="button"
            className="action"
            onClick={() => onNavigate(`/projects/${projectId}/evidence`)}
            data-testid="automations-webhook-evidence"
          >
            Open Evidence
          </button>
        </div>
      ) : (
        <dl className="facts" data-testid="automations-webhook-state">
          <div>
            <dt>Listening</dt>
            <dd data-testid="automations-webhook-enabled">On</dd>
          </div>
          <div>
            <dt>Secret stored</dt>
            <dd data-testid="automations-webhook-secret">
              {subscription.hasSecret ? 'Yes, in the vault' : 'No secret stored'}
            </dd>
          </div>
          <div>
            <dt>Changed</dt>
            <dd data-testid="automations-webhook-updated">{subscription.updatedAt}</dd>
          </div>
        </dl>
      )}
    </section>
  );
}

/** This project's recent background jobs, read from the delivered operations route. */
function RecentJobsSection({
  projectId,
  onNavigate,
}: {
  readonly projectId: string;
  readonly onNavigate: (to: string) => void;
}) {
  const [jobs, setJobs] = useState<readonly Operation[]>([]);
  const [failure, setFailure] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);
    listOperations(controller.signal)
      .then((page) => {
        if (active) {
          // The operations route is scope-wide; the project half is the
          // targetId this project owns. Nothing here is invented.
          setJobs(page.items.filter((item) => item.targetId === projectId).slice(0, 10));
          setFailure(null);
        }
      })
      .catch(() => {
        if (active) {
          setFailure('The recent jobs could not be read.');
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
  }, [projectId]);

  return (
    <section className="section" data-testid="automations-jobs">
      <h2 className="section__heading">Recent jobs</h2>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="automations-jobs-loading">
          Reading the recent jobs…
        </p>
      ) : failure ? (
        <p className="notice notice--error" role="alert" data-testid="automations-jobs-error">
          {failure}
        </p>
      ) : jobs.length === 0 ? (
        <div className="empty" data-testid="automations-jobs-empty">
          <p className="empty__title">No background job has run for this project yet.</p>
          <p className="empty__body">
            Syncs, captures and generations appear here with the status the queue reports.
          </p>
        </div>
      ) : (
        <ul className="jobs" data-testid="automations-jobs-list">
          {jobs.map((job) => (
            <li key={job.id} data-testid="automations-job">
              <button
                type="button"
                className="link"
                onClick={() => onNavigate(`/operations/${job.id}`)}
                data-testid="automations-job-open"
              >
                {job.kind}
              </button>{' '}
              <span data-testid="automations-job-status">{job.status}</span>
              {job.errorCode ? (
                <span data-testid="automations-job-error"> ({job.errorCode})</span>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/**
 * Publish history (PROJECT-AREA-IA-002 Boundary).
 *
 * The server offers NO per-project publish-history route: receipts are
 * read one attempt at a time (`GET /api/publications/{attemptId}`), and
 * publish operations carry the asset as their target, not the project.
 * So this section states that gap aloud and shows the scope's recent
 * `publication.publish` jobs captioned as scope-wide — the server's own
 * answer, never this project's history dressed up.
 */
function PublishHistorySection() {
  const [jobs, setJobs] = useState<readonly Operation[]>([]);
  const [failure, setFailure] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    setLoading(true);
    listOperations(controller.signal)
      .then((page) => {
        if (active) {
          setJobs(
            page.items.filter((item) => item.kind === 'publication.publish').slice(0, 10),
          );
          setFailure(null);
        }
      })
      .catch(() => {
        if (active) {
          setFailure('The publish jobs could not be read.');
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
  }, []);

  return (
    <section className="section" data-testid="automations-publish-history">
      <h2 className="section__heading">Publish history</h2>

      <p className="view__state" data-testid="automations-publish-note">
        Chronicleaf serves no per-project publish-history route, so this is the
        installation's recent publish jobs rather than this project's own history.
        A receipt is read from the Publish area with its attempt id.
      </p>

      {loading ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="automations-publish-loading">
          Reading the publish jobs…
        </p>
      ) : failure ? (
        <p className="notice notice--error" role="alert" data-testid="automations-publish-error">
          {failure}
        </p>
      ) : jobs.length === 0 ? (
        <div className="empty" data-testid="automations-publish-empty">
          <p className="empty__title">Nothing has been published from this installation yet.</p>
          <p className="empty__body">
            A publish is always an explicit owner act from the Publish area; rendering
            never sends anything.
          </p>
        </div>
      ) : (
        <ul className="jobs" data-testid="automations-publish-list">
          {jobs.map((job) => (
            <li key={job.id} data-testid="automations-publish-job">
              <span data-testid="automations-publish-target">{job.targetId}</span>{' '}
              <span data-testid="automations-publish-status">{job.status}</span>
              {job.errorCode ? (
                <span data-testid="automations-publish-error-code"> ({job.errorCode})</span>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function isAbort(thrown: unknown): boolean {
  return thrown instanceof DOMException && thrown.name === 'AbortError';
}
