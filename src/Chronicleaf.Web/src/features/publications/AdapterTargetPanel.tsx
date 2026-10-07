import type { PublishPreview, PublisherTarget } from './types';

/**
 * The per-adapter target surface (PUBLISHER-ADAPTER-PANELS-002/003).
 *
 * It renders under the target picker inside the one shared publish form, so
 * both publish hosts inherit it with the delivered consent flow untouched.
 * Everything it shows comes from delivered routes: the classification is the
 * picked descriptor served field-for-field by `GET /api/publisher-targets`,
 * and the bound configuration is the preview's own first warning — every
 * shipped adapter binds its target line there, and the contract hashes the
 * warnings, so a reconfiguration after the preview stales the confirm.
 *
 * It sends nothing and it refuses nothing: an export-only target names the
 * hand-carry flow, and the refusal itself is the server's answer to an
 * actual preview, shown verbatim by the form's error slot.
 */
export function AdapterTargetPanel({
  target,
  preview,
  namespace,
}: {
  /** The picked target descriptor, as the target list served it. */
  readonly target: PublisherTarget;
  /** The latest preview, or null before the owner previews. */
  readonly preview: PublishPreview | null;
  /** Testid scope, inherited from the hosting form. */
  readonly namespace: string;
}) {
  const copy = adapterCopy(target.targetId);

  return (
    <section aria-label="Adapter target" data-testid={`${namespace}-adapter-panel`}>
      <h4 className="view__subsection" data-testid={`${namespace}-adapter-name`}>
        {copy.name}
      </h4>
      <p data-testid={`${namespace}-adapter-about`}>{copy.about}</p>
      <p data-testid={`${namespace}-adapter-modes`}>{modeLine(target)}</p>
      {target.supportsReconcile ? (
        <p data-testid={`${namespace}-adapter-reconcile`}>
          Ambiguous receipts can be reconciled against what this target reports.
        </p>
      ) : null}
      {preview !== null && preview.targetId === target.targetId ? (
        <p data-testid={`${namespace}-adapter-config`}>
          The server describes this send as: {preview.warnings[0] ?? 'no configuration reported.'}
        </p>
      ) : (
        <p data-testid={`${namespace}-adapter-unpreviewed`}>
          Preview to read the exact configuration this send would use.
        </p>
      )}
    </section>
  );
}

function modeLine(target: PublisherTarget): string {
  if (target.exportOnly) {
    return 'This target is export-only: material leaves through its export flow, carried by hand — nothing is sent from here.';
  }

  const modes = [
    target.supportsDraft ? 'draft' : null,
    target.supportsPublish ? 'publish' : null,
  ].filter((mode): mode is string => mode !== null);

  return `This target serves: ${modes.join(' + ')}.`;
}

function adapterCopy(targetId: string): { name: string; about: string } {
  switch (targetId) {
    case 'github-pages':
      return {
        name: 'GitHub Pages',
        about:
          'Renders the site files locally against the bound repository, branch and subdirectory and moves the Pages branch. Publish only; the preview reaches no remote side.',
      };
    case 'devto':
      return {
        name: 'Dev.to',
        about:
          'Sends the approved text as a Forem article under the configured tags, as a draft or a published article. Approval-covered pictures are named in the preview, not carried.',
      };
    case 'hashnode':
      return {
        name: 'Hashnode',
        about:
          'Sends the approved text as a post under the configured publication, as a draft or a published post. Approval-covered pictures are named in the preview, not carried.',
      };
    case 'ghost':
      return {
        name: 'Ghost',
        about:
          'Sends the sanitized HTML article to the configured origin under its Admin API version and default tags, as a draft or a published post. Approval-covered pictures are named in the preview, not carried.',
      };
    case 'wordpress':
      return {
        name: 'WordPress',
        about:
          'Sends the sanitized HTML article to the configured site under its application-password user, as a draft or a published post. Approval-covered pictures are named in the preview, not carried.',
      };
    case 'custom-blog':
      return {
        name: 'Custom blog',
        about:
          'Sends the approved Markdown to the fixed configured origin speaking the Chronicleaf Publisher HTTP contract, as a draft or a published post. Approval-covered pictures are named in the preview, not carried.',
      };
    case 'medium':
      return {
        name: 'Medium (export-only)',
        about:
          'Medium has no native API: material leaves through the Medium import flow, carried by hand. Previewing here asks the server, which refuses — that refusal is the answer, not a send.',
      };
    case 'linkedin':
      return {
        name: 'LinkedIn (export-only)',
        about:
          'LinkedIn has no native send in contract: the output is copy-ready text carried by hand. Previewing here asks the server, which refuses — that refusal is the answer, not a send.',
      };
    default:
      return {
        name: targetId,
        about:
          'The server classifies this target; the preview answers what a send would do.',
      };
  }
}
