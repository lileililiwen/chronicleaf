/**
 * The wire shapes of the GitHub evidence routes, as the API publishes them.
 *
 * Everything here comes from a response body. There is no sample repository, no invented branch and no
 * field this UI would show that the server did not send - which is what lets a project say it has no
 * repository connected, and lets a refused repository say the same thing GitHub says about it.
 */

/**
 * What a project may be told about the repository it collects from.
 *
 * Note what is absent: there is no URL, because the API does not publish one and never holds one. The
 * host is a constant of the server, and a response that could carry a host would be a response that
 * could be pointed somewhere else. `hasCredential` is a boolean because an owner needs to know whether a
 * token is configured and has no use for the vault's identifier.
 */
export interface GitHubConnection {
  readonly id: string;
  readonly projectId: string;
  readonly owner: string;
  readonly repository: string;
  readonly defaultBranch: string | null;
  readonly repositoryId: number | null;
  readonly hasCredential: boolean;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** The 202 a collection request answers with, and where its progress is read. */
export interface GitHubSyncAccepted {
  readonly operationId: string;
  readonly operationUrl: string;
  readonly status: string;
  readonly created: boolean;
}

/** What the owner asks a milestone to collect. The revision is the evidence one they were looking at. */
export interface GitHubSyncDraft {
  readonly headRef: string;
  readonly baseRef: string;
  readonly releaseTag: string;
  readonly issueNumber: string;
  readonly pullRequestNumber: string;
  readonly expectedBundleRevision: number;
  readonly idempotencyKey: string;
}

/**
 * A short sentence for a refusal, in the words a person would use.
 *
 * The codes are this package's own and the delivered ones, and each one gets its own sentence rather
 * than a generic failure message: "not public or not there" and "GitHub is not answering" are different
 * problems with different next steps, and a panel that renders both as "something went wrong" is
 * hiding the only information the owner has.
 */
export function gitHubFailureLabel(code: string): string {
  switch (code) {
    case 'github_repository_unavailable':
      return 'That repository could not be read. A repository that is not public and a repository that does not exist are the same answer.';
    case 'github_credential_rejected':
      return 'GitHub refused the stored token. Change it, or connect a repository that is public.';
    case 'github_connection_missing':
      return 'This project has no repository connected yet.';
    case 'github_unreachable':
      return 'GitHub could not be read. Nothing was collected.';
    case 'github_job_superseded':
      return 'The evidence changed while this was being collected, so nothing was appended.';
    case 'validation_failed':
      return 'Some of those values are not ones a request may carry.';
    case 'not_found':
      return 'There is no such project or milestone.';
    default:
      return 'The request could not be completed.';
  }
}
