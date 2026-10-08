/**
 * The one stale-revision sentence in the Web client (WEB-FEED-003).
 *
 * A milestone edit, a project edit and a review write can all lose the same
 * race, so all three name it in the same words. The sentence is
 * entity-neutral by construction: this function takes no noun, so no call
 * site can drift into a fourth wording.
 */
export function formatRevisionConflict(storedRevision: number | null): string {
  if (storedRevision === null) {
    return 'This record changed since you opened it, so your edit was not saved.';
  }

  return `This record changed since you opened it (the stored revision is ${storedRevision}). Your edit was not saved.`;
}
