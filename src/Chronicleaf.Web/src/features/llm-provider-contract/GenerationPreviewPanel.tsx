import { useEffect, useState } from 'react';
import { useGeneration } from './useGeneration';
import type { AiProviderCatalogue, AiProviderView } from './types';

/**
 * The generation panel, rendered under a milestone.
 *
 * It is a panel rather than a page because a generation is always about one milestone: the owner
 * reads the redacted input for that milestone, authorizes that, and reads the draft. A separate route
 * would have implied a preview could be about something other than what it names.
 *
 * Three things it deliberately does not do, each because the requirement says so:
 *
 * - It does not offer a "generate" button until a redacted snapshot exists and the owner has read
 *   it. A button that appeared earlier would be a button that spends money before consent.
 * - It does not present a result as approved, and it labels which of the two schema paths checked
 *   it. A local validation is a weaker guarantee than a provider's own, and a reader deserves to know
 *   which one they are looking at.
 * - It does not show provider prose, a model the system did not accept, or an endpoint it was not
 *   given. Those are the server's business, and a page is the wrong place to re-state them. The one
 *   endpoint it does show is the one the owner must supply for a local-endpoint provider: without it
 *   no call can be addressed, so the field is part of the selection, not of the result.
 */
export function GenerationPreviewPanel({
  milestoneId,
  milestoneTitle,
  archived,
}: {
  readonly milestoneId: string;
  readonly milestoneTitle: string;
  readonly archived: boolean;
}) {
  const flow = useGeneration(milestoneId);
  const [providerId, setProviderId] = useState('');
  const [model, setModel] = useState('');
  const [endpoint, setEndpoint] = useState('');

  const slots = flow.catalogue?.supported ?? [];
  const selected: AiProviderView | null =
    slots.find((slot) => slot.id === providerId) ?? null;

  // Whether the selected provider runs on an endpoint the owner names rather than a published
  // one. Only then is an endpoint field shown: for every other provider the origin is the
  // adapter's own constant and a field would imply a choice the owner does not have.
  const wantsEndpoint = selected?.capability?.localEndpoint === true;

  // The credential for the selected slot, which is what a preview binds to. Never a value: this
  // panel has no field that could hold one, and the settings surface is the only place that does.
  const credentialRef = selected?.configured ? selected.id : null;

  useEffect(() => {
    if (providerId.length === 0 && slots.length > 0) {
      const firstUsable = slots.find((slot) => slot.configured) ?? slots[0];
      setProviderId(firstUsable.id);
    }
  }, [providerId, slots]);

  if (archived) {
    return null;
  }

  return (
    <section className="evidence" data-testid="generation-panel">
      <h4 className="evidence__heading">Draft a story for “{milestoneTitle}” with your own provider</h4>

      {flow.catalogueError !== null ? (
        <p className="notice notice--error" role="alert" data-testid="generation-catalogue-error">
          {flow.catalogueError}
        </p>
      ) : null}

      {flow.catalogue !== null && flow.catalogue.usable.length === 0 ? (
        <p className="notice" data-testid="generation-no-adapter">
          {noUsableProviderSentence(flow.catalogue)}
        </p>
      ) : null}

      <div className="view">
        <label htmlFor={`generation-provider-${milestoneId}`}>Provider</label>
        <select
          id={`generation-provider-${milestoneId}`}
          value={providerId}
          data-testid="generation-provider"
          disabled={flow.catalogueBusy || slots.length === 0}
          onChange={(event) => {
            setProviderId(event.target.value);
            setModel('');
            setEndpoint('');
          }}
        >
          {slots.map((slot) => (
            <option key={slot.id} value={slot.id}>
              {slotLabel(slot)}
            </option>
          ))}
        </select>
      </div>

      {selected !== null ? (
        <p className="evidence__revision" data-testid="generation-capability">
          {capabilitySentence(
            selected,
            (flow.catalogue?.usable ?? []).some((slot) => slot.id === selected.id),
          )}
        </p>
      ) : null}

      <div className="view">
        <label htmlFor={`generation-model-${milestoneId}`}>Model</label>
        <input
          id={`generation-model-${milestoneId}`}
          type="text"
          value={model}
          data-testid="generation-model"
          autoComplete="off"
          onChange={(event) => setModel(event.target.value)}
        />
        <p className="evidence__item-meta">
          Chronicleaf keeps no list of models. The provider you select decides what it accepts, and a
          name it does not accept is refused rather than replaced with a near miss.
        </p>
      </div>

      {wantsEndpoint ? (
        <div className="view">
          <label htmlFor={`generation-endpoint-${milestoneId}`}>Endpoint</label>
          <input
            id={`generation-endpoint-${milestoneId}`}
            type="text"
            value={endpoint}
            data-testid="generation-endpoint"
            autoComplete="off"
            placeholder="http://127.0.0.1:11434"
            onChange={(event) => setEndpoint(event.target.value)}
          />
          <p className="evidence__item-meta">
            The local origin this deployment may address. It must be one the operator allowlisted;
            anything else is refused before a call is made.
          </p>
        </div>
      ) : null}

      {flow.catalogue !== null ? (
        <p className="evidence__item-meta" data-testid="generation-limits">
          {/* One interpolated string rather than several, so no JSX whitespace rule can quietly join a
              number to its unit and turn "256 KiB" into "256KiB" on a page whose whole job is to
              state a bound accurately. */}
          {`At most ${flow.catalogue.limits.maxOutputTokens} output tokens, a ${
            flow.catalogue.limits.timeoutSeconds
          }s budget, ${Math.round(flow.catalogue.limits.maxInputBytes / 1024)} KiB of evidence, and a consent that holds for ${Math.round(
            flow.catalogue.limits.consentTtlSeconds / 60,
          )} minutes. Text only: no tools, no function calls, no browsing.`}
        </p>
      ) : null}

      <h5 className="evidence__kind" id={`generation-evidence-${milestoneId}`}>
        Evidence to include
      </h5>

      {flow.evidenceBusy ? (
        <p className="view__state" data-testid="generation-evidence-loading">
          Reading the evidence for this milestone…
        </p>
      ) : null}

      {flow.evidenceError !== null ? (
        <p className="notice notice--error" role="alert" data-testid="generation-evidence-error">
          {flow.evidenceError}
        </p>
      ) : null}

      {flow.evidenceItems.length === 0 && !flow.evidenceBusy ? (
        <p className="empty" data-testid="generation-evidence-empty">
          This milestone has no evidence yet. Record something first: a generation is a draft from
          what was actually collected, not from what the title suggests.
        </p>
      ) : (
        <ul className="evidence__items" aria-labelledby={`generation-evidence-${milestoneId}`} data-testid="generation-evidence-list">
          {flow.evidenceItems.map((item) => (
            <li key={item.id}>
              <label>
                <input
                  type="checkbox"
                  checked={flow.selectedEvidenceIds.includes(item.id)}
                  data-testid={`generation-evidence-${item.id}`}
                  onChange={() => flow.toggleEvidence(item.id)}
                />
                <span>
                  {item.kind} — {item.sourceRef}
                  {item.availability === 'available' ? '' : ' (not fully available)'}
                </span>
              </label>
            </li>
          ))}
        </ul>
      )}

      <div className="view__actions">
        <button
          type="button"
          className="action"
          data-testid="generation-preview"
          disabled={
            flow.previewBusy ||
            flow.catalogueBusy ||
            model.trim().length === 0 ||
            (wantsEndpoint && endpoint.trim().length === 0) ||
            selected === null ||
            flow.evidenceItems.length === 0
          }
          onClick={() =>
            void flow.createPreview({
              providerId,
              model: model.trim(),
              credentialRef,
              endpointRef: wantsEndpoint ? endpoint.trim() : null,
            })
          }
        >
          {flow.previewBusy ? 'Preparing…' : 'Review what would be sent'}
        </button>
      </div>

      {flow.resolutionError !== null ? (
        <p className="notice notice--error" role="alert" data-testid="generation-settings-error">
          {flow.resolutionError}
        </p>
      ) : null}

      {flow.previewError !== null ? (
        <p className="notice notice--error" role="alert" data-testid="generation-preview-error">
          {flow.previewError}
        </p>
      ) : null}

      {flow.preview !== null ? (
        <div className="evidence__bundle" data-testid="generation-preview-card">
          <h4 className="evidence__kind">What would be sent</h4>

          <p className="evidence__revision" data-testid="generation-preview-summary">
            {flow.preview.providerId} · {flow.preview.model} · evidence revision{' '}
            {flow.preview.evidenceRevision} · voice revision {flow.preview.voiceRevision}
            {flow.preview.credentialRevision === null
              ? ''
              : ` · credential revision ${flow.preview.credentialRevision}`}
          </p>

          <div className="view">
            <span className="evidence__kind">Evidence items included</span>
            <p className="evidence__revision" data-testid="generation-preview-items">
              {flow.preview.inputEvidenceIds.length} of {flow.evidenceItems.length} selected
            </p>
          </div>

          <div className="view">
            <span className="evidence__kind">Redactions carried by those items</span>
            <p className="evidence__revision" data-testid="generation-preview-redactions">
              {flow.preview.redactions.length === 0
                ? 'None recorded.'
                : flow.preview.redactions.join(', ')}
            </p>
          </div>

          <p className="evidence__item-meta" data-testid="generation-preview-expiry">
            Authorizing sends exactly these bytes, once, to {flow.preview.providerId}. This consent
            holds until {formatInstant(flow.preview.expiresAt)}.
          </p>

          <div className="view__actions">
            <button
              type="button"
              className="action action--primary"
              data-testid="generation-generate"
              disabled={flow.runBusy}
              onClick={() => void flow.runGeneration()}
            >
              {flow.runBusy ? 'Generating…' : 'Authorize and generate'}
            </button>
            <button
              type="button"
              className="action"
              data-testid="generation-discard"
              disabled={flow.runBusy}
              onClick={flow.discardPreview}
            >
              Discard
            </button>
          </div>
        </div>
      ) : null}

      {flow.runError !== null ? (
        <p className="notice notice--error" role="alert" data-testid="generation-run-error">
          {flow.runError}
        </p>
      ) : null}

      {flow.run !== null ? (
        <div className="evidence__bundle" data-testid="generation-run">
          <h4 className="evidence__kind">Draft</h4>

          <p className="evidence__revision" data-testid="generation-run-meta">
            {flow.run.status} · {flow.run.finishReason}
            {flow.run.inputTokens === null ? '' : ` · ${flow.run.inputTokens} in / ${flow.run.outputTokens ?? 0} out`}
            {flow.run.attempts > 1 ? ` · ${flow.run.attempts} attempts` : ''}
            {flow.run.providerRequestId === null ? '' : ` · request ${flow.run.providerRequestId}`}
          </p>

          <p className="evidence__item-meta" data-testid="generation-run-validation">
            {flow.run.nativeJsonSchema
              ? 'The provider enforced the response schema itself.'
              : 'The provider cannot enforce the response schema, so the schema was sent as an instruction and the answer was checked here, locally, before it was shown.'}
          </p>

          <p className="evidence__item-text" data-testid="generation-run-text">
            {flow.run.text ?? ''}
          </p>

          <p className="evidence__item-meta" data-testid="generation-run-notice">
            This is a draft. It has not been approved, it confirms nothing, and saving it is a separate
            decision this panel does not make for you.
          </p>
        </div>
      ) : null}
    </section>
  );
}

/**
 * Why nothing can be called from this installation, in one sentence per real case.
 *
 * <b>Every clause is counted from the catalogue the server just sent.</b> This used to state that none
 * of the slots had an adapter, which was true when this package was written and stopped being true the
 * moment the first adapter package landed - and a page that keeps claiming a provider has no transport
 * after one has been installed is lying to an owner who is looking at a settings page to find out what
 * they can call. So the sentence is assembled from three numbers the server computed: how many slots
 * have an adapter, how many have a credential, and `usable`, which is the server's own answer to "what
 * can I call right now".
 *
 * The three cases are genuinely different and get three sentences. No adapter at all is a build with no
 * transport. An adapter and no credential is an owner who has not stored one. An adapter, a credential,
 * and still nothing usable is a deployment whose accepted-model list is empty - a fact about
 * configuration that the owner fixes by naming a model, and a sentence saying anything else would send
 * them to look in the wrong place.
 */
function noUsableProviderSentence(catalogue: AiProviderCatalogue): string {
  const slotCount = catalogue.supported.length;
  const withAdapter = catalogue.supported.filter((slot) => slot.adapterInstalled);
  const withCredential = catalogue.configured.length;
  const bothInstalled = withAdapter.filter((slot) => slot.configured);

  if (withAdapter.length === 0) {
    const noAdapters =
      slotCount === 1
        ? 'the one provider slot this build knows about has no adapter installed'
        : `none of the ${slotCount} provider slots this build knows about has an adapter installed`;

    if (withCredential === 0) {
      return `No provider can be called from this installation yet: no credential is stored, and ${noAdapters}. Nothing has been sent and nothing can be.`;
    }

    const credential = withCredential === 1 ? 'A credential is stored' : `${withCredential} credentials are stored`;

    return `No provider can be called from this installation yet: ${credential.toLowerCase()}, but ${noAdapters}. Nothing has been sent and nothing can be.`;
  }

  const installed =
    withAdapter.length === 1
      ? `an adapter is installed for ${withAdapter[0].id}`
      : `adapters are installed for ${withAdapter.map((slot) => slot.id).join(', ')}`;

  if (withCredential === 0) {
    return `No provider can be called from this installation yet: ${installed}, but no credential is stored for it. Nothing has been sent and nothing can be.`;
  }

  if (bothInstalled.length > 0) {
    return `No provider can be called from this installation yet: ${installed} and a credential is stored, but no model has been accepted for it, so every call would be refused. Nothing has been sent and nothing can be.`;
  }

  return `No provider can be called from this installation yet: ${installed}, but the ${withCredential} credential${
    withCredential === 1 ? ' is' : 's are'
  } stored for other providers. Nothing has been sent and nothing can be.`;
}

function slotLabel(slot: AiProviderView): string {
  if (!slot.configured) {
    return `${slot.id} — no credential stored`;
  }

  if (!slot.adapterInstalled) {
    return `${slot.id} — no adapter installed`;
  }

  return slot.id;
}

/**
 * What the panel can honestly say about a slot.
 *
 * "No adapter installed" and "the provider cannot enforce the schema" are different sentences on
 * purpose. The first is a fact about this deployment and the second about a provider, and an owner
 * who cannot tell them apart will read a green tick on a provider nothing can reach. The third case -
 * an adapter and a credential and still nothing usable - is the one this panel could previously not
 * express at all, and it is the one an operator with an empty accepted-model list is actually looking
 * at.
 */
function capabilitySentence(slot: AiProviderView, usable: boolean): string {
  if (!slot.configured) {
    // A provider that needs no credential is callable without one: the sentence says so rather
    // than sending the owner to store something the call will never read.
    if (slot.adapterInstalled && slot.capability?.requiresCredential === false) {
      return 'No credential is stored for this provider, and none is needed: it can be called without one. Name its endpoint below.';
    }

    return 'No credential is stored for this provider, so it cannot be called. Store one in the credential settings first.';
  }

  if (!slot.adapterInstalled) {
    return 'A credential is stored, but no adapter is installed for this provider, so nothing can be sent to it yet.';
  }

  if (!usable) {
    return 'An adapter is installed and a credential is stored, but no model has been accepted for it in this deployment, so every call would be refused before a credential is read. Name a model this provider accepts and it becomes usable.';
  }

  return slot.capability?.nativeJsonSchema === true
    ? 'This provider enforces the response schema itself, so a result arrives already constrained to it.'
    : 'This provider cannot enforce the response schema. It is sent as an instruction and every answer is checked here, locally, before it is shown.';
}

function formatInstant(value: string): string {
  const parsed = new Date(value);

  return Number.isNaN(parsed.getTime()) ? value : parsed.toISOString().replace('T', ' ').slice(0, 16) + ' UTC';
}
