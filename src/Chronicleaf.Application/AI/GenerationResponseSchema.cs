using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chronicleaf.Application.AI;

/// <summary>
/// One response schema, and the strict local validation that stands in for a provider that cannot
/// enforce it (LLM-PROVIDER-CONTRACT-001, boundary scenario).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the fallback, and it is a real validator rather than a hope.</b> When an adapter reports
/// <c>nativeJsonSchema: false</c> the schema goes into the prompt as an instruction and every
/// returned text is parsed and checked here before it can be called a draft. So a provider that
/// ignores the instruction produces a rejection, not a broken draft - which is the whole reason the
/// capability is worth asking about.
/// </para>
/// <para>
/// The supported vocabulary is the smallest one that is worth validating: an object, required keys,
/// string properties with an optional maximum length, and whether unknown keys are refused. That is
/// deliberately not a general JSON Schema implementation. A validator that claims to understand
/// <c>oneOf</c> and silently does not is worse than one that refuses a schema it cannot check,
/// because the failure is invisible until a real draft is wrong.
/// </para>
/// <para>
/// A schema the validator cannot fully understand is refused at construction time, not at generation
/// time, so the owner learns before consenting rather than after a call has been billed.
/// </para>
/// </remarks>
public sealed class GenerationResponseSchema
{
    private readonly IReadOnlyList<string> _requiredKeys;
    private readonly IReadOnlyDictionary<string, int?> _stringProperties;

    private GenerationResponseSchema(
        string version,
        string json,
        IReadOnlyList<string> requiredKeys,
        IReadOnlyDictionary<string, int?> stringProperties,
        bool allowAdditionalProperties)
    {
        Version = version;
        Json = json;
        _requiredKeys = requiredKeys;
        _stringProperties = stringProperties;
        AllowAdditionalProperties = allowAdditionalProperties;
    }

    /// <summary>The schema's own version label, carried in the snapshot beside the schema text.</summary>
    public string Version { get; }

    /// <summary>
    /// The canonical JSON Schema, as text. This is what goes to a provider that can enforce it and
    /// what is quoted into the prompt for one that cannot.
    /// </summary>
    public string Json { get; }

    /// <summary>Whether a key the schema does not name is tolerated in a result.</summary>
    public bool AllowAdditionalProperties { get; }

    /// <summary>The required property names, in schema order.</summary>
    public IReadOnlyList<string> RequiredKeys => _requiredKeys;

    /// <summary>
    /// Parses a JSON Schema document this validator fully understands.
    /// </summary>
    /// <param name="version">The schema's version label, stored in the snapshot.</param>
    /// <param name="schema">The JSON Schema text.</param>
    /// <exception cref="ArgumentException">
    /// The document is not an object schema, or uses a keyword this validator does not implement.
    /// The message names the keyword and never echoes the document.
    /// </exception>
    public static GenerationResponseSchema Parse(string version, string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(schema);
        }
        catch (JsonException)
        {
            throw new ArgumentException(
                "The response schema is not valid JSON.",
                nameof(schema));
        }

        if (root is not JsonObject definition)
        {
            throw new ArgumentException(
                "The response schema must be a JSON object schema.",
                nameof(schema));
        }

        if (definition["type"]?.GetValue<string>() is not "object")
        {
            throw new ArgumentException(
                "Only an object response schema is supported.",
                nameof(schema));
        }

        var required = new List<string>();
        if (definition["required"] is JsonArray requiredArray)
        {
            foreach (var node in requiredArray)
            {
                if (node is null)
                {
                    throw new ArgumentException("A required key cannot be null.", nameof(schema));
                }

                required.Add(node.GetValue<string>());
            }
        }

        var properties = new Dictionary<string, int?>(StringComparer.Ordinal);
        if (definition["properties"] is JsonObject propertyMap)
        {
            foreach (var member in propertyMap)
            {
                if (member.Value is not JsonObject property)
                {
                    throw new ArgumentException(
                        $"The schema for property '{member.Key}' is not an object.",
                        nameof(schema));
                }

                if (property["type"]?.GetValue<string>() is not "string")
                {
                    throw new ArgumentException(
                        $"Only string properties are supported, and '{member.Key}' is not one.",
                        nameof(schema));
                }

                int? maxLength = null;
                if (property["maxLength"] is { } lengthNode)
                {
                    maxLength = lengthNode.GetValue<int>();
                    if (maxLength <= 0)
                    {
                        throw new ArgumentException(
                            $"The maxLength for property '{member.Key}' is not a positive number.",
                            nameof(schema));
                    }
                }

                properties[member.Key] = maxLength;
            }
        }

        if (required.Count == 0)
        {
            throw new ArgumentException(
                "The response schema requires no keys, so a result could not be checked for anything.",
                nameof(schema));
        }

        var allowAdditional = definition["additionalProperties"]?.GetValue<bool>() ?? true;

        // Canonical text, so two identical schemas hash identically regardless of how they were
        // written. The document is rebuilt from the understood subset rather than echoed.
        var canonical = Rebuild(schema, required, properties, allowAdditional);

        return new GenerationResponseSchema(
            version,
            canonical,
            required,
            properties,
            allowAdditional);
    }

    /// <summary>
    /// Checks a returned text against the schema, strictly and locally.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the text satisfies the schema, or a short reason describing the first thing
    /// that does not. The reason is a property name or a parse failure, never the returned text -
    /// this string is not going anywhere near a log or an envelope, but it is not the place to start
    /// reproducing provider prose.
    /// </returns>
    public string? Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "the result carried no text";
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return "the result was not valid JSON";
        }

        if (root is not JsonObject result)
        {
            return "the result was not a JSON object";
        }

        foreach (var key in _requiredKeys)
        {
            if (!result.ContainsKey(key) || result[key] is null)
            {
                return $"the result is missing the required key '{key}'";
            }
        }

        foreach (var member in result)
        {
            if (!_stringProperties.ContainsKey(member.Key))
            {
                if (!AllowAdditionalProperties)
                {
                    return $"the result carried the undeclared key '{member.Key}'";
                }

                continue;
            }

            string? value;
            try
            {
                value = member.Value?.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                return $"the result's '{member.Key}' was not a string";
            }
            catch (FormatException)
            {
                return $"the result's '{member.Key}' was not a string";
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return $"the result's '{member.Key}' was empty";
            }

            var maxLength = _stringProperties[member.Key];
            if (maxLength.HasValue && value.Length > maxLength.Value)
            {
                return $"the result's '{member.Key}' exceeded the schema's maximum length";
            }
        }

        return null;
    }

    /// <summary>
    /// The instruction appended to the system prompt when the provider cannot enforce the schema
    /// itself.
    /// </summary>
    /// <remarks>
    /// The wording does the two things that matter. It asks for one JSON object and nothing else, so
    /// the validator is not asked to dig a schema out of prose; and it states that the result is
    /// checked and discarded if it does not conform, so a provider that ignores the instruction
    /// wastes one call instead of producing a draft that looks fine.
    /// </remarks>
    public string FallbackInstruction =>
        "Return exactly one JSON object and nothing else - no prose before or after it, no code fence. "
        + "It must satisfy this JSON Schema exactly: " + Json;

    private static string Rebuild(
        string original,
        IReadOnlyList<string> required,
        IReadOnlyDictionary<string, int?> properties,
        bool allowAdditional)
    {
        _ = original;

        var propertiesNode = new JsonObject();
        foreach (var property in properties)
        {
            var definition = new JsonObject { ["type"] = "string" };
            if (property.Value.HasValue)
            {
                definition["maxLength"] = property.Value.Value;
            }

            propertiesNode[property.Key] = definition;
        }

        var requiredNode = new JsonArray();
        foreach (var key in required)
        {
            requiredNode.Add(key);
        }

        var document = new JsonObject
        {
            ["additionalProperties"] = allowAdditional,
            ["properties"] = propertiesNode,
            ["required"] = requiredNode,
            ["type"] = "object",
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
