import { useState } from 'react';

import { useCapture } from './useCapture';
import {
  CAPTURE_BOUNDS,
  CAPTURE_JOB_CODES,
  CAPTURE_VIEWPORTS,
  isSandboxUnavailable,
  isTargetBlocked,
  type CaptureDraft,
  type ScreenshotCandidate,
} from './types';

/**
 * What was actually photographed for one milestone (SCREENSHOT-CAPTURE-001/002).
 *
 * <b>This panel's subject is a wait.</b> Asking for a capture returns a queued job, never a picture,
 * because the work is bounded browser work that happens in the delivered queue. So the panel has three
 * states that are genuinely different and none of them is dressed up as another: a request that was
 * refused before a job existed, a job that is running, and a job that stopped. Each one is shown as
 * what it is, and the candidates below are re-read from the server when the job ends rather than
 * inferred from a status the browser was told.
 *
 * <b>On a host that cannot confine a browser.</b> The job fails with `capture_sandbox_unavailable` and
 * no browser is ever started, and there is no setting on this page that changes that. The panel says
 * so in those words, says that no page was visited and no picture exists, and offers nothing that
 * would look like a retry. A capture that answered with a spinner which quietly stopped would be
 * claiming an outcome this host cannot produce, and the whole point of the refusal is that it is an
 * answer.
 *
 * <b>What this panel does not do.</b> It does not select, reorder, mark a primary, caption or append
 * anything to evidence. Screenshot-curation owns all of that and consumes these rows; a panel that
 * offered a "make this the screenshot" button would be offering a control this package cannot honour.
 * Each candidate is therefore shown with its provenance - the URL asked for, where that URL ended up,
 * the viewport, the size and when it was captured - and with its state named as `candidate`, which is
 * what it is.
 */
export function ScreenshotCapturePanel({
  milestoneId,
  milestoneTitle,
  archived,
}: {
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  /** An archived project's screenshots are kept and cannot be added to, so no form is offered. */
  readonly archived: boolean;
}) {
  const capture = useCapture(milestoneId);
  const [url, setUrl] = useState('');
  const [url2, setUrl2] = useState('');
  const [url3, setUrl3] = useState('');
  const [viewports, setViewports] = useState<readonly string[]>(['desktop']);
  const [waitSelector, setWaitSelector] = useState('');
  const [waitMs, setWaitMs] = useState('');
  const [fullPage, setFullPage] = useState(false);

  const sandboxUnavailable = isSandboxUnavailable(capture.operation);
  const targetBlocked = isTargetBlocked(capture.operation);
  const jobRunning = capture.accepted !== null && capture.operation === null;
  const terminal = capture.operation !== null && !isActive(capture.operation.status);

  const toggleViewport = (name: string) => {
    setViewports((current) =>
      current.includes(name)
        ? current.filter((viewport) => viewport !== name)
        : [...current, name],
    );
  };

  const submit = () => {
    // The evidence revision is not read here: the hook reads it at the moment the request is made, so
    // a panel left open across the owner's own note asks for what the server currently believes rather
    // than for the revision it happened to mount with.
    const draft: Omit<CaptureDraft, 'expectedBundleRevision'> = {
      urls: [url, url2, url3].map((value) => value.trim()).filter((value) => value.length > 0),
      viewports,
      waitSelector: waitSelector.length === 0 ? null : waitSelector,
      waitMs: toWaitMs(waitMs),
      fullPage,
    };

    void capture.submit(draft);
  };

  const submittable =
    [url, url2, url3].some((value) => value.trim().length > 0) && viewports.length > 0;

  return (
    <section className="evidence" data-testid="capture-panel">
      {/* WEB-A11Y-ROLES-001. Detail panels are h3 under the h2 row title. */}
      <h3 className="evidence__heading" data-testid="capture-heading">
        Screenshots
      </h3>

      {capture.reading && capture.candidates.length === 0 ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="capture-loading">
          Reading what was captured…
        </p>
      ) : capture.readFailure !== null && capture.candidates.length === 0 ? (
        <p className="notice--error" role="alert" data-testid="capture-failed">
          The captured screenshots for this milestone could not be read: {capture.readFailure}
        </p>
      ) : capture.candidates.length === 0 ? (
        <div className="empty" data-testid="capture-empty">
          <p className="empty__title" data-testid="capture-empty-title">
            No screenshot has been captured for {milestoneTitle}
          </p>
          <p className="empty__body" data-testid="capture-empty-body">
            This milestone has no candidates. That is what is stored - it is not a placeholder and not
            a failed read. Ask for a capture below and the pictures will appear here once the job has
            run; a capture is bounded browser work, so it is answered by a job and not immediately.
            {/* WEB-FEED-004. The anchor is offered only when the form below exists:
                an archived milestone keeps its screenshots with no form to link to. */}
            {archived ? null : (
              <>
                {' '}
                <a href="#capture-form" data-testid="capture-empty-link">
                  Go to the capture form
                </a>
              </>
            )}
          </p>
        </div>
      ) : (
        <ul className="evidence__bundles" data-testid="capture-candidates">
          {capture.candidates.map((candidate) => (
            <CandidateView
              key={candidate.id}
              candidate={candidate}
              mediaBaseUrl={capture.mediaBaseUrl}
            />
          ))}
        </ul>
      )}

      {archived ? (
        <p className="notice notice--muted" data-testid="capture-archived">
          This project is archived, so its screenshots are kept and no new capture can be requested.
        </p>
      ) : (
        <form
          className="evidence__form"
          id="capture-form"
          data-testid="capture-form"
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <p className="view__hint" data-testid="capture-form-hint">
            Up to {CAPTURE_BOUNDS.maximumPages} pages and{' '}
            {CAPTURE_BOUNDS.maximumViewports} viewports in one request. A capture is queued work: pressing
            this returns a job, and the pictures appear above when it has run. Candidates are not
            selected or attached to evidence here - curation does that.
          </p>

          {/* Which evidence the capture would be about, so the owner can see it has moved on since the
              page was opened. The revision a request carries is read again at the moment it is made, so
              this is what the panel knows, not a claim about what the server would accept. */}
          <p className="view__hint" data-testid="capture-revision-hint">
            A capture is about revision {capture.bundleRevision} of this milestone's evidence.
          </p>

          <label className="session__label" htmlFor="capture-url">
            The pages to photograph (up to {CAPTURE_BOUNDS.maximumPages}, at least 1 required)
          </label>
          <input
            id="capture-url"
            className="session__input"
            type="url"
            value={url}
            aria-invalid={capture.submitFailure?.messageFor('urls') ? 'true' : undefined}
            aria-describedby={capture.submitFailure?.messageFor('urls') ? 'capture-urls-error' : undefined}
            onChange={(event) => setUrl(event.target.value)}
            placeholder="https://example.com/pricing"
            data-testid="capture-url"
          />
          <label className="session__label" htmlFor="capture-url-2">
            Second page (optional)
          </label>
          <input
            id="capture-url-2"
            className="session__input"
            type="url"
            value={url2}
            aria-invalid={capture.submitFailure?.messageFor('urls') ? 'true' : undefined}
            aria-describedby={capture.submitFailure?.messageFor('urls') ? 'capture-urls-error' : undefined}
            onChange={(event) => setUrl2(event.target.value)}
            placeholder="https://example.com/tour"
            data-testid="capture-url-2"
          />
          <label className="session__label" htmlFor="capture-url-3">
            Third page (optional)
          </label>
          <input
            id="capture-url-3"
            className="session__input"
            type="url"
            value={url3}
            aria-invalid={capture.submitFailure?.messageFor('urls') ? 'true' : undefined}
            aria-describedby={capture.submitFailure?.messageFor('urls') ? 'capture-urls-error' : undefined}
            onChange={(event) => setUrl3(event.target.value)}
            placeholder="https://example.com/changelog"
            data-testid="capture-url-3"
          />
          {capture.submitFailure?.messageFor('urls') !== null &&
          capture.submitFailure?.messageFor('urls') !== undefined ? (
            <p className="notice--error" role="alert" id="capture-urls-error" data-testid="capture-urls-error">
              {capture.submitFailure.messageFor('urls')}
            </p>
          ) : null}

          <fieldset className="capture__viewports" data-testid="capture-viewports">
            <legend className="session__label">Viewports</legend>
            {CAPTURE_VIEWPORTS.map((viewport) => (
              <label key={viewport.name} className="capture__viewport">
                <input
                  type="checkbox"
                  checked={viewports.includes(viewport.name)}
                  onChange={() => toggleViewport(viewport.name)}
                  data-testid={`capture-viewport-${viewport.name}`}
                />
                {viewport.label} ({viewport.size})
              </label>
            ))}
          </fieldset>

          <label className="session__label" htmlFor="capture-wait-selector">
            Wait for a selector, if the page needs one (optional)
          </label>
          <input
            id="capture-wait-selector"
            className="session__input"
            value={waitSelector}
            onChange={(event) => setWaitSelector(event.target.value)}
            placeholder="#chart"
            data-testid="capture-wait-selector"
          />

          <label className="session__label" htmlFor="capture-wait-ms">
            Extra wait in milliseconds, 0 to {CAPTURE_BOUNDS.maximumWaitMs} (optional)
          </label>
          <input
            id="capture-wait-ms"
            className="session__input"
            inputMode="numeric"
            value={waitMs}
            onChange={(event) => setWaitMs(event.target.value)}
            data-testid="capture-wait-ms"
          />

          <label className="capture__fullpage">
            <input
              type="checkbox"
              checked={fullPage}
              onChange={(event) => setFullPage(event.target.checked)}
              data-testid="capture-full-page"
            />
            Photograph the whole scrollable page, not only what is above the fold
          </label>

          <div className="form__actions">
            <button
              type="submit"
              className="action action--primary"
              disabled={capture.busy || !submittable}
              data-testid="capture-submit"
            >
              {capture.busy ? 'Requesting…' : 'Capture these pages'}
            </button>
          </div>
        </form>
      )}

      {/* The accepted job, in the server's own words. `created` is shown because it is the difference
          between "it is running" and "it was already running". */}
      {capture.accepted !== null && !terminal ? (
        <p className="view__state" data-testid="capture-job-running">
          {capture.accepted.created
            ? 'The capture was accepted and is queued.'
            : 'An identical request was already running, so that job is being followed.'}{' '}
          No picture exists yet. The candidates above are re-read when the job reaches a status it will
          not leave.
        </p>
      ) : null}

      {jobRunning ? <p className="view__state" data-testid="capture-job-status">Reading the job…</p> : null}

      {capture.operation !== null && isActive(capture.operation.status) ? (
        <p className="view__state" data-testid="capture-job-status">
          The job is {capture.operation.status}. No picture exists yet.
        </p>
      ) : null}

      {capture.progressFailure !== null ? (
        <p className="notice--error" role="alert" data-testid="capture-progress-failed">
          The job was accepted but this panel lost sight of it: {capture.progressFailure}
        </p>
      ) : null}

      {/* The refusal that is not a failure: the host cannot confine a browser, so no browser was
          started. It is named in the design's own words and it is not a retry. */}
      {sandboxUnavailable ? (
        <div className="empty" data-testid="capture-sandbox-unavailable">
          <p className="empty__title" data-testid="capture-sandbox-unavailable-title">
            This host cannot capture screenshots
          </p>
          <p className="empty__body" data-testid="capture-sandbox-unavailable-body">
            The job stopped with <code>{CAPTURE_JOB_CODES.sandboxUnavailable}</code> before a browser
            was started. Capturing a page means running a browser with no access to this machine's
            network, and this host cannot provide that isolation, so the request was refused rather
            than carried out with weaker controls. No page was visited, no bytes left this host and no
            picture was created - the list above is empty because nothing was captured, not because a
            read failed. This is a property of the host, not of the URL, and no choice on this page
            changes it.
          </p>
        </div>
      ) : null}

      {targetBlocked ? (
        <p className="notice--error" role="alert" data-testid="capture-target-blocked">
          The job refused this destination with <code>{CAPTURE_JOB_CODES.targetBlocked}</code> and no
          browser was started for it. Captures may only go to public pages, so a private, local or
          cloud-metadata address is never visited. The URL policy does not say which of those it was -
          the message is deliberately vague, because confirming the shape of a host on a network is
          itself a disclosure.
        </p>
      ) : null}

      {terminal && !sandboxUnavailable && !targetBlocked ? (
        <p className="view__state" data-testid="capture-job-outcome">
          {capture.operation?.errorCode === CAPTURE_JOB_CODES.timeout
            ? 'The job stopped at the time limit before the page settled, so it published no candidate.'
            : capture.operation?.errorCode !== null && capture.operation?.errorCode !== undefined
              ? `The job stopped with ${capture.operation.errorCode}. Whatever it produced is listed above.`
              : 'The job finished. Whatever it produced is listed above.'}
        </p>
      ) : null}

      {/* A request refused before a job existed. The destination refusal says what was refused and
          stops: the server's message names no host and no range on purpose. */}
      {capture.submitFailure !== null ? (
        <p className="notice--error" role="alert" data-testid="capture-request-failed">
          {capture.submitFailure.isTargetBlocked
            ? 'Nothing was requested: the server refused this destination before a job was created, so no browser was ever started for it. Captures may only go to public pages. The refusal deliberately does not say which address it was - naming it would confirm what this machine can reach, which is the information the policy withholds.'
            : capture.submitFailure.isRevisionConflict
              ? `Nothing was requested: somebody else added evidence to this milestone first, so this was asked for against revision ${capture.requestedRevision ?? 0}, which is now older than the evidence. The newer evidence is shown above - read it, then ask again.`
              : `Nothing was requested: ${capture.submitFailure.message}${
                  capture.submitFailure.messageFor('urls') !== null
                    ? ` ${capture.submitFailure.messageFor('urls')}`
                    : ''
                }${
                  capture.submitFailure.messageFor('viewports') !== null
                    ? ` ${capture.submitFailure.messageFor('viewports')}`
                    : ''
                }`}
        </p>
      ) : null}
    </section>
  );
}

function CandidateView({
  candidate,
  mediaBaseUrl,
}: {
  readonly candidate: ScreenshotCandidate;
  readonly mediaBaseUrl: string;
}) {
  // The bytes are served by the delivered media route, whose base the server publishes. This panel
  // builds no path of its own: a candidate whose picture cannot be read shows as a named candidate
  // with no image rather than as a broken image that reads like a capture problem.
  const source = `${mediaBaseUrl}/${candidate.mediaId}`;
  const redirected = candidate.finalUrl !== candidate.sourceUrl;

  return (
    <li className="evidence__bundle" data-testid="capture-candidate">
      <p className="evidence__revision" data-testid="capture-candidate-viewport">
        {candidate.viewport} · {candidate.width} × {candidate.height} ·{' '}
        <span className="badge" data-testid="capture-candidate-status">
          {candidate.status}
        </span>
      </p>

      <img
        className="capture__image"
        src={source}
        alt={`The page at ${candidate.finalUrl}, captured at ${candidate.viewport} width`}
        data-testid="capture-candidate-image"
      />

      <p className="capture__provenance">
        <span data-testid="capture-candidate-source">Asked for {candidate.sourceUrl}</span>
        <br />
        <span data-testid="capture-candidate-final">
          {redirected ? `Ended at ${candidate.finalUrl}` : candidate.finalUrl}
        </span>
        <br />
        <span data-testid="capture-candidate-captured">Captured {candidate.capturedAt}</span>
      </p>
    </li>
  );
}

function isActive(status: string | undefined): boolean {
  return status === 'queued' || status === 'running' || status === 'retrying';
}

/**
 * A number the owner typed, or nothing.
 *
 * `NaN` is never sent: a wait that is not a whole number is left absent so the server can refuse the
 * request it can see rather than this client guessing what was meant.
 */
function toWaitMs(value: string): number | null {
  const trimmed = value.trim();

  if (trimmed.length === 0) {
    return null;
  }

  const parsed = Number(trimmed);

  return Number.isInteger(parsed) && parsed >= 0 ? parsed : null;
}
