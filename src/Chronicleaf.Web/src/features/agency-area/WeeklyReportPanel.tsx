import { useCallback, useState } from 'react';

import {
  approveReport,
  downloadReport,
  generateReport,
  getReport,
  saveReportDownload,
} from './api';
import { AgencyApiError } from './types';
import type { AgencyClientProject, WeeklyReport } from './types';

/** The Monday of the current local week, as YYYY-MM-DD for the week picker. */
function currentWeekMonday(): string {
  const now = new Date();
  const monday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const offset = (monday.getDay() + 6) % 7;
  monday.setDate(monday.getDate() - offset);

  const month = String(monday.getMonth() + 1).padStart(2, '0');
  const day = String(monday.getDate()).padStart(2, '0');

  return `${monday.getFullYear()}-${month}-${day}`;
}

interface SourceRef {
  readonly projectId: string;
  readonly milestoneId: string;
  readonly bundleId: string | null;
  readonly bundleRevision: number | null;
}

function parseSourceRefs(raw: string): SourceRef[] {
  try {
    const parsed = JSON.parse(raw) as readonly {
      readonly projectId?: unknown;
      readonly milestoneId?: unknown;
      readonly bundleId?: unknown;
      readonly bundleRevision?: unknown;
    }[];

    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed.map((entry) => ({
      projectId: typeof entry.projectId === 'string' ? entry.projectId : '',
      milestoneId: typeof entry.milestoneId === 'string' ? entry.milestoneId : '',
      bundleId: typeof entry.bundleId === 'string' ? entry.bundleId : null,
      bundleRevision: typeof entry.bundleRevision === 'number' ? entry.bundleRevision : null,
    }));
  } catch {
    return [];
  }
}

/**
 * The weekly-report flow for one client (AGENCY-AREA-UI-002).
 *
 * <b>The report is generated, reviewed and carried by hand — never sent.</b>
 * The generate POST renders the selected projects' in-window evidence through
 * the delivered deterministic template (no provider, no model, no recipient),
 * the preview shows the stored body as characters with the bindings it was
 * rendered under, and each download hands the server's exact bytes to a file.
 * Approval binds the shown revision, body hash, attribution hash and source
 * refs: anything that moves afterwards refuses with `approval_stale`, and the
 * remedy named here is regeneration, which appends a new revision that starts
 * unapproved.
 */
export function WeeklyReportPanel({
  clientId,
  members,
}: {
  readonly clientId: string;
  readonly members: readonly AgencyClientProject[];
}) {
  const [weekStart, setWeekStart] = useState(currentWeekMonday);
  const [timeZone, setTimeZone] = useState('UTC');
  const [selected, setSelected] = useState<readonly string[] | null>(null);
  const [inProgressNotes, setInProgressNotes] = useState('');
  const [nextWeekNotes, setNextWeekNotes] = useState('');
  const [report, setReport] = useState<WeeklyReport | null>(null);
  const [working, setWorking] = useState(false);
  const [failure, setFailure] = useState<AgencyApiError | null>(null);
  const [approval, setApproval] = useState<string | null>(null);

  // The selection follows membership until the owner takes it over: a project
  // assigned after the panel mounted must not silently join the next report.
  const effectiveSelection: readonly string[] =
    selected ?? members.map((member) => member.id);

  const toggle = useCallback(
    (projectId: string) => {
      setSelected(
        effectiveSelection.includes(projectId)
          ? effectiveSelection.filter((id) => id !== projectId)
          : [...effectiveSelection, projectId],
      );
    },
    [effectiveSelection],
  );

  const generate = useCallback(async () => {
    setWorking(true);
    setFailure(null);
    setApproval(null);

    try {
      const generated = await generateReport(clientId, {
        projectIds: [...effectiveSelection],
        weekStart: weekStart.trim(),
        timeZone: timeZone.trim(),
        inProgressNotes: inProgressNotes.trim() === '' ? null : inProgressNotes.trim(),
        nextWeekNotes: nextWeekNotes.trim() === '' ? null : nextWeekNotes.trim(),
      });
      setReport(generated);
    } catch (thrown) {
      setFailure(toAgencyError(thrown));
    } finally {
      setWorking(false);
    }
  }, [clientId, effectiveSelection, weekStart, timeZone, inProgressNotes, nextWeekNotes]);

  const refresh = useCallback(async () => {
    if (report === null) {
      return;
    }

    setWorking(true);
    setFailure(null);

    try {
      setReport(await getReport(clientId, report.id));
    } catch (thrown) {
      setFailure(toAgencyError(thrown));
    } finally {
      setWorking(false);
    }
  }, [clientId, report]);

  const approve = useCallback(async () => {
    if (report === null) {
      return;
    }

    setWorking(true);
    setFailure(null);

    try {
      const stamped = await approveReport(clientId, report.id, report.revision);
      setApproval(`Approved revision ${stamped.revision} at ${stamped.approvedAt}.`);
      setReport(await getReport(clientId, report.id));
    } catch (thrown) {
      setFailure(toAgencyError(thrown));
    } finally {
      setWorking(false);
    }
  }, [clientId, report]);

  const download = useCallback(
    async (format: 'markdown' | 'html') => {
      if (report === null) {
        return;
      }

      setWorking(true);
      setFailure(null);

      try {
        const offered = await downloadReport(clientId, report.id, format, null);
        saveReportDownload(offered.fileName, offered.bytes);
      } catch (thrown) {
        setFailure(toAgencyError(thrown));
      } finally {
        setWorking(false);
      }
    },
    [clientId, report],
  );

  const sources = report === null ? [] : parseSourceRefs(report.sourceRefs);
  const names = new Map(members.map((member) => [member.id, member.name] as const));

  return (
    <section data-testid="weekly-report-panel">
      <h2 className="view__heading">Weekly report</h2>
      <p className="view__state" data-testid="weekly-report-about">
        Generated from recorded evidence for the week and projects below. Nothing here is sent
        anywhere: approval is your own review, and each download carries the server&apos;s exact
        bytes.
      </p>

      {failure ? (
        <p className="notice notice--error" role="alert" data-testid="weekly-report-error">
          {failure.code}: {failure.message}
        </p>
      ) : null}

      <p>
        <label htmlFor="weekly-report-week">Week starting (local date)</label>
        <input
          id="weekly-report-week"
          data-testid="weekly-report-week"
          value={weekStart}
          placeholder="YYYY-MM-DD"
          onChange={(event) => setWeekStart(event.target.value)}
        />
      </p>
      <p>
        <label htmlFor="weekly-report-timezone">Time zone (IANA id)</label>
        <input
          id="weekly-report-timezone"
          data-testid="weekly-report-timezone"
          value={timeZone}
          placeholder="UTC"
          onChange={(event) => setTimeZone(event.target.value)}
        />
      </p>

      {members.length === 0 ? (
        <p className="view__state" data-testid="weekly-report-no-projects">
          No projects grouped under this client, so there is nothing to report on yet. Assign a
          project above first.
        </p>
      ) : (
        <fieldset data-testid="weekly-report-projects">
          <legend>Projects in this report</legend>
          {members.map((member) => (
            <p key={member.id}>
              <label>
                <input
                  type="checkbox"
                  data-testid="weekly-report-project"
                  data-project-id={member.id}
                  checked={effectiveSelection.includes(member.id)}
                  onChange={() => toggle(member.id)}
                />{' '}
                {member.name}
              </label>
            </p>
          ))}
        </fieldset>
      )}

      <p>
        <label htmlFor="weekly-report-in-progress">In progress (your notes)</label>
        <textarea
          id="weekly-report-in-progress"
          data-testid="weekly-report-in-progress"
          value={inProgressNotes}
          onChange={(event) => setInProgressNotes(event.target.value)}
        />
      </p>
      <p>
        <label htmlFor="weekly-report-next-week">Next week (your notes)</label>
        <textarea
          id="weekly-report-next-week"
          data-testid="weekly-report-next-week"
          value={nextWeekNotes}
          onChange={(event) => setNextWeekNotes(event.target.value)}
        />
      </p>

      <p className="view__actions">
        <button
          type="button"
          className="action action--primary"
          onClick={() => void generate()}
          disabled={working}
          data-testid="weekly-report-generate"
        >
          {report === null ? 'Generate report' : 'Regenerate report'}
        </button>{' '}
        {report !== null ? (
          <button
            type="button"
            className="action"
            onClick={() => void refresh()}
            disabled={working}
            data-testid="weekly-report-refresh"
          >
            Re-read report
          </button>
        ) : null}
      </p>

      {report === null ? (
        <p className="view__state" data-testid="weekly-report-empty">
          No report generated for this selection yet.
        </p>
      ) : (
        <article data-testid="weekly-report-preview">
          <p className="view__state" data-testid="weekly-report-meta">
            Revision {report.revision} · week {report.weekStart} ({report.timeZone}) · window{' '}
            {report.windowStartUtc} to {report.windowEndUtc} · attribution {report.attributionHash}
          </p>
          <p className="view__state" data-testid="weekly-report-status">
            {report.approved ? 'approved' : 'draft'}
          </p>
          {approval ? (
            <p className="view__state" data-testid="weekly-report-approval-note">
              {approval}
            </p>
          ) : null}

          <h3 className="view__heading">Sources</h3>
          {sources.length === 0 ? (
            <p className="view__state" data-testid="weekly-report-sources-empty">
              No recorded sources in this window.
            </p>
          ) : (
            <ul data-testid="weekly-report-sources">
              {sources.map((source) => (
                <li key={`${source.projectId}-${source.milestoneId}`} data-testid="weekly-report-source">
                  {names.get(source.projectId) ?? source.projectId} · milestone{' '}
                  {source.milestoneId}
                  {source.bundleId ? (
                    <> · evidence revision {source.bundleRevision}</>
                  ) : (
                    <> · no source evidence recorded</>
                  )}
                </li>
              ))}
            </ul>
          )}

          <h3 className="view__heading">Body</h3>
          <pre data-testid="weekly-report-body">{report.bodyMarkdown}</pre>

          <p className="view__actions">
            <button
              type="button"
              className="action action--primary"
              onClick={() => void approve()}
              disabled={working}
              data-testid="weekly-report-approve"
            >
              Approve revision {report.revision}
            </button>{' '}
            <button
              type="button"
              className="action"
              onClick={() => void download('markdown')}
              disabled={working || !report.approved}
              title={report.approved ? undefined : 'Approve the report before exporting it.'}
              data-testid="weekly-report-download-markdown"
            >
              Download Markdown
            </button>{' '}
            <button
              type="button"
              className="action"
              onClick={() => void download('html')}
              disabled={working || !report.approved}
              title={report.approved ? undefined : 'Approve the report before exporting it.'}
              data-testid="weekly-report-download-html"
            >
              Download HTML
            </button>
          </p>
          {!report.approved ? (
            <p className="view__state" data-testid="weekly-report-download-unavailable">
              Export unlocks behind a live approval: generating again or moving evidence ends the
              current approval until re-approval.
            </p>
          ) : null}
        </article>
      )}
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
