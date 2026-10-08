import { useState } from 'react';

import { useGroundedDraft, type DraftEvidenceOption } from './useDrafts';
import { explainDraftFailure, isSuperseded, type ContentAsset, type GroundingRule } from './types';

/**
 * What a grounded draft is, and the two things a reader has to look at before trusting it
 * (GROUNDED-MARKDOWN-DRAFTS-001/002).
 *
 * <b>This panel's subject is a draft, and a draft is text nobody has approved yet.</b> So three things
 * are load-bearing here and none of them is decoration:
 *
 * - <b>It is shown as source.</b> The body is rendered inside a `<pre>` and never as HTML. A draft can
 *   contain a prompt, a link or a script tag, and a page whose job is to ask a person whether they
 *   believe a paragraph must not be the page that executes it. The review package will own rendering;
 *   this one previews.
 * - <b>The findings are listed, not summarised.</b> A grounding finding and a voice finding are
 *   different things with different remedies, and a badge that said "2 issues" would hide which. Each is
 *   rendered with the rule in words and the claim it is about, and the panel says plainly that neither
 *   list is a verdict - a claim can be perfectly cited and still be wrong about what the citation means.
 * - <b>There is no approve control.</b> Approval is the review package's table, bound to a revision and a
 *   body hash. A button here would either be dead or would be a control this package cannot honour, and
 *   the status shown is read from the server's own row rather than from anything this client could set.
 *
 * <b>On a job that stopped.</b> A draft is queued work, so the panel says the work was accepted and
 * nothing more while it runs. When it stops it shows the server's own refusal in words, names the stored
 * code, and re-reads the asset from the server rather than inferring from the status. A superseded run
 * is shown as what it is - nothing was committed and the previous draft is unchanged - because that is
 * a different situation from a failure and the owner can act on only one of them.
 */
export function GroundedMarkdownDraftPanel({
  milestoneId,
  milestoneTitle,
  archived,
  confirmed,
}: {
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  /** An archived project's drafts are kept and cannot be added to, so no form is offered. */
  readonly archived: boolean;
  /** A milestone the owner has not confirmed gets no form either, and the panel says why. */
  readonly confirmed: boolean;
}) {
  const drafts = useGroundedDraft(milestoneId);
  const [providerId, setProviderId] = useState('');
  const [model, setModel] = useState('');
  const [selected, setSelected] = useState<readonly string[]>([]);

  const usable = drafts.evidence.filter((item) => item.usable);

  // The provider falls back to the first one this installation can actually reach, and the model does
  // not: there is no model list anywhere in this product, because a list is a claim a build cannot keep
  // and the owner knows which model their provider accepts. The field starts empty and says so.
  const effectiveProvider = providerId === '' ? (drafts.providers[0]?.id ?? '') : providerId;
  const effectiveModel = model;
  const terminal = drafts.operation !== null && !isActive(drafts.operation.status);
  const failure = drafts.operation?.errorCode ?? null;
  const explanation = explainDraftFailure(failure);

  const toggle = (id: string) => {
    setSelected((current) =>
      current.includes(id) ? current.filter((value) => value !== id) : [...current, id],
    );
  };

  const submit = () => {
    void drafts.request({
      providerId: effectiveProvider,
      model: effectiveModel,
      selectedEvidenceIds: selected,
    });
  };

  const canSubmit =
    !drafts.busy && effectiveProvider !== '' && effectiveModel !== '' && selected.length > 0;

  return (
    <section className="evidence" data-testid="draft-panel">
      <h4 className="evidence__heading" data-testid="draft-heading">
        Draft
      </h4>

      <p className="view__hint" data-testid="draft-hint">
        A draft is written from this milestone&rsquo;s evidence by a provider you name, and it is a draft:
        nobody has approved it, this page offers no approval, and nothing here has been published
        anywhere. Every claim it makes cites the evidence items it came from, and a figure that appears in
        none of them is refused rather than stored.
      </p>

      {drafts.reading && drafts.asset === null ? (
        <p className="view__state" aria-live="polite" aria-busy="true" data-testid="draft-loading">
          Reading the draft for {milestoneTitle}…
        </p>
      ) : drafts.readFailure !== null && drafts.asset === null ? (
        <p className="notice--error" role="alert" data-testid="draft-failed">
          The draft for this milestone could not be read: {drafts.readFailure}
        </p>
      ) : drafts.asset === null ? (
        <div className="empty" data-testid="draft-empty">
          <p className="empty__title" data-testid="draft-empty-title">
            No draft has been written for {milestoneTitle}
          </p>
          <p className="empty__body" data-testid="draft-empty-body">
            That is what is stored - it is not a placeholder and not a failed read. Choose the evidence
            below and a provider, and the draft will appear here once the job has run. Writing one takes
            provider calls, so it is answered by a job and not immediately.
          </p>
        </div>
      ) : (
        <DraftView asset={drafts.asset} />
      )}

      {archived || !confirmed ? (
        <p className="notice notice--muted" data-testid="draft-unavailable">
          {archived
            ? 'This project is archived, so its drafts are kept and no new one can be requested.'
            : 'This milestone is not confirmed, so no draft is written for it. Confirm it on the milestone and come back.'}
        </p>
      ) : drafts.providers.length === 0 ? (
        <p className="notice notice--muted" data-testid="draft-no-provider">
          No provider in this installation can be called. A draft needs a provider with a stored
          credential and an installed adapter; until one exists there is nothing this page can do, and it
          will not pretend otherwise.
        </p>
      ) : usable.length === 0 ? (
        <p className="notice notice--muted" data-testid="draft-no-evidence">
          Nothing in this milestone&rsquo;s evidence was actually collected, so there is no factual
          material to ground a draft in. Record a manual note on the milestone first - the evidence panel
          above does that - and then ask for the draft.
        </p>
      ) : (
        <form
          className="evidence__form"
          data-testid="draft-form"
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <p className="view__hint" data-testid="draft-revision-hint">
            A draft would be about revision {drafts.evidenceRevision} of this milestone&rsquo;s evidence.
          </p>

          <label className="session__label" htmlFor="draft-provider">
            Provider
          </label>
          <select
            id="draft-provider"
            className="session__input"
            value={effectiveProvider}
            onChange={(event) => setProviderId(event.target.value)}
            data-testid="draft-provider"
          >
            {drafts.providers.map((provider) => (
              <option key={provider.id} value={provider.id}>
                {provider.id}
              </option>
            ))}
          </select>

          <label className="session__label" htmlFor="draft-model">
            Model
          </label>
          <input
            id="draft-model"
            className="session__input"
            value={effectiveModel}
            onChange={(event) => setModel(event.target.value)}
            placeholder="the model your provider accepts"
            data-testid="draft-model"
          />

          <fieldset className="capture__viewports" data-testid="draft-evidence-list">
            <legend className="session__label">Evidence to ground the draft in</legend>
            {usable.map((item) => (
              <EvidenceOption
                key={item.id}
                item={item}
                checked={selected.includes(item.id)}
                onToggle={() => toggle(item.id)}
              />
            ))}
          </fieldset>

          <div className="form__actions">
            <button
              type="submit"
              className="action action--primary"
              disabled={!canSubmit}
              data-testid="draft-submit"
            >
              {drafts.busy ? 'Asking…' : 'Write a draft'}
            </button>
          </div>
        </form>
      )}

      {drafts.authorized !== null ? (
        <p className="view__state" data-testid="draft-authorized">
          The input was frozen and authorized: {drafts.authorized.inputEvidenceIds.length} evidence
          item(s), revision {drafts.authorized.evidenceRevision}, voice revision{' '}
          {drafts.authorized.voiceRevision}. That authorization covers only this input, and it stops
          working at {formatInstant(drafts.authorized.expiresAt)} - the job re-checks it before it sends
          anything, so an authorization that has lapsed is refused rather than used.
        </p>
      ) : null}

      {drafts.accepted !== null && !terminal ? (
        <p className="view__state" data-testid="draft-job-running">
          {drafts.accepted.created
            ? 'The request was accepted and is queued.'
            : 'An identical request was already running, so that job is being followed.'}{' '}
          No draft exists yet. The draft above is re-read when the job reaches a status it will not leave.
        </p>
      ) : null}

      {drafts.progressFailure !== null ? (
        <p className="notice--error" role="alert" data-testid="draft-progress-failed">
          The request was accepted but this panel lost sight of the job: {drafts.progressFailure}
        </p>
      ) : null}

      {terminal && failure !== null ? (
        <div className="notice notice--error" role="alert" data-testid="draft-job-failed">
          <p data-testid="draft-job-failed-code">
            The job stopped with <code>{failure}</code> and wrote no draft.
          </p>
          <p data-testid="draft-job-failed-explanation">
            {explanation ?? 'The server reported a failure this page does not have words for; the code above is what it recorded.'}
          </p>
          {isSuperseded(drafts.operation) ? (
            <p data-testid="draft-job-superseded">
              Whatever you already had is unchanged - nothing replaced it.
            </p>
          ) : null}
        </div>
      ) : terminal ? (
        <p className="view__state" data-testid="draft-job-outcome">
          The job finished. Whatever it stored is shown above.
        </p>
      ) : null}

      {drafts.requestFailure !== null ? (
        <p className="notice--error" role="alert" data-testid="draft-request-failed">
          {drafts.requestFailure.isMilestoneNotConfirmed
            ? 'Nothing was requested: a draft is only written for a confirmed milestone, and this one is not. Confirm it and ask again.'
            : drafts.requestFailure.isNoGrounding
              ? `Nothing was requested: ${drafts.requestFailure.message}`
              : drafts.requestFailure.isConsentStale
                ? `Nothing was requested: ${drafts.requestFailure.message} Nothing was sent to a provider.`
                : `Nothing was requested: ${drafts.requestFailure.message}`}
        </p>
      ) : null}
    </section>
  );
}

/** The stored draft, its provenance and both lists of findings. */
function DraftView({ asset }: { readonly asset: ContentAsset }) {
  return (
    <div data-testid="draft-view">
      <p className="evidence__revision" data-testid="draft-revision">
        Revision {asset.currentRevision} of the draft · <span data-testid="draft-status">{asset.status}</span>{' '}
        · evidence revision {asset.evidenceRevision} · voice revision {asset.voiceRevision}
      </p>

      {/*
        The provenance line, in full. A reader deciding whether to trust a paragraph is entitled to know
        which evidence snapshot it was checked against, which voice it was written under, which provider
        produced it and which instruction and schema versions were in force - all of it is on the stored
        row, and a panel that showed only the prose would be asking for trust it had not earned.
      */}
      <p className="capture__provenance" data-testid="draft-provenance">
        <span data-testid="draft-provider-id">{asset.providerId} / {asset.model}</span>
        <br />
        <span data-testid="draft-versions">
          instruction {asset.promptVersion} · schema {asset.schemaVersion}
        </span>
        <br />
        <span data-testid="draft-voice">written under voice {short(asset.voiceFingerprint)}</span>
        <br />
        <span data-testid="draft-hash">body {asset.bodyHash}</span>
      </p>

      {asset.title !== null ? (
        <h5 className="milestone__title" data-testid="draft-title">
          {asset.title}
        </h5>
      ) : null}

      {asset.summary !== null ? (
        <p className="milestone__notes" data-testid="draft-summary">
          {asset.summary}
        </p>
      ) : null}

      <Findings
        rules={asset.groundingFindings}
        testId="draft-grounding-findings"
        emptyTestId="draft-grounding-clean"
        heading="Grounding"
        intro="These are the things a machine could notice about how the draft cites your evidence. They are not a verdict: a claim can be perfectly cited and still be wrong about what the citation means, and only you can judge that."
        emptyText="Every claim in this draft cites evidence that was collected, and no figure in it appears outside that evidence. That is what this list checks; it is not proof the draft is true."
      />

      <Findings
        rules={asset.voiceFindings}
        testId="draft-voice-findings"
        emptyTestId="draft-voice-clean"
        heading="Voice"
        intro="Checked against the voice this draft was written with, not the project's current one - a profile edited afterwards does not rewrite history."
        emptyText="This draft breaks none of the rules of the voice it was written under."
      />

      {/*
        The body, as source. Never as HTML: a draft is unapproved text that may contain a link, a prompt
        or a tag, and the page asking whether to believe it must not be the page that renders it. The
        citations are visible as written so a reader can follow one back to the claim list above.
      */}
      <pre className="draft__body" data-testid="draft-body">
        {asset.bodyMarkdown ?? ''}
      </pre>

      <details className="draft__claims" data-testid="draft-claims">
        <summary>What this draft claims, and what each claim rests on</summary>
        <ul>
          {asset.claims.map((claim) => (
            <li key={claim.id} data-testid="draft-claim">
              <span data-testid="draft-claim-id">[{claim.id}]</span>{' '}
              <span data-testid="draft-claim-section">{claim.section}</span>
              {' — '}
              <span data-testid="draft-claim-text">{claim.text}</span>
              <br />
              <span data-testid="draft-claim-evidence">
                rests on {claim.evidenceIds.length === 0 ? 'nothing recorded' : `${claim.evidenceIds.length} evidence item(s)`}
              </span>
            </li>
          ))}
        </ul>
      </details>
    </div>
  );
}

/**
 * One list of findings, with its own honest empty state.
 *
 * Two lists rather than one because the remedies differ: a grounding finding is about the evidence the
 * draft rests on, and a voice finding is about how it was written. Merging them into one count is how a
 * reader ends up looking in the wrong place.
 */
function Findings<T>({
  rules,
  testId,
  emptyTestId,
  heading,
  intro,
  emptyText,
}: {
  readonly rules: readonly T[];
  readonly testId: string;
  readonly emptyTestId: string;
  readonly heading: string;
  readonly intro: string;
  readonly emptyText: string;
}) {
  return (
    <div className="draft__findings" data-testid={testId}>
      <h5 className="view__subsection">{heading}</h5>
      <p className="view__hint">{intro}</p>
      {rules.length === 0 ? (
        <p className="view__state" data-testid={emptyTestId}>
          {emptyText}
        </p>
      ) : (
        <ul>
          {rules.map((rule, index) => (
            <li key={index} data-testid={`${testId}-item`}>
              <span data-testid={`${testId}-rule`}>{describe(rule)}</span>
              {' — '}
              <span data-testid={`${testId}-message`}>{messageOf(rule)}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/**
 * One evidence item, with the item's own text shown in full.
 *
 * The owner is choosing what a draft will be allowed to claim, so the choice has to be made against the
 * evidence rather than against a checkbox. An item nothing was collected from is not offered at all,
 * because the server refuses such a selection and offering it would be a control that cannot work.
 */
function EvidenceOption({
  item,
  checked,
  onToggle,
}: {
  readonly item: DraftEvidenceOption;
  readonly checked: boolean;
  readonly onToggle: () => void;
}) {
  return (
    <label className="draft__evidence" data-testid="draft-evidence-option">
      <input type="checkbox" checked={checked} onChange={onToggle} data-testid={`draft-evidence-${item.id}`} />
      <span data-testid="draft-evidence-kind">{item.kind}</span> — {item.text}
    </label>
  );
}

function describe(rule: unknown): string {
  if (typeof rule === 'string') {
    return rule;
  }

  const value = (rule as { readonly rule?: unknown }).rule;

  return typeof value === 'string' ? groundingLabel(value) : 'a finding';
}

function messageOf(rule: unknown): string {
  const value = (rule as { readonly message?: unknown }).message;

  return typeof value === 'string' ? value : '';
}

function groundingLabel(rule: string): string {
  const labels: Readonly<Record<GroundingRule, string>> = {
    claim_unreferenced: 'a claim cites no evidence',
    claim_cited_unavailable: 'a claim rests only on evidence that could not be read',
    claim_evidence_missing: 'a claim cites an item this draft\'s evidence does not contain',
  };

  return labels[rule as GroundingRule] ?? rule;
}

function short(value: string | null): string {
  return value === null ? 'none recorded' : `${value.slice(0, 12)}…`;
}

/** An ISO instant shown in the reader's own zone. */
function formatInstant(iso: string): string {
  const date = new Date(iso);

  if (Number.isNaN(date.getTime()) || iso.length === 0) {
    return iso;
  }

  return date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

function isActive(status: string | undefined): boolean {
  return status === 'queued' || status === 'running' || status === 'retrying';
}
