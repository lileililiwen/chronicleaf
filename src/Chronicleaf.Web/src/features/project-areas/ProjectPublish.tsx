import { JourneyPanel } from '../journey-publish/JourneyPanel';
import { PublicationPanel } from '../publications/PublicationPanel';

/**
 * The Publish area (PROJECT-AREA-IA-001): journey projection and
 * publications.
 *
 * Both panels are project-level decisions with no milestone rows:
 * publishing sends an approved asset through a named target, and the
 * journey depicts approved entries other areas already produced.
 * Unpublished material never renders on the public page.
 */
export function ProjectPublish({ projectId }: { readonly projectId: string }) {
  return (
    <>
      {/*
        FEATURE-TRIGGER-PANELS-005. Publication status, reconcile and the publish entry from
        an asset. It sits beside the content panels because a publish sends an approved
        asset through a named target: the preview authorizes nothing, and the publish carries
        a separate explicit confirmation. Nothing is published by rendering.
      */}
      <PublicationPanel projectId={projectId} />

      {/*
        FEATURE-TRIGGER-PANELS-006. The explicit public projection of this project: preview
        what a selection would publish, publish or revoke it, and follow the link to the
        anonymous page. It sits last because a journey depicts approved entries other panels
        already produced. Unpublished material never renders on the public page.
      */}
      <JourneyPanel projectId={projectId} />
    </>
  );
}
