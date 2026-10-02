using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronicleaf.Application.Outbox;

/// <summary>
/// The versioned envelope persisted as the outbox <c>payload</c> column:
/// <c>{schemaVersion,kind,scopeId,aggregateId,revision,operationId?}</c>.
/// </summary>
public sealed record OutboxPayload
{
    /// <summary>The only schema version this build writes and dispatches.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("scopeId")]
    public required Guid ScopeId { get; init; }

    [JsonPropertyName("aggregateId")]
    public required Guid AggregateId { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("operationId")]
    public Guid? OperationId { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Wire);

    /// <summary>
    /// Parses a stored payload. A payload that is not a JSON object, or whose
    /// <c>schemaVersion</c> this build does not know, is rejected here so that no consumer ever
    /// executes an event it cannot interpret.
    /// </summary>
    public static bool TryParse(string payloadJson, out OutboxPayload? payload, out int? schemaVersion)
    {
        payload = null;
        schemaVersion = null;

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var parsedVersion))
            {
                return false;
            }

            schemaVersion = parsedVersion;
            if (parsedVersion != CurrentSchemaVersion)
            {
                return false;
            }

            try
            {
                payload = JsonSerializer.Deserialize<OutboxPayload>(root.GetRawText(), Wire);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return payload is not null;
    }
}
