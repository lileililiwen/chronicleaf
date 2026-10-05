/**
 * The wire shapes of the credential settings routes, as the API publishes them.
 *
 * There is no field here that can carry a secret, and there is deliberately no field derived from
 * one either: no masked value, no length, no fingerprint, no last four. The client is told whether a
 * provider is configured and when it last changed, which is the whole of what it may show, so there is
 * nothing for it to display by accident.
 */

/**
 * The closed vocabulary of providers a credential may be stored for, as the server fixed it.
 *
 * <b>`github-webhook` is a second GitHub slot and it has to be.</b> The server holds one credential per
 * provider for a scope, and the two GitHub secrets are not interchangeable: the `github` token
 * authenticates Chronicleaf to GitHub when it reads a repository, and the webhook secret authenticates
 * GitHub to Chronicleaf when it delivers a signed event. Reusing one slot would mean saving a webhook
 * secret overwrote the read token, so they are two providers here as they are on the server - and this
 * list is the client's copy of the server's, so the two cannot drift without this comment being a lie.
 */
export const CREDENTIAL_PROVIDERS = [
  'github',
  'github-webhook',
  'openai',
  'gemini',
  'anthropic',
  'openrouter',
  'ollama',
  'publisher',
] as const;

export type CredentialProvider = (typeof CREDENTIAL_PROVIDERS)[number];

/** What the server says about one provider. Never a value. */
export interface CredentialMetadata {
  readonly id: string | null;
  readonly provider: string;
  readonly configured: boolean;
  readonly updatedAt: string | null;
}

/** One item of the settings list: the same four fields. */
export interface CredentialListItem {
  readonly id: string;
  readonly provider: string;
  readonly configured: boolean;
  readonly updatedAt: string;
}

/** One bounded page of configured providers, with the cursor that continues it. */
export interface CredentialPage {
  readonly items: readonly CredentialListItem[];
  readonly nextCursor: string | null;
}

/**
 * What one provider's state looks like in the panel, including the revision the next write has to
 * present.
 *
 * The revision comes from the `ETag` of the provider's own read, not from the list: the list is a
 * summary and this is the token a mutation is conditional on. `revision` is 0 for a provider that has
 * nothing stored, which is the same empty-state token the API publishes.
 */
export interface ProviderState {
  readonly provider: CredentialProvider;
  /** The credential's id, which a remove presents, or null when nothing is stored. */
  readonly id: string | null;
  readonly configured: boolean;
  readonly updatedAt: string | null;
  readonly revision: number;
}

/** The delivered error envelope, as far as this client needs it. */
export interface ApiFailure {
  readonly code: string;
  readonly message: string;
  readonly traceId: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}

/** A human label for a provider, so the panel says words rather than slugs. */
export function providerLabel(provider: string): string {
  switch (provider) {
    case 'github':
      return 'GitHub';
    case 'github-webhook':
      return 'GitHub webhook secret';
    case 'openai':
      return 'OpenAI';
    case 'gemini':
      return 'Google Gemini';
    case 'anthropic':
      return 'Anthropic';
    case 'openrouter':
      return 'OpenRouter';
    case 'ollama':
      return 'Ollama';
    case 'publisher':
      return 'Publisher';
    default:
      return provider;
  }
}

/** What a provider's field placeholder says, and never what it holds. */
export function providerHint(provider: CredentialProvider): string {
  if (provider === 'ollama') {
    return 'Usually empty: a local model needs no key.';
  }

  if (provider === 'github-webhook') {
    return 'The secret from the repository webhook, not a personal access token. It authenticates GitHub to Chronicleaf, and it is the other half of the github token above.';
  }

  return 'Paste a new token to replace whatever is stored.';
}
