using Chronicleaf.Core;

namespace Chronicleaf.Application.Agency;

/// <summary>
/// The one place the accepted client input ranges are decided (AGENCY-CLIENT-PROJECTS-001).
/// </summary>
/// <remarks>
/// Every rule the API and any later caller share lives here, so a value cannot be accepted by one
/// path and refused by another. A method returns the field errors it found; an empty result means
/// the input is acceptable. The service turns a non-empty result into the delivered 422 envelope,
/// so the wire contract is still written in exactly one place.
/// <para>
/// Nothing here reaches outside the process. Notes are plain text with a length bound only: there
/// is no markup, no link and no identity in a client, because a client is organizational context
/// rather than a login.
/// </para>
/// </remarks>
public static class AgencyClientPolicy
{
    /// <summary>At most this many clients or client projects may be asked for on one page.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The page size a caller gets when it asks for none.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>
    /// Clamps a requested page size into the accepted range. A page size is clamped rather than
    /// refused, so a caller asking for a thousand rows gets the maximum and a next cursor instead
    /// of an error.
    /// </summary>
    public static int ClampPageSize(int? requested) =>
        requested is null or <= 0 ? DefaultPageSize : Math.Min(requested.Value, MaxPageSize);

    /// <summary>Validates a new client.</summary>
    public static Dictionary<string, string[]> ValidateClient(CreateClientInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        ValidateName(input.Name, errors);
        ValidateNotes(input.Notes, errors);

        return errors;
    }

    /// <summary>Validates a partial client edit. Only the fields the caller actually sent are checked.</summary>
    public static Dictionary<string, string[]> ValidateClientPatch(ClientPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (patch.Name is { } name)
        {
            ValidateName(name, errors);
        }

        if (patch.Notes is { } notes)
        {
            ValidateNotes(notes, errors);
        }

        return errors;
    }

    private static void ValidateName(string name, Dictionary<string, string[]> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            errors["name"] = ["a name between 1 and " + Client.NameMaxLength + " characters is required."];
            return;
        }

        if (trimmed.Length > Client.NameMaxLength)
        {
            errors["name"] = [$"a name may be at most {Client.NameMaxLength} characters."];
        }
    }

    private static void ValidateNotes(string notes, Dictionary<string, string[]> errors)
    {
        if ((notes?.Length ?? 0) > Client.NotesMaxLength)
        {
            errors["notes"] = [$"notes may be at most {Client.NotesMaxLength} characters."];
        }
    }
}
