using Chronicleaf.Core;

namespace Chronicleaf.Application.Milestones;

/// <summary>
/// The one place the accepted input ranges are decided (PROJECTS-AND-MILESTONES-001/002).
/// </summary>
/// <remarks>
/// Every rule the API and any later caller share lives here, so a value cannot be accepted by one
/// path and refused by another. A method returns the field errors it found; an empty result means
/// the input is acceptable. The service turns a non-empty result into the delivered 422 envelope, so
/// the wire contract is still written in exactly one place.
/// <para>
/// Nothing here reaches outside the process. A project URL is checked for being an http or https URL
/// and then stored as written: this package has no network call, so a URL can never cause a fetch.
/// </para>
/// </remarks>
public static class MilestonePolicy
{
    /// <summary>At most this many projects and milestones may be asked for on one page.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The page size a caller gets when it asks for none.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>How many records one import call may carry. A batch is bounded like any other input.</summary>
    public const int MaxImportBatch = 100;

    /// <summary>
    /// Clamps a requested page size into the accepted range. A page size is clamped rather than
    /// refused, so a caller asking for a thousand rows gets the maximum and a next cursor instead of
    /// an error.
    /// </summary>
    public static int ClampPageSize(int? requested) =>
        requested is null or <= 0 ? DefaultPageSize : Math.Min(requested.Value, MaxPageSize);

    /// <summary>Validates a new project.</summary>
    public static Dictionary<string, string[]> ValidateProject(CreateProjectInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        ValidateName(input.Name, required: true, errors);
        ValidateText(input.Description, "description", Project.DescriptionMaxLength, errors);
        ValidateUrl(input.ProjectUrl, "projectUrl", errors);

        return errors;
    }

    /// <summary>Validates a partial project edit. Only the fields the caller actually sent are checked.</summary>
    public static Dictionary<string, string[]> ValidateProjectPatch(ProjectPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (patch.Name is { } name)
        {
            ValidateName(name, required: true, errors);
        }

        if (patch.Description is { } description)
        {
            ValidateText(description, "description", Project.DescriptionMaxLength, errors);
        }

        if (patch.ProjectUrl is { } url)
        {
            ValidateUrl(url, "projectUrl", errors);
        }

        return errors;
    }

    /// <summary>Validates a new milestone and a candidate that arrived from a source.</summary>
    public static Dictionary<string, string[]> ValidateDraft(MilestoneDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        ValidateTitle(draft.Title, required: true, errors);
        ValidateVersion(draft.Version, errors);
        ValidateType(draft.Type, required: true, errors);
        ValidateText(draft.Notes, "notes", Milestone.NotesMaxLength, errors);
        ValidateList(draft.KnownLimitations, "knownLimitations", errors);
        ValidateList(draft.NextSteps, "nextSteps", errors);

        return errors;
    }

    /// <summary>Validates a partial milestone edit. Only the fields the caller actually sent are checked.</summary>
    public static Dictionary<string, string[]> ValidateMilestonePatch(MilestonePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (patch.Title is { } title)
        {
            ValidateTitle(title, required: true, errors);
        }

        ValidateVersion(patch.Version, errors);
        ValidateType(patch.Type, required: false, errors);
        ValidateText(patch.Notes, "notes", Milestone.NotesMaxLength, errors);
        ValidateList(patch.KnownLimitations, "knownLimitations", errors);
        ValidateList(patch.NextSteps, "nextSteps", errors);

        return errors;
    }

    /// <summary>
    /// Validates one import call. The source label is checked here and the keys are checked to be
    /// present, because a key is what makes the import idempotent: a record without one could be
    /// imported twice and would have nothing to be recognized as.
    /// </summary>
    public static Dictionary<string, string[]> ValidateImport(ImportCandidatesInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!MilestoneSource.IsWellFormed(input.Source) ||
            string.Equals(input.Source, MilestoneSource.Manual, StringComparison.Ordinal))
        {
            errors["source"] = [
                "source must be a lower-case token of letters, digits, '-' and '_' and must not be 'manual', which only the owner uses.",
            ];
        }

        if (input.Candidates.Count is 0)
        {
            errors["candidates"] = ["at least one candidate is required."];
            return errors;
        }

        if (input.Candidates.Count > MaxImportBatch)
        {
            errors["candidates"] = [$"at most {MaxImportBatch} candidates may be imported at once."];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < input.Candidates.Count; index++)
        {
            var candidate = input.Candidates[index];
            var prefix = $"candidates[{index}].";

            if (string.IsNullOrWhiteSpace(candidate.SourceKey) ||
                candidate.SourceKey.Length > MilestoneSource.SourceKeyMaxLength)
            {
                errors[prefix + "sourceKey"] =
                    [$"a source key of 1 to {MilestoneSource.SourceKeyMaxLength} characters is required."];
            }
            else if (!seen.Add(candidate.SourceKey))
            {
                // The same key twice in one call would be the same record twice, which the unique
                // index would reject as a provider error rather than as the caller's mistake.
                errors[prefix + "sourceKey"] = ["this source key is repeated in the same request."];
            }

            Add(errors, ValidateDraft(candidate.Draft), prefix);
        }

        return errors;
    }

    /// <summary>Validates a status filter on a milestone list.</summary>
    public static Dictionary<string, string[]> ValidateStatusFilter(string? status)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (status is not null && !MilestoneStatus.All.Contains(status, StringComparer.Ordinal))
        {
            errors["status"] = [$"status must be one of: {string.Join(", ", MilestoneStatus.All)}."];
        }

        return errors;
    }

    private static void ValidateName(string name, bool required, Dictionary<string, string[]> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            if (required)
            {
                errors["name"] = ["a name between 1 and " + Project.NameMaxLength + " characters is required."];
            }

            return;
        }

        if (trimmed.Length > Project.NameMaxLength)
        {
            errors["name"] = [$"a name may be at most {Project.NameMaxLength} characters."];
        }
    }

    private static void ValidateTitle(string title, bool required, Dictionary<string, string[]> errors)
    {
        var trimmed = title?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            if (required)
            {
                errors["title"] = ["a title between 1 and " + Milestone.TitleMaxLength + " characters is required."];
            }

            return;
        }

        if (trimmed.Length > Milestone.TitleMaxLength)
        {
            errors["title"] = [$"a title may be at most {Milestone.TitleMaxLength} characters."];
        }
    }

    private static void ValidateVersion(string? version, Dictionary<string, string[]> errors)
    {
        if (version is not null && version.Trim().Length > Milestone.VersionMaxLength)
        {
            errors["version"] = [$"a version label may be at most {Milestone.VersionMaxLength} characters."];
        }
    }

    private static void ValidateType(string? type, bool required, Dictionary<string, string[]> errors)
    {
        if (type is null)
        {
            if (required)
            {
                errors["type"] = [$"a type is required; one of: {string.Join(", ", MilestoneTypes.All)}."];
            }

            return;
        }

        if (!MilestoneTypes.All.Contains(type, StringComparer.Ordinal))
        {
            errors["type"] = [$"type must be one of: {string.Join(", ", MilestoneTypes.All)}."];
        }
    }

    /// <summary>
    /// Checks a stored http or https URL. The value is validated and then kept as written; nothing in
    /// this package dereferences it, so a URL can never cause an outbound request.
    /// </summary>
    private static void ValidateUrl(string? url, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (url.Length > Project.ProjectUrlMaxLength)
        {
            errors[field] = [$"a URL may be at most {Project.ProjectUrlMaxLength} characters."];
            return;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            errors[field] = ["a URL must be an absolute http or https URL."];
        }
    }

    private static void ValidateText(
        string? value,
        string field,
        int maxLength,
        Dictionary<string, string[]> errors)
    {
        if ((value?.Length ?? 0) > maxLength)
        {
            errors[field] = [$"{field} may be at most {maxLength} characters."];
        }
    }

    private static void ValidateList(
        IReadOnlyList<string>? items,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (items is null)
        {
            return;
        }

        if (items.Count > Milestone.ListItemMax)
        {
            errors[field] = [$"at most {Milestone.ListItemMax} entries may be given."];
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];

            if (string.IsNullOrWhiteSpace(item))
            {
                errors[field] = [$"entry {index + 1} may not be empty."];
            }
            else if (item.Trim().Length > Milestone.ListItemMaxLength)
            {
                errors[field] = [$"entry {index + 1} may be at most {Milestone.ListItemMaxLength} characters."];
            }
        }
    }

    /// <summary>Prefixes nested field names, so a nested error still says exactly which entry it is.</summary>
    private static void Add(
        Dictionary<string, string[]> errors,
        Dictionary<string, string[]> nested,
        string prefix)
    {
        foreach (var (field, messages) in nested)
        {
            errors[prefix + field] = messages;
        }
    }
}
