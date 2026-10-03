namespace Chronicleaf.Application.AI;

/// <summary>
/// The wall-clock budget one bounded call is given.
/// </summary>
/// <remarks>
/// <para>
/// This exists as an injected value so a test can prove the <i>decision</i> a timeout produces - 504,
/// one attempt, no retry - without waiting the full budget. It is not a setting.
/// </para>
/// <para>
/// <b>No configuration key binds it.</b> There is no <c>Chronicleaf:…</c> name for a timeout, no
/// environment variable and no database row that could change it, and the production registration
/// supplies <see cref="Default"/>. The one way to move it is to hand a different instance to the
/// service's constructor, which only a composition root can do and only a test does. That is a
/// deliberate asymmetry: the budget is a safety property, and a safety property that an operator can
/// raise at start-up is a safety property that eventually will be raised.
/// </para>
/// <para>
/// The default is <see cref="GenerationLimits.TimeoutSeconds"/> - the same number the catalogue
/// publishes and the same one a scenario asserts the request carries - so a test that shrinks the
/// budget is proving the decision and not the number.
/// </para>
/// </remarks>
/// <param name="TimeoutSeconds">The per-attempt budget, in seconds.</param>
public sealed record GenerationCallOptions(int TimeoutSeconds)
{
    /// <summary>The budget every production call gets.</summary>
    public static GenerationCallOptions Default { get; } = new(GenerationLimits.TimeoutSeconds);
}
