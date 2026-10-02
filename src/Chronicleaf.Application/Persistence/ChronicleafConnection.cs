using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Application.Persistence;

/// <summary>
/// The one connection setting this runtime requires.
/// </summary>
/// <remarks>
/// Configuration key <c>ConnectionStrings__Chronicleaf</c> (environment variable form
/// <c>ConnectionStrings__Chronicleaf</c>) has no default: an absent value leaves the process
/// without a database, and readiness reports <c>database_unavailable</c> rather than standing
/// in an in-memory store. The Infrastructure layer owns the decision about whether a
/// configured value is usable; the value itself is never logged, echoed in an exception
/// message, or returned over HTTP.
/// </remarks>
public static class ChronicleafConnection
{
    /// <summary>Configuration key; the environment variable form is <c>ConnectionStrings__Chronicleaf</c>.</summary>
    public const string SettingName = "ConnectionStrings:Chronicleaf";
}
