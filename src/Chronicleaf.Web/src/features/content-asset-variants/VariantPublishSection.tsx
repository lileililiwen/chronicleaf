import { PublishForm } from '../publications/PublishForm';

/**
 * The publish section of one approved content variant
 * (PUBLISH-FROM-VARIANT-002).
 *
 * It hosts the one shared publish form with the variant's own asset id
 * and the live approval the review read named. The discipline is the
 * delivered panel's, moved not weakened: the preview authorizes nothing,
 * the publish carries a separate explicit confirmation, and refusals
 * show the server's own code and message verbatim. Nothing here fires
 * on render except the target list read.
 */
export function VariantPublishSection({
  assetId,
  approvalId,
}: {
  /** The approved variant asset to send. Fixed by the row that opened this section. */
  readonly assetId: string;
  /** The live approval the publish is read against. Prefilled, still shown. */
  readonly approvalId: string;
}) {
  return (
    <div data-testid="variant-publish-section">
      <h4 className="view__subsection" data-testid="variant-publish-heading">
        Publish this format
      </h4>
      <p data-testid="variant-publish-hint">
        A preview answers what a send would do without sending anything. The publish
        sends only what the stored rows still describe, behind a separate confirmation.
      </p>
      <PublishForm assetId={assetId} approvalId={approvalId} namespace="variant-publish" />
    </div>
  );
}
