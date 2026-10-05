/**
 * The wire shapes of the local Git evidence routes, as the API publishes them.
 *
 * Everything here comes from a response body. There is no sample repository, no invented branch, no
 * directory, and no field this UI would show that the server did not send - which is what lets a project
 * say it has no local repository registered, and lets a Hosted install say it never reads one.
 */

/**
 * What a project may be told about the local repository it collects from.
 *
 * Note what is absent: there is no path, because the API does not publish one and never holds it. The
 * registered directory lives in the operator's configuration, and a response that could carry a directory
 * would be a response that describes the machine the server runs on. `rootAlias` and `relativePath` are
 * both things the owner chose and are therefore already theirs.
 */
export interface LocalRepository {
  readonly id: string;
  readonly projectId: string;
  readonly rootAlias: string;
  readonly relativePath: string;
  readonly defaultBranch: string | null;
  readonly headSha: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/**
 * The roots this installation offers, and whether it reads local directories at all.
 *
 * `rootAliases` is the whole of what a picker can be built from, and it is the whole of what it needs: a
 * choice between the operator's own names. `available` is false in a deployment that refuses local access,
 * and in that case the list is empty rather than populated - a tenant that could read the names would learn
 * the shape of a machine it does not share.
 */
export interface LocalRepositoryCatalog {
  readonly deploymentMode: string;
  readonly available: boolean;
  readonly rootAliases: readonly string[];
}

/** The 202 a collection request answers with, and where its progress is read. */
export interface LocalGitSyncAccepted {
  readonly operationId: string;
  readonly operationUrl: string;
  readonly status: string;
  readonly created: boolean;
}

/** What the owner asks a milestone to collect. The revision is the evidence one they were looking at. */
export interface LocalGitSyncDraft {
  readonly headRef: string;
  readonly baseRef: string;
  readonly expectedBundleRevision: number;
  readonly idempotencyKey: string;
}

/**
 * A short sentence for a refusal, in the words a person would use.
 *
 * Each code gets its own sentence rather than a generic failure message, because the codes here have
 * genuinely different owners and different repairs: an alias nobody registered is the operator's
 * configuration, a path that left the root is the owner's, and a missing Git is the machine's. A panel
 * that renders all three as "something went wrong" is hiding the only information the owner has.
 */
export function localGitFailureLabel(code: string): string {
  switch (code) {
    case 'local_git_root_unknown':
      return 'This installation collects from roots its operator configured, and that is not one of them.';
    case 'local_git_outside_root':
      return 'That path is not inside the registered root. Chronicleaf reads only a repository inside a root its operator registered.';
    case 'local_git_not_a_repository':
      return 'That path is not a Git repository Chronicleaf can read.';
    case 'local_git_repository_missing':
      return 'This project has no local repository registered yet.';
    case 'local_git_unavailable':
      return 'Git is not available to this server, so nothing was collected. Nothing about the repository was read.';
    case 'local_git_timed_out':
      return 'Reading that repository took longer than Chronicleaf allows, so the read was stopped and nothing was collected.';
    case 'local_git_not_available_in_hosted':
      return 'Reading a local repository is not available in this deployment, so no directory was searched.';
    case 'local_git_job_superseded':
      return 'The repository head moved while this was being collected, so nothing was appended.';
    case 'local_git_job_bundle_superseded':
      return 'The evidence changed while this was being collected, so nothing was appended.';
    case 'local_git_job_ref_not_found':
      return 'That ref does not resolve to a commit in this repository, so nothing was collected.';
    case 'local_git_job_root_unknown':
    case 'local_git_job_outside_root':
      return 'The registered root or the path inside it is no longer usable, so nothing was collected.';
    case 'local_git_job_not_a_repository':
      return 'That path is no longer a Git repository, so nothing was collected.';
    case 'local_git_job_git_unavailable':
    case 'local_git_job_timed_out':
      return 'Reading the repository did not finish. Nothing was appended, and the same request is worth trying again.';
    case 'validation_failed':
      return 'Some of those values are not ones a request may carry.';
    case 'not_found':
      return 'There is no such project or milestone.';
    default:
      return 'The request could not be completed.';
  }
}
