/**
 * The shared `maxLength` counter (WEB-FEED-005).
 *
 * Every bounded field renders one as the field's sibling, with the same
 * literal as the field's own `maxLength`, so the counter can never lie about
 * the bound: `n / max`, `n` the live length.
 */
export function FieldCount({
  value,
  max,
  testId,
}: {
  readonly value: string;
  readonly max: number;
  readonly testId: string;
}) {
  return (
    <small className="form__count" data-testid={testId}>
      {value.length} / {max}
    </small>
  );
}
