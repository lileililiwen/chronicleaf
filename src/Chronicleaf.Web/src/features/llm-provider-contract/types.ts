/**
 * The wire shapes of the generation routes, as the API publishes them.
 *
 * These are declarations only. Nothing in this file fetches, holds a credential, or keeps a
 * generation result: a provider response belongs to the server, and a browser that cached one would
 * be a browser holding a draft nobody chose to save.
 */

/** One provider slot as the catalogue publishes it. */
export interface AiProviderView {
  readonly id: string;
  /**
   * Whether an adapter implementation is present in this deployment.
   *
   * A `false` here is a fact about the build. A `true` with `capability.nativeJsonSchema === false`
   * is a fact about the provider, and the two lead to different things, so the surface shows them
   * differently rather than collapsing them into one "unavailable" badge.
   */
  readonly adapterInstalled: boolean;
  /** The adapter's own descriptor, or null when no adapter is installed. */
  readonly capability: ProviderCapability | null;
  readonly configured: boolean;
  readonly credentialRevision: number | null;
}

/**
 * What an adapter says its provider can do.
 *
 * Never promoted by the client. A `false` nativeJsonSchema is what makes the surface show that a
 * draft will be checked locally after it arrives, which is the one thing a reader needs to know
 * before trusting a result it did not check itself.
 */
export interface ProviderCapability {
  readonly id: string;
  readonly structuredOutputSupport: boolean;
  readonly nativeJsonSchema: boolean;
  readonly localEndpoint: boolean;
  readonly requiresCredential: boolean;
}

/** The bounds every call runs inside, published so the surface shows them rather than guesses. */
export interface GenerationLimits {
  readonly maxOutputTokens: number;
  readonly timeoutSeconds: number;
  readonly maxInputBytes: number;
  readonly consentTtlSeconds: number;
  readonly maxTransientAttempts: number;
  readonly enabledPurposes: readonly string[];
}

/** The catalogue, with configured and supported kept apart. */
export interface AiProviderCatalogue {
  readonly supported: readonly AiProviderView[];
  readonly configured: readonly AiProviderView[];
  readonly usable: readonly AiProviderView[];
  readonly limits: GenerationLimits;
}

/** An owner's resolved provider selection. */
export interface AiSettingsResolution {
  readonly providerId: string;
  readonly model: string;
  readonly adapterInstalled: boolean;
  readonly capability: ProviderCapability | null;
  readonly nativeJsonSchema: boolean | null;
  readonly credentialId: string | null;
  readonly credentialRevision: number | null;
  readonly endpointRef: string | null;
  readonly limits: GenerationLimits;
}

/**
 * The owner-authorized preview: the input side of the handshake.
 *
 * There is no draft text here and no provider response, by design. The owner reads the evidence ids
 * and the redaction labels against the app's own redacted view and decides whether to authorize it;
 * the consent hash that comes back is a binding over that decision and is not a credential, so it
 * is treated as one piece of request state and never as a token worth keeping.
 */
export interface GenerationPreview {
  readonly previewId: string;
  readonly consentHash: string;
  readonly inputEvidenceIds: readonly string[];
  readonly redactions: readonly string[];
  readonly providerId: string;
  readonly model: string;
  readonly credentialRevision: number | null;
  readonly evidenceRevision: number;
  readonly voiceRevision: number;
  readonly expiresAt: string;
}

/** The result of one bounded call. A draft, and only a draft. */
export interface GenerationRun {
  readonly status: 'completed' | 'refused' | 'invalid' | 'failed';
  readonly text: string | null;
  readonly providerRequestId: string | null;
  readonly inputTokens: number | null;
  readonly outputTokens: number | null;
  readonly finishReason: string;
  readonly errorCode: string | null;
  /**
   * Whether the provider enforced the schema itself. False means this system's local validator
   * checked the text, which is a materially weaker guarantee and is labelled as such.
   */
  readonly nativeJsonSchema: boolean;
  readonly localValidationApplied: boolean;
  /** How many times the transport was invoked, so a bounded retry is visible rather than billed. */
  readonly attempts: number;
}

/** One evidence item, as far as the preview needs to know about it. */
export interface EvidenceItemSummary {
  readonly id: string;
  readonly kind: string;
  readonly sourceRef: string;
  readonly text: string;
  readonly redactions: readonly string[];
  readonly availability: string;
}

/** The milestone's current evidence head. */
export interface EvidenceHead {
  readonly revision: number;
  readonly items: readonly EvidenceItemSummary[];
}

/** The delivered error envelope. */
export interface ApiFailure {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}
