import { useCallback, useEffect, useState } from 'react';

import { createEntry, editEntry, readNarrative, readRefreshOperation, refreshSummary } from './api';
import { NarrativeApiError, type Narrative, type NarrativeEntry } from './types';

/**
 * Explicit project history and its generated summary (NARRATIVE-MEMORY-001/002).
 *
 * <b>Entries and summaries are rendered as different facts.</b> The history section lists what
 * the owner wrote - each line with its kind, its visibility and the count of evidence items it
 * cites - and the summary section shows what the last refresh concluded, with its revision, its
 * staleness and the input hash it is bound to. A reader can never mistake the generated document
 * for the owner's words, because the two never share a section.
 *
 * <b>An edit names the revision it read.</b> Saving sends the entry's revision in `If-Match` and
 * in the body, so an edit against a stale entry is refused with 409 rather than forked: a stale
 * writer loses rather than merging, and the panel reloads so the owner sees what won.
 *
 * <b>A refresh is a job, not an answer.</b> The button asks for one and then follows the
 * delivered operation until it settles; a failed refresh keeps the prior summary on screen under
 * its stale mark rather than clearing it, because a failed regeneration is a labelled old answer
 * rather than a missing one.
 */
export function NarrativeMemoryPanel({
  projectId,
  archived,
}: {
  readonly projectId: string;
  /** An archived project's history is kept and cannot be added to, so no form is offered. */
  readonly archived: boolean;
}) {
  const [narrative, setNarrative] = useState<Narrative | null>(null);
  const [reading, setReading] = useState(true);
  const [readFailure, setReadFailure] = useState<string | null>(null);

  const [kind, setKind] = useState('origin');
  const [text, setText] = useState('');
  const [visibility, setVisibility] = useState<'private' | 'exportable'>('exportable');
  const [saving, setSaving] = useState(false);
  const [saveFailure, setSaveFailure] = useState<NarrativeApiError | null>(null);

  const [editingId, setEditingId] = useState<string | null>(null);
  const [editText, setEditText] = useState('');

  const [refreshing, setRefreshing] = useState(false);
  const [refreshFailure, setRefreshFailure] = useState<NarrativeApiError | null>(null);
  const [refreshStatus, setRefreshStatus] = useState<string | null>(null);

  const load = useCallback(async () => {
    setReading(true);

    try {
      setNarrative(await readNarrative(projectId));
      setReadFailure(null);
    } catch (thrown) {
      setReadFailure(
        thrown instanceof Error ? thrown.message : 'The narrative for this project could not be read.',
      );
    } finally {
      setReading(false);
    }
  }, [projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  const save = useCallback(async () => {
    setSaving(true);
    setSaveFailure(null);

    try {
      await createEntry(projectId, {
        kind,
        occurredAt: new Date().toISOString(),
        text,
        sourceEvidenceIds: [],
        visibility,
      });
      setText('');
      await load();
    } catch (thrown) {
      setSaveFailure(asNarrativeError(thrown));
    } finally {
      setSaving(false);
    }
  }, [kind, load, projectId, text, visibility]);

  const saveEdit = useCallback(
    async (entry: NarrativeEntry) => {
      setSaving(true);
      setSaveFailure(null);

      try {
        await editEntry(projectId, entry.id, entry.revision, { text: editText });
        setEditingId(null);
        await load();
      } catch (thrown) {
        setSaveFailure(asNarrativeError(thrown));
      } finally {
        setSaving(false);
      }
    },
    [editText, load, projectId],
  );

  const refresh = useCallback(async () => {
    setRefreshing(true);
    setRefreshFailure(null);
    setRefreshStatus('refresh requested');

    try {
      const accepted = await refreshSummary(projectId, {
        cutoff: null,
        selectedEntryIds: [],
        idempotencyKey: `narrative-panel-${Date.now()}`,
      });

      // The refresh runs in the delivered queue: poll the operation the accept route named until
      // it settles, then reload whatever the run committed.
      for (let attempt = 0; attempt < 60; attempt += 1) {
        const operation = await readRefreshOperation(accepted.operationId);
        setRefreshStatus(operation.status);

        if (operation.status === 'succeeded' || operation.status === 'failed') {
          break;
        }

        await new Promise((resolve) => setTimeout(resolve, 1000));
      }

      await load();
    } catch (thrown) {
      setRefreshFailure(asNarrativeError(thrown));
    } finally {
      setRefreshing(false);
    }
  }, [load, projectId]);

  if (reading && narrative === null) {
    return (
      <section aria-label="Project narrative">
        <p>Reading the project narrative.</p>
      </section>
    );
  }

  return (
    <section aria-label="Project narrative" data-testid="narrative-memory">
      <h2>Project narrative</h2>

      {readFailure ? (
        <p className="notice notice--error" role="alert">
          {readFailure}
        </p>
      ) : null}

      <div data-testid="narrative-entries">
        <h3>History the owner wrote</h3>

        {narrative !== null && narrative.entries.length === 0 ? (
          <p>No narrative history has been recorded for this project.</p>
        ) : null}

        <ul>
          {(narrative?.entries ?? []).map((entry) => (
            <li key={entry.id} data-testid={`narrative-entry-${entry.id}`}>
              <span data-testid="narrative-entry-kind">{entry.kind}</span>
              {entry.visibility === 'private' ? (
                <span data-testid="narrative-entry-private"> private</span>
              ) : null}
              {editingId === entry.id ? (
                <>
                  <textarea
                    aria-label="Entry text"
                    value={editText}
                    onChange={(event) => setEditText(event.target.value)}
                    data-testid="narrative-edit-text"
                  />
                  <button
                    type="button"
                    disabled={saving || archived}
                    onClick={() => void saveEdit(entry)}
                    data-testid="narrative-edit-save"
                  >
                    Save revision {entry.revision}
                  </button>
                </>
              ) : (
                <>
                  <p data-testid="narrative-entry-text">{entry.text}</p>
                  <button
                    type="button"
                    disabled={archived}
                    onClick={() => {
                      setEditingId(entry.id);
                      setEditText(entry.text);
                    }}
                    data-testid="narrative-edit-start"
                  >
                    Edit
                  </button>
                </>
              )}
            </li>
          ))}
        </ul>
      </div>

      <div data-testid="narrative-summary">
        <h3>Generated summary</h3>

        {narrative?.summary == null ? (
          <p>No summary has been generated for this project.</p>
        ) : (
          <>
            <p data-testid="narrative-summary-revision">Revision {narrative.summary.revision}</p>
            {narrative.summary.stale ? (
              <p className="notice" role="status" data-testid="narrative-summary-stale">
                Stale: a source changed after this summary was built.
              </p>
            ) : null}
          </>
        )}
      </div>

      {archived ? null : (
        <div>
          <h3>Record history</h3>

          <label>
            Kind
            <select
              value={kind}
              onChange={(event) => setKind(event.target.value)}
              data-testid="narrative-kind"
            >
              <option value="origin">origin</option>
              <option value="audience">audience</option>
              <option value="technical-route">technical-route</option>
              <option value="decision">decision</option>
              <option value="problem">problem</option>
              <option value="feedback">feedback</option>
            </select>
          </label>

          <label>
            Visibility
            <select
              value={visibility}
              onChange={(event) => setVisibility(event.target.value as 'private' | 'exportable')}
              data-testid="narrative-visibility"
            >
              <option value="exportable">exportable</option>
              <option value="private">private</option>
            </select>
          </label>

          <label>
            History
            <textarea
              value={text}
              onChange={(event) => setText(event.target.value)}
              data-testid="narrative-text"
            />
          </label>

          <button
            type="button"
            disabled={saving || text.trim().length === 0}
            onClick={() => void save()}
            data-testid="narrative-save"
          >
            Record entry
          </button>

          <button
            type="button"
            disabled={refreshing}
            onClick={() => void refresh()}
            data-testid="narrative-refresh"
          >
            Refresh summary
          </button>

          {refreshStatus !== null ? (
            <p role="status" data-testid="narrative-refresh-status">
              {refreshStatus}
            </p>
          ) : null}

          {saveFailure !== null ? (
            <p className="notice notice--error" role="alert" data-testid="narrative-save-error">
              {saveFailure.message}
            </p>
          ) : null}

          {refreshFailure !== null ? (
            <p className="notice notice--error" role="alert" data-testid="narrative-refresh-error">
              {refreshFailure.message}
            </p>
          ) : null}
        </div>
      )}
    </section>
  );
}

function asNarrativeError(thrown: unknown): NarrativeApiError {
  if (thrown instanceof NarrativeApiError) {
    return thrown;
  }

  return new NarrativeApiError(
    {
      code: 'internal_error',
      message: thrown instanceof Error ? thrown.message : 'The narrative request could not be completed.',
      traceId: '',
    },
    0,
  );
}
