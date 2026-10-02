namespace Chronicleaf.Application.Operations;

/// <summary>
/// Resolves the one handler that owns a kind.
/// </summary>
/// <remarks>
/// The registry is a dictionary rather than a scan, and a kind with no registration is a normal,
/// reportable condition rather than a crash: an operation enqueued before its owning package
/// registers, or after a build where it did not, fails once with
/// <see cref="OperationErrorCodes.NoHandler"/> instead of retrying something that will never work.
/// </remarks>
public interface IOperationHandlerRegistry
{
    /// <summary>The handler for a kind, or <c>null</c> when none is registered.</summary>
    IOperationHandler? Find(string kind);
}

/// <summary>The default registry, built from whatever handlers are registered in the container.</summary>
public sealed class OperationHandlerRegistry(IEnumerable<IOperationHandler> handlers) : IOperationHandlerRegistry
{
    private readonly Dictionary<string, IOperationHandler> _byKind = handlers
        .Where(handler => handler is not null)
        .ToDictionary(handler => handler.Kind, StringComparer.Ordinal);

    public IOperationHandler? Find(string kind) =>
        kind is not null && _byKind.TryGetValue(kind, out var handler) ? handler : null;

    /// <summary>The kinds that have a handler, for diagnostics.</summary>
    public IReadOnlyCollection<string> RegisteredKinds => _byKind.Keys;
}
