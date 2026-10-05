/**
 * The wire shapes of the GitHub candidate routes, as the API publishes them.
 *
 * <b>What is deliberately absent is the point of this file.</b> A candidate has no milestone id, no
 * bundle revision, no approval status and no confirm verb anywhere in this feature's types - because the
 * server publishes none and this product owns none. A panel built from these types cannot render a
 * candidate as though it were a milestone, and a type that grew a `milestoneId` would be the first sign
 * that the boundary had been crossed.
 */

/** The closed set of kinds a candidate can be, as the server fixes it. */
export const CANDIDATE_KINDS = [
  'release',
  'tag',
  'pull-request',
  'issue',
  'star',
] as const;

export type CandidateKind = (typeof CANDIDATE_KINDS)[number];

/** Whether a signed delivery or a poll proposed this candidate. */
export type CandidateSource = 'webhook' | 'poll';

/**
 * One tag- or event-derived Milestone candidate, exactly as the server sent it.
 *
 * `sourceKey` is the provider's own identity for the thing proposed and is published rather than kept
 * behind an opaque id, because it is what makes the row deduplicable: the same release delivered twice
 * is one candidate, and an owner reasoning about that needs to be able to see the key.
 */
export interface GitHubCandidate {
  readonly id: string;
  readonly projectId: string;
  readonly sourceKey: string;
  readonly kind: CandidateKind;
  readonly title: string;
  readonly tagName: string | null;
  readonly revision: string | null;
  readonly source: CandidateSource;
  readonly deliveryId: string | null;
  readonly receivedAt: string;
}

/**
 * A page of candidates, and the stored total beside it.
 *
 * `count` is the total and `items` is the bounded page. They are different numbers on purpose: a project
 * with two hundred candidates and a page of twenty-five is not an empty list, and a panel that reported
 * the page as the total would tell its owner they had lost a hundred and seventy-five proposals.
 */
export interface GitHubCandidatePage {
  readonly items: readonly GitHubCandidate[];
  readonly count: number;
}

/**
 * Whether this project's connection accepts a signed delivery, and whether a secret is stored for it.
 *
 * `hasSecret` is a boolean and never the vault's identifier: an owner needs to know that a secret is
 * configured, and a stored id in a browser's memory is one more copy of a thing that identifies a
 * secret. There is no field here that could carry the secret itself.
 */
export interface GitHubWebhookSubscription {
  readonly connectionId: string;
  readonly projectId: string;
  readonly enabled: boolean;
  readonly hasSecret: boolean;
  readonly updatedAt: string;
}

/** The 202 a poll request answers with, and where its progress is read. */
export interface GitHubCandidateSyncAccepted {
  readonly operationId: string;
  readonly operationUrl: string;
  readonly status: string;
  readonly created: boolean;
}

/**
 * A short sentence for a refusal, in the words a person would use.
 *
 * Each code gets its own sentence rather than a generic failure message, because these codes have
 * genuinely different owners and different repairs. A panel that renders all of them as "something went
 * wrong" is hiding the only information the owner has - and one of them in particular, the 401, is the
 * server refusing to say anything at all about a connection id, which is a decision this UI must not
 * narrate as "your connection does not exist".
 */
export function candidateFailureLabel(code: string): string {
  switch (code) {
    case 'github_candidate_signature_invalid':
      return 'That delivery did not authenticate. Chronicleaf refused it without saying anything else about the connection.';
    case 'github_candidate_delivery_malformed':
      return 'That delivery was signed correctly but its body is not something this server can read.';
    case 'github_candidate_connection_missing':
      return 'This project has no repository connected, so there is nothing to propose anything about.';
    case 'github_candidate_unreachable':
      return 'GitHub could not be read just now, so nothing was proposed.';
    case 'github_candidate_delivery_too_large':
      return 'That delivery was over the one mebibyte this route accepts, so none of it was read.';
    case 'validation_failed':
      return 'Some of those values are not ones a request may carry.';
    case 'not_found':
      return 'There is no such project here.';
    default:
      return 'The request could not be completed.';
  }
}

/**
 * A human label for one kind of candidate.
 *
 * The kind is a closed set, so the label is a switch rather than a prettified slug: an owner should never
 * be shown a raw wire value as though it were a heading.
 */
export function candidateKindLabel(kind: string): string {
  switch (kind) {
    case 'release':
      return 'Release';
    case 'tag':
      return 'Tag';
    case 'pull-request':
      return 'Merged pull request';
    case 'issue':
      return 'Closed issue';
    case 'star':
      return 'Stars';
    default:
      return kind;
  }
}

/**
 * The short sentence a kind contributes to a row.
 *
 * <b>It always says "proposed", and it never says "added".</b> These are things somebody else published
 * that Chronicleaf is suggesting the owner might record as a milestone. A label that read "Added" or
 * "Recorded" would be claiming a decision this product has not made and cannot make, and the sentence is
 * where an owner's eye goes first.
 */
export function candidateKindSentence(kind: string): string {
  switch (kind) {
    case 'release':
      return 'A published release was proposed as a Milestone.';
    case 'tag':
      return 'A created tag was proposed as a Milestone.';
    case 'pull-request':
      return 'A merged pull request was proposed as a Milestone.';
    case 'issue':
      return 'A closed issue was proposed as a Milestone.';
    case 'star':
      return 'A star threshold was crossed, and that was proposed as a Milestone.';
    default:
      return 'Something on GitHub was proposed as a Milestone.';
  }
}
