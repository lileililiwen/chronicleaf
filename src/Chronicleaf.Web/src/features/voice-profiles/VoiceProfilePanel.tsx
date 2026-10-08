import { useEffect, useState } from 'react';

import { VoiceApiError, VoiceInputError, readVoiceProfile, saveVoiceProfile } from './api';
import { NO_OVERRIDES, fieldLabel } from './types';
import type {
  VoiceCatalogue,
  VoiceLanguage,
  VoiceMarketingLevel,
  VoiceOverrides,
  VoiceProfile,
  VoiceTechnicalDetail,
  VoiceTone,
} from './types';
import { useVoiceMutation, useVoiceProfile } from './useVoice';

/**
 * The owner-facing voice editor for one project (VOICE-PROFILES-001).
 *
 * Three things are on screen at once, and that is the design of the surface rather than an accident
 * of layout:
 *
 * - the preset, as the owner would get it with no changes;
 * - the explicit overrides, each with a control that can say "no override" as well as a value;
 * - the effective resolved values, and per field which of the two produced them.
 *
 * The third is what makes the requirement visible. A profile where the preset asks for emoji and the
 * owner turned it off shows `preset: true`, `override: false`, `effective: false` on one row, so the
 * owner can see the override won rather than infer it from a value that happens to be false.
 *
 * A save is only offered once the read that publishes the revision has landed. Before that there is
 * no token to present, and a write with a placeholder revision would be refused and would strand the
 * owner's edits - so the control stays disabled and says why.
 */
export function VoiceProfilePanel({
  projectId,
  catalogue,
}: {
  readonly projectId: string;
  readonly catalogue: VoiceCatalogue;
}) {
  const profile = useVoiceProfile(readVoiceProfile, projectId);
  const save = useVoiceMutation<{ readonly profile: VoiceProfile; readonly revision: number }>();

  const [presetId, setPresetId] = useState<string>('');
  const [overrides, setOverrides] = useState<VoiceOverrides>(NO_OVERRIDES);
  const [edited, setEdited] = useState(false);

  // The editor is seeded from the answer that actually landed, and only while the owner has not
  // started changing anything: re-seeding mid-edit would discard what they typed.
  useEffect(() => {
    if (profile.profile !== null && !edited) {
      setPresetId(profile.profile.presetId);
      setOverrides(profile.profile.overrides);
    }
  }, [profile.profile, edited]);

  useEffect(() => {
    if (save.lastSaved !== null && !save.saving) {
      setEdited(false);
      profile.reload();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [save.lastSaved]);

  if (profile.failure?.status === 404) {
    return (
      <section className="evidence" data-testid="voice-profile-missing">
        {/* WEB-A11Y-ROLES-001. Voice panels are h2 sections under the view h1. */}
        <h2 className="view__heading">Project not found</h2>
        <p className="view__state">
          This project is not in your installation, so it has no voice to edit. Nothing was changed.
        </p>
      </section>
    );
  }

  if (profile.loading || profile.profile === null) {
    return (
      <p className="view__state" aria-live="polite" aria-busy="true" data-testid="voice-profile-loading">
        Loading this project's voice…
      </p>
    );
  }

  /**
   * Records one override, or clears it by setting null.
   *
   * The update is a function of the current record so two controls changed in the same tick cannot
   * drop each other's field - which is the one way a three-state override could quietly become a
   * two-state one.
   */
  const override = <K extends keyof VoiceOverrides>(field: K, value: VoiceOverrides[K]) => {
    setOverrides((current) => ({ ...current, [field]: value }));
    setEdited(true);
  };

  const selected = catalogue.presets.find((preset) => preset.id === presetId) ?? catalogue.presets[0];
  const effective = effectiveValues(selected, overrides);
  const canSave = profile.revision !== null && !save.saving && !profile.loading;

  return (
    <section className="evidence" data-testid="voice-profile-panel">
      <h2 className="view__heading" data-testid="voice-profile-heading">
        Writing voice
      </h2>
      <p className="view__state" data-testid="voice-profile-state">
        {profile.profile.stored
          ? `Preset ${profile.profile.presetId}, revision ${profile.profile.revision}.`
          : 'No voice saved yet. The defaults below are what this project is using.'}
      </p>

      <label className="form__field">
        <span className="form__label">Preset</span>
        <select
          className="form__input"
          value={presetId}
          data-testid="voice-preset"
          onChange={(event) => {
            setPresetId(event.target.value);
            setEdited(true);
          }}
        >
          {catalogue.presets.map((preset) => (
            <option key={preset.id} value={preset.id}>
              {preset.label}
            </option>
          ))}
        </select>
      </label>
      <p className="form__hint" data-testid="voice-preset-description">
        {selected?.description ?? ''}
      </p>

      <fieldset className="voice-overrides" data-testid="voice-overrides">
        <legend className="voice-overrides__legend">Your overrides</legend>
        <p className="form__hint">
          An override replaces the preset for that field only. Leaving one unset keeps the preset's
          value; setting one is what wins.
        </p>

        <OverrideChoice
          field="emoji"
          value={overrides.emoji}
          onChange={(value) => override('emoji', value)}
          options={[
            { value: null, label: 'Preset' },
            { value: true, label: 'On' },
            { value: false, label: 'Off' },
          ]}
          testId="voice-override-emoji"
        />

        <OverrideChoice
          field="firstPerson"
          value={overrides.firstPerson}
          onChange={(value) => override('firstPerson', value)}
          options={[
            { value: null, label: 'Preset' },
            { value: true, label: 'On' },
            { value: false, label: 'Off' },
          ]}
          testId="voice-override-firstPerson"
        />

        <OverrideChoice
          field="tone"
          value={overrides.tone}
          onChange={(value) => override('tone', value)}
          options={[
            { value: null, label: 'Preset' },
            ...catalogue.tones.map((tone) => ({ value: tone as VoiceTone, label: tone })),
          ]}
          testId="voice-override-tone"
        />

        <OverrideChoice
          field="language"
          value={overrides.language}
          onChange={(value) => override('language', value)}
          options={[
            { value: null, label: 'Preset' },
            ...catalogue.languages.map((language) => ({ value: language as VoiceLanguage, label: language })),
          ]}
          testId="voice-override-language"
        />

        <OverrideChoice
          field="technicalDetail"
          value={overrides.technicalDetail}
          onChange={(value) => override('technicalDetail', value)}
          options={[
            { value: null, label: 'Preset' },
            ...catalogue.technicalDetails.map((detail) => ({
              value: detail as VoiceTechnicalDetail,
              label: detail,
            })),
          ]}
          testId="voice-override-technicalDetail"
        />

        <OverrideChoice
          field="marketing"
          value={overrides.marketing}
          onChange={(value) => override('marketing', value)}
          options={[
            { value: null, label: 'Preset' },
            ...catalogue.marketingLevels.map((level) => ({ value: level as VoiceMarketingLevel, label: level })),
          ]}
          testId="voice-override-marketing"
        />

        <OverrideText
          field="titleMaxChars"
          value={overrides.titleMaxChars}
          min={catalogue.limits.minTitleMaxChars}
          max={catalogue.limits.maxTitleMaxChars}
          onChange={(value) => override('titleMaxChars', value)}
          testId="voice-override-titleMaxChars"
        />

        <OverrideTerms
          value={overrides.forbiddenTerms}
          max={catalogue.limits.maxForbiddenTerms}
          termMaxLength={catalogue.limits.forbiddenTermMaxLength}
          onChange={(value) => override('forbiddenTerms', value)}
          testId="voice-override-forbiddenTerms"
        />
      </fieldset>

      <table className="voice-table" data-testid="voice-effective">
        <caption className="voice-table__caption">What each field resolves to</caption>
        <thead>
          <tr>
            <th scope="col">Field</th>
            <th scope="col">Preset</th>
            <th scope="col">Effective</th>
            <th scope="col">Source</th>
          </tr>
        </thead>
        <tbody>
          {resolutions(selected, overrides).map((resolution) => (
            <tr key={resolution.field} data-testid={`voice-effective-${resolution.field}`}>
              <th scope="row">{fieldLabel(resolution.field)}</th>
              <td data-testid={`voice-preset-${resolution.field}`}>{resolution.presetValue}</td>
              <td data-testid={`voice-value-${resolution.field}`}>{resolution.effectiveValue}</td>
              <td data-testid={`voice-source-${resolution.field}`}>
                {resolution.overridden ? 'Your override' : 'Preset'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {save.failure !== null ? (
        <p className="notice notice--error" role="alert" data-testid="voice-profile-error">
          {save.failure instanceof VoiceInputError || save.failure.isMissingRevision
            ? 'The voice was not saved: the revision this change replaces is not known. Reload and try again.'
            : save.failure.message}
        </p>
      ) : null}


      {save.failure instanceof VoiceApiError && save.failure.isRevisionConflict ? (
        <p className="notice notice--muted" data-testid="voice-profile-conflict">
          This project's voice moved since you opened this editor, so nothing was changed. The
          effective values below are the ones now stored.
        </p>
      ) : null}

      <p className="view__actions">
        <button
          type="button"
          className="action action--primary"
          data-testid="voice-profile-save"
          disabled={!canSave}
          onClick={() => {
            if (profile.revision === null) {
              return;
            }

            void save.run(() =>
              saveVoiceProfile(projectId, presetId, overrides, profile.revision as number),
            );
          }}
        >
          Save voice
        </button>
      </p>

      {profile.revision === null ? (
        <p className="form__hint" data-testid="voice-profile-no-revision">
          Saving becomes available once this project's current voice has loaded.
        </p>
      ) : null}

      <p className="view__state" data-testid="voice-effective-summary">
        Emoji is {effective.emoji ? 'on' : 'off'}, tone is {effective.tone}, title budget is{' '}
        {effective.titleMaxChars} characters.
      </p>
    </section>
  );
}

interface Choice<T> {
  readonly value: T;
  readonly label: string;
}

/**
 * One three-state override control: unset, or one of two values.
 *
 * The "Preset" entry is what makes the control honest. Without it a select could not say "no
 * override", and the owner would have no way back to the preset's value once they had chosen their
 * own - the saved state and the control would disagree.
 */
function OverrideChoice<T>({
  field,
  value,
  options,
  onChange,
  testId,
}: {
  readonly field: string;
  readonly value: T | null;
  readonly options: readonly Choice<T | null>[];
  readonly onChange: (value: T | null) => void;
  readonly testId: string;
}) {
  const selected = options.some((option) => option.value === value)
    ? value
    : (options[0].value as T | null);

  return (
    <label className="form__field">
      <span className="form__label">{fieldLabel(field)}</span>
      <select
        className="form__input"
        value={String(selected ?? '')}
        data-testid={testId}
        onChange={(event) => {
          // A select's value is always a string, so the chosen option is looked up by that string
          // and its real value returned. Passing the string through would turn a boolean false into
          // the truthy "false", which every resolver would then read as "on".
          const raw = event.target.value;
          const chosen = options.find((option) => String(option.value ?? '') === raw);

          onChange(chosen === undefined ? null : (chosen.value as T | null));
        }}
      >
        {options.map((option) => (
          <option key={option.value === null ? 'preset' : String(option.value)} value={String(option.value ?? '')}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  );
}

/** One numeric override, with the range the server accepts shown next to it. */
function OverrideText({
  field,
  value,
  min,
  max,
  onChange,
  testId,
}: {
  readonly field: string;
  readonly value: number | null;
  readonly min: number;
  readonly max: number;
  readonly onChange: (value: number | null) => void;
  readonly testId: string;
}) {
  return (
    <label className="form__field">
      <span className="form__label">{fieldLabel(field)}</span>
      <input
        className="form__input"
        type="number"
        min={min}
        max={max}
        data-testid={testId}
        value={value === null ? '' : String(value)}
        placeholder="Preset"
        onChange={(event) => {
          const raw = event.target.value;
          onChange(raw === '' ? null : Number(raw));
        }}
      />
      <span className="form__hint">Between {min} and {max}. Empty means the preset's value.</span>
    </label>
  );
}

/**
 * The forbidden-term list, one term per line.
 *
 * A textarea rather than a repeating row of inputs, because the list is short, the owner pastes a
 * list far more often than they add a single word, and a whole-list replace is one undo rather than
 * several. The text is split and trimmed only for reading the control's value: what is sent is the
 * list, and the server is what judges it.
 */
function OverrideTerms({
  value,
  max,
  termMaxLength,
  onChange,
  testId,
}: {
  readonly value: readonly string[] | null;
  readonly max: number;
  readonly termMaxLength: number;
  readonly onChange: (value: readonly string[] | null) => void;
  readonly testId: string;
}) {
  return (
    <label className="form__field">
      <span className="form__label">{fieldLabel('forbiddenTerms')}</span>
      <textarea
        className="form__input form__input--area"
        rows={4}
        data-testid={testId}
        defaultValue={(value ?? []).join('\n')}
        placeholder="One term per line. Empty means the preset's list."
        onChange={(event) => {
          const raw = event.target.value;
          onChange(raw.trim().length === 0 ? null : raw.split('\n').map((term) => term.trim()).filter((term) => term.length > 0));
        }}
      />
      <span className="form__hint">
        At most {max} terms, {termMaxLength} characters each. Matching is case-insensitive.
      </span>
    </label>
  );
}

/**
 * The resolved values, computed here for the summary line.
 *
 * The authoritative resolution is the server's, in the table above; this is the same rule applied to
 * the controls' current state so the editor can show what saving would produce, before it is saved.
 */
function effectiveValues(
  preset: VoiceCatalogue['presets'][number] | undefined,
  overrides: VoiceOverrides,
) {
  const base = preset?.values;

  return {
    emoji: overrides.emoji ?? base?.emoji ?? false,
    tone: overrides.tone ?? base?.tone ?? ('restrained' as VoiceTone),
    titleMaxChars: overrides.titleMaxChars ?? base?.titleMaxChars ?? 60,
  };
}

/**
 * Per-field resolutions for the table, using the same per-field precedence the server applies.
 *
 * `overridden` says whether the owner set the field, not whether the two rendered strings differ: an
 * override that happens to resolve to the preset's own value is still the owner's choice, and the
 * table saying "Preset" for it would misreport where the value came from.
 */
function resolutions(
  preset: VoiceCatalogue['presets'][number] | undefined,
  overrides: VoiceOverrides,
): Resolution[] {
  const base = preset?.values;
  const terms = (list: readonly string[] | undefined) => (list ?? []).join(' | ');

  return [
    resolution('language', base?.language ?? '', overrides.language, String),
    resolution('tone', base?.tone ?? '', overrides.tone, String),
    resolution('emoji', String(base?.emoji ?? false), overrides.emoji, String),
    resolution(
      'titleMaxChars',
      base?.titleMaxChars === undefined ? '' : String(base.titleMaxChars),
      overrides.titleMaxChars,
      String,
    ),
    resolution('technicalDetail', base?.technicalDetail ?? '', overrides.technicalDetail, String),
    resolution('marketing', base?.marketing ?? '', overrides.marketing, String),
    resolution('firstPerson', String(base?.firstPerson ?? false), overrides.firstPerson, String),
    resolution('forbiddenTerms', terms(base?.forbiddenTerms), overrides.forbiddenTerms, terms),
  ];
}

interface Resolution {
  readonly field: string;
  readonly presetValue: string;
  readonly effectiveValue: string;
  readonly overridden: boolean;
}

function resolution<T>(
  field: string,
  presetValue: string | undefined,
  override: T | null,
  show: (value: T) => string,
): Resolution {
  return {
    field,
    presetValue: presetValue ?? '',
    effectiveValue: override === null ? (presetValue ?? '') : show(override),
    overridden: override !== null,
  };
}
