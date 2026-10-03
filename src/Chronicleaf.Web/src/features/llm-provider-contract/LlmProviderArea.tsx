import { GenerationPreviewPanel } from './GenerationPreviewPanel';

/**
 * The generation area, rendered under a milestone inside the project detail.
 *
 * There is no separate route and no settings page here. A generation is always about one milestone,
 * and the provider catalogue it needs is the same one the credential settings surface is about, so
 * putting this behind a path would have implied a preview could name something other than what it is
 * about. The panel appears where the evidence panel already appears, for the same reason: what is
 * drafted and what it is drafted from belong together on the page.
 *
 * What is deliberately absent is a way to see the redacted snapshot as raw text. The owner sees the
 * evidence items, the redaction labels and the provider and model that will be used - which is what
 * the authorization is a decision about - and the bytes themselves stay on the server, where the
 * credential reference and the stored snapshot live. A page that rendered the snapshot would be one
 * more place a screenshot could capture a payload nobody chose to publish.
 */
export function LlmProviderArea({
  milestoneId,
  milestoneTitle,
  archived,
}: {
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  readonly archived: boolean;
}) {
  return (
    <GenerationPreviewPanel
      milestoneId={milestoneId}
      milestoneTitle={milestoneTitle}
      archived={archived}
    />
  );
}
