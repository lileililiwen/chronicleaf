namespace Chronicleaf.Application.Common;

/// <summary>
/// The shared list-input bounds: every list surface defaults to 25 items and refuses more than
/// 100, so a caller can never turn a list into an unbounded read.
/// </summary>
public static class ListQueryPolicy
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>Clamps a requested page size into the accepted range.</summary>
    public static int Clamp(int? requestedPageSize) =>
        requestedPageSize is null or <= 0
            ? DefaultPageSize
            : Math.Min(requestedPageSize.Value, MaxPageSize);
}
