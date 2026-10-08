import { useState } from 'react';

import { checkVoice, createVoiceDraft } from './api';
import { ruleLabel } from './types';
import type { VoiceCatalogue, VoiceCheck, VoiceDraft, VoiceFinding, VoiceProfile } from './types';
import { useVoiceCheck, useVoiceMutation } from './useVoice';

/**
 * The voice-check surface for one project (VOICE-PROFILES-002).
 *
 * The owner writes a title and a body here and the checker says what that text breaks, with the span
 * each rule fired on. Nothing on this surface changes the text: the findings are a list of places to
 * look, and the wording that replaces them is the owner's to write. That is the difference between a
 * finding and a correction, and it is why there is no "fix this" button anywhere in this file.
 *
 * A draft can be saved from here, which is what takes the checked text out of the compose box and
 * into a stored record with its own voice snapshot. From that point the draft is judged under the
 * voice it was created with, and a later profile change cannot reach it.
 */
export function VoiceCheckPanel({
  projectId,
  profile,
  catalogue,
  onDraftSaved,
}: {
  readonly projectId: string;
  readonly profile: VoiceProfile | null;
  readonly catalogue: VoiceCatalogue;
  /** Told when a draft was stored, so the list beside this panel can show it. */
  readonly onDraftSaved: () => void;
}) {
  const check = useVoiceCheck(checkVoice);
  const save = useVoiceMutation<VoiceDraft>();

  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');

  const canCheck =
    profile !== null && !check.checking && (title.trim().length > 0 || body.trim().length > 0);
  const canSave = canCheck && !save.saving;

  return (
    <section className="evidence" data-testid="voice-check-panel">
      {/* WEB-A11Y-ROLES-001. Voice panels are h2 sections under the view h1. */}
      <h2 className="view__heading" data-testid="voice-check-heading">
        Check a draft against this voice
      </h2>
      <p
        className="view__state"
        aria-live="polite"
        aria-busy={profile === null}
        data-testid="voice-check-state"
      >
        {profile === null
          ? 'Loading this project’s voice…'
          : `Checked against ${profile.presetId}${profile.stored ? '' : ' (the defaults)'}. Emoji is ${
              profile.effectiveValues.emoji ? 'allowed' : 'not allowed'
            }, title budget ${profile.effectiveValues.titleMaxChars} characters.`}
      </p>

      <label className="form__field">
        <span className="form__label">Title</span>
        <input
          className="form__input"
          data-testid="voice-check-title"
          value={title}
          maxLength={catalogue.limits.maxTitleTextLength}
          onChange={(event) => setTitle(event.target.value)}
        />
      </label>

      <label className="form__field">
        <span className="form__label">Body</span>
        <textarea
          className="form__input form__input--area"
          rows={6}
          data-testid="voice-check-body"
          value={body}
          maxLength={catalogue.limits.maxBodyTextLength}
          onChange={(event) => setBody(event.target.value)}
        />
      </label>

      <p className="view__actions">
        <button
          type="button"
          className="action"
          data-testid="voice-check-run"
          disabled={!canCheck}
          onClick={() => {
            void check.run(title, body, projectId);
          }}
        >
          {check.checking ? 'Checking…' : 'Check this text'}
        </button>
        <button
          type="button"
          className="action action--primary"
          data-testid="voice-check-save"
          disabled={!canSave}
          onClick={() => {
            void save.run(() => createVoiceDraft(projectId, title, body)).then(onDraftSaved);
          }}
        >
          Save as a draft
        </button>
      </p>

      {check.failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="voice-check-error">
          {check.failure.message}
        </p>
      ) : null}

      {save.failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="voice-check-save-error">
          {save.failure.message}
        </p>
      ) : null}

      {save.lastSaved !== null ? (
        <p className="notice" data-testid="voice-check-saved">
          Saved as a draft under {save.lastSaved.snapshot.presetId}, profile revision{' '}
          {save.lastSaved.snapshot.profileRevision}. Its text is judged under that voice from now on.
        </p>
      ) : null}

      {check.result !== null ? (
        <VoiceCheckResult result={check.result} title={title} body={body} />
      ) : (
        <p className="view__state" data-testid="voice-check-empty">
          Nothing checked yet. Write something above and check it.
        </p>
      )}
    </section>
  );
}

/**
 * The findings for one check, each with the span it fired on.
 *
 * The excerpt is rendered as the server sent it and the offset and length are shown beside it, so
 * the owner can see both which characters a rule matched and where in their own text they are. There
 * is no highlight overlay over the text and no suggested replacement: an overlay would have to
 * re-render the owner's words, and a replacement would be a different product.
 */
function VoiceCheckResult({
  result,
  title,
  body,
}: {
  readonly result: VoiceCheck;
  readonly title: string;
  readonly body: string;
}) {
  if (result.clean) {
    return (
      <div className="empty" data-testid="voice-check-clean">
        <p className="empty__title">No voice violations.</p>
        <p className="view__state" data-testid="voice-check-clean-detail">
          Checked against {result.snapshot.presetId}, profile revision {result.snapshot.profileRevision}.
          Your text was not changed.
        </p>
      </div>
    );
  }

  return (
    <ul className="findings" data-testid="voice-check-findings">
      {result.findings.map((finding, index) => (
        <Finding
          key={`${finding.rule}-${finding.field}-${finding.start}-${index}`}
          finding={finding}
          text={finding.field === 'title' ? title : body}
        />
      ))}
    </ul>
  );
}

/** One finding: what the rule was, what it matched, and exactly where. */
function Finding({ finding, text }: { readonly finding: VoiceFinding; readonly text: string }) {
  // The excerpt is compared against the owner's own text to show that the span really does address
  // what they wrote. A mismatch is displayed rather than hidden, because a finding whose span does
  // not line up is a finding the owner cannot act on.
  const slice = text.slice(finding.start, finding.start + finding.length);
  const lines = text.slice(0, finding.start).split('\n').length;
  const column = finding.start - (text.lastIndexOf('\n', finding.start - 1) + 1) + 1;

  return (
    <li className="finding" data-testid={`voice-finding-${finding.rule}`} data-rule={finding.rule}>
      <p className="finding__rule" data-testid={`voice-finding-rule-${finding.rule}`}>
        {ruleLabel(finding.rule)} in the {finding.field}
      </p>
      <p className="finding__span" data-testid={`voice-finding-span-${finding.rule}`}>
        characters {finding.start + 1}–{finding.start + finding.length} (line {lines}, column{' '}
        {column})
      </p>
      <p className="finding__excerpt" data-testid={`voice-finding-excerpt-${finding.rule}`}>
        {finding.excerpt}
      </p>
      <p className="finding__term" data-testid={`voice-finding-term-${finding.rule}`}>
        matched “{finding.term}” as “{finding.normalizedTerm}”
      </p>
      <p className="finding__message">{finding.message}</p>
      <p className="finding__message" data-testid={`voice-finding-slice-${finding.rule}`}>
        Your text at that offset reads: {slice === finding.excerpt ? finding.excerpt : `“${slice}”`}
      </p>
    </li>
  );
}
