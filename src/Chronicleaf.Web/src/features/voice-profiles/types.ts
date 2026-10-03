/**
 * The wire shapes of the voice routes, as the API publishes them.
 *
 * Two things about this file are load-bearing rather than decorative.
 *
 * First, `VoiceOverrides` is eight three-state fields: a value, or `null` for "the owner said
 * nothing here". `emoji: false` and an absent `emoji` are different inputs and resolve differently -
 * the first turns emoji off over a preset that had it on, the second leaves the preset alone. A type
 * that collapsed them to `boolean` would make the package's central requirement unrepresentable on
 * this side of the wire.
 *
 * Second, a `VoiceFinding` is a span, never a boolean and never a replacement. It names the rule, the
 * field, the offset and length inside the owner's own text, and the term that matched. There is no
 * field on it that could carry corrected text, because this surface reports a rule violation and
 * leaves the wording to the owner.
 */

/** The languages a profile may declare. */
export const VOICE_LANGUAGES = ['zh', 'en'] as const;

/** The tones a profile may declare. */
export const VOICE_TONES = ['restrained', 'neutral', 'casual'] as const;

/** The verbosity of technical content a profile may declare. */
export const VOICE_TECHNICAL_DETAILS = ['low', 'medium', 'high'] as const;

/** How far a profile may lean on product language. */
export const VOICE_MARKETING_LEVELS = ['low', 'medium', 'high'] as const;

export type VoiceLanguage = (typeof VOICE_LANGUAGES)[number];
export type VoiceTone = (typeof VOICE_TONES)[number];
export type VoiceTechnicalDetail = (typeof VOICE_TECHNICAL_DETAILS)[number];
export type VoiceMarketingLevel = (typeof VOICE_MARKETING_LEVELS)[number];

/** The eight resolved values, each one already decided by the server. */
export interface VoiceValues {
  readonly language: VoiceLanguage;
  readonly tone: VoiceTone;
  readonly emoji: boolean;
  readonly titleMaxChars: number;
  readonly technicalDetail: VoiceTechnicalDetail;
  readonly marketing: VoiceMarketingLevel;
  readonly firstPerson: boolean;
  readonly forbiddenTerms: readonly string[];
}

/**
 * One field's resolution, as the server reports it: what the preset said, what won, and which one it
 * was. The surface shows all three, so an owner who turned emoji off can see that it was the
 * override that did it rather than having to trust it.
 */
export interface VoiceField {
  readonly field: string;
  readonly presetValue: string;
  readonly effectiveValue: string;
  readonly overridden: boolean;
}

/** The owner's explicit deviations from the preset. `null` means "not an override". */
export interface VoiceOverrides {
  readonly language: VoiceLanguage | null;
  readonly tone: VoiceTone | null;
  readonly emoji: boolean | null;
  readonly titleMaxChars: number | null;
  readonly technicalDetail: VoiceTechnicalDetail | null;
  readonly marketing: VoiceMarketingLevel | null;
  readonly firstPerson: boolean | null;
  readonly forbiddenTerms: readonly string[] | null;
}

/** A stored profile, with the revision the next write presents. */
export interface VoiceProfile {
  readonly id: string;
  readonly projectId: string;
  readonly presetId: string;
  readonly stored: boolean;
  readonly revision: number;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly overrides: VoiceOverrides;
  readonly presetValues: VoiceValues;
  readonly effectiveValues: VoiceValues;
  readonly fields: readonly VoiceField[];
  readonly overriddenFields: readonly string[];
}

/** One preset in the catalogue. */
export interface VoicePreset {
  readonly id: string;
  readonly label: string;
  readonly description: string;
  readonly values: VoiceValues;
}

/** The bounds a settings surface validates against before it posts. */
export interface VoiceLimits {
  readonly minTitleMaxChars: number;
  readonly maxTitleMaxChars: number;
  readonly maxForbiddenTerms: number;
  readonly forbiddenTermMaxLength: number;
  readonly maxTitleTextLength: number;
  readonly maxBodyTextLength: number;
  readonly defaultPageSize: number;
  readonly maxPageSize: number;
}

/** The whole catalogue, with the vocabularies and limits needed to render an editor. */
export interface VoiceCatalogue {
  readonly presets: readonly VoicePreset[];
  readonly defaults: VoiceValues;
  readonly languages: readonly string[];
  readonly tones: readonly string[];
  readonly technicalDetails: readonly string[];
  readonly marketingLevels: readonly string[];
  readonly rules: readonly string[];
  readonly limits: VoiceLimits;
}

/** One rule violation, with the span it points at inside the owner's own text. */
export interface VoiceFinding {
  readonly rule: string;
  readonly field: string;
  readonly start: number;
  readonly length: number;
  readonly excerpt: string;
  readonly term: string;
  readonly normalizedTerm: string;
  readonly message: string;
}

/** The voice a piece of text was judged under, and what that text violated. */
export interface VoiceCheck {
  readonly snapshot: VoiceSnapshot;
  readonly clean: boolean;
  readonly findings: readonly VoiceFinding[];
}

/** The copied voice, named so a finding is never shown without the voice it was measured against. */
export interface VoiceSnapshot {
  readonly presetId: string;
  readonly profileRevision: number;
  readonly capturedAt: string;
  readonly fingerprint: string;
  readonly values: VoiceValues;
}

/** An owner-written draft, with the voice it was created under. */
export interface VoiceDraft {
  readonly id: string;
  readonly projectId: string;
  readonly title: string;
  readonly body: string;
  readonly status: string;
  readonly revision: number;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly snapshot: VoiceSnapshot;
  readonly clean: boolean;
  readonly findings: readonly VoiceFinding[];
}

/** One draft in a list, with how many rules its text currently breaks. */
export interface VoiceDraftSummary {
  readonly id: string;
  readonly projectId: string;
  readonly status: string;
  readonly revision: number;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly snapshotFingerprint: string;
  readonly findingCount: number;
}

/** One bounded page of drafts, with the cursor that continues it. */
export interface VoiceDraftPage {
  readonly items: readonly VoiceDraftSummary[];
  readonly nextCursor: string | null;
  readonly pageSize: number;
}

/** The delivered error envelope, as far as this client needs it. */
export interface ApiFailure {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}

/** An override record with nothing set, which is what an untouched editor holds. */
export const NO_OVERRIDES: VoiceOverrides = {
  language: null,
  tone: null,
  emoji: null,
  titleMaxChars: null,
  technicalDetail: null,
  marketing: null,
  firstPerson: null,
  forbiddenTerms: null,
};

/** A human label for a field name, so the surface says words rather than wire keys. */
export function fieldLabel(field: string): string {
  switch (field) {
    case 'language':
      return 'Language';
    case 'tone':
      return 'Tone';
    case 'emoji':
      return 'Emoji';
    case 'titleMaxChars':
      return 'Title budget';
    case 'technicalDetail':
      return 'Technical detail';
    case 'marketing':
      return 'Marketing';
    case 'firstPerson':
      return 'First person';
    case 'forbiddenTerms':
      return 'Forbidden terms';
    default:
      return field;
  }
}

/** A human label for a rule name, so a finding says what it checked. */
export function ruleLabel(rule: string): string {
  switch (rule) {
    case 'forbidden_term':
      return 'Forbidden term';
    case 'emoji_not_allowed':
      return 'Emoji not allowed';
    case 'title_too_long':
      return 'Title too long';
    case 'language_mismatch':
      return 'Language mismatch';
    default:
      return rule;
  }
}
