namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The Anthropic messages wire shapes, pinned from the provider's own reference on 2026-09-30
/// (LLM-ANTHROPIC-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are.</b> The documented shape of <c>POST /v1/messages</c> and the message object it
/// returns, transcribed as fixtures so a scenario can drive every branch of the mapping - completed,
/// refused, tool use, truncated, unexpected stop, malformed, throttled, rejected - without a network
/// and without inventing a shape no provider has published.
/// </para>
/// <para>
/// <b>What they are not.</b> Evidence that Anthropic works. A fixture proves that this adapter reads
/// the documented shape correctly; it cannot prove that a live account, a live key or a live model
/// behaves the same way, and no scenario in this suite may be reported as if it did. The real
/// provider's current field set is the reference's, not this file's - which is exactly why the
/// adapter reads members defensively and treats anything it does not recognise as unusable rather
/// than as text.
/// </para>
/// <para>
/// Every body here is written with the same member names the reference documents -
/// <c>content[].type</c>, <c>content[].text</c>, <c>stop_reason</c>, <c>usage.input_tokens</c> - so a
/// change to the adapter that broke the real dialect would break these fixtures rather than passing
/// them.
/// </para>
/// </remarks>
internal static class AnthropicFixtures
{
    /// <summary>
    /// A model this deployment's allowlist names. A fixture, not a claim: the contract deliberately
    /// keeps no model list, and the allowlist is deployment configuration, so a scenario supplies one
    /// and the product still never chooses a model on an owner's behalf.
    /// </summary>
    public const string KnownModel = "claude-test-wire-shape";

    /// <summary>A model this deployment's allowlist does not name.</summary>
    public const string UnknownModel = "claude-not-in-the-allowlist";

    /// <summary>The secret sealed under the credential a scenario stores. Never leaves this file.</summary>
    public const string StoredSecret = "sk-ant-chronicleaf-anthropic-adapter-sentinel-4b8d2f6a9e1c";

    /// <summary>A provider-assigned message id, carried as the run's correlation handle.</summary>
    public const string MessageId = "msg_test_wire_shape_01a2b3c4d5e6f";

    /// <summary>The exact origin and path the adapter is allowed to reach.</summary>
    public static string MessagesUri() => "https://api.anthropic.com/v1/messages";

    /// <summary>
    /// A completed response: one message carrying one text block.
    /// </summary>
    /// <param name="text">The generated text, as the provider would return it.</param>
    /// <param name="inputTokens">Input tokens, or null to omit the usage block entirely.</param>
    /// <param name="outputTokens">Output tokens, or null to omit them from the block.</param>
    public static string CompletedBody(
        string text,
        int? inputTokens = 812,
        int? outputTokens = 96)
    {
        var usage = inputTokens is null && outputTokens is null
            ? string.Empty
            : $$"""
                  ,
                "usage": {
                  "input_tokens": {{(inputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "output_tokens": {{(outputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                }
                """;

        return $$"""
                {
                  "id": "{{MessageId}}",
                  "type": "message",
                  "role": "assistant",
                  "model": "{{KnownModel}}",
                  "content": [
                    {
                      "type": "text",
                      "text": {{System.Text.Json.JsonSerializer.Serialize(text)}}
                    }
                  ],
                  "stop_reason": "end_turn",
                  "stop_sequence": null
                  {{usage}}
                }
                """;
    }

    /// <summary>A completed response whose text is split across two text blocks.</summary>
    /// <remarks>
    /// The provider may split an answer across blocks, so an adapter that read only the first string
    /// would silently truncate an answer the provider split. This fixture is what makes that a
    /// failing assertion rather than a reviewer's hope.
    /// </remarks>
    public static string SplitBlocksBody(string first, string second) =>
        $$"""
          {
            "id": "{{MessageId}}",
            "type": "message",
            "role": "assistant",
            "model": "{{KnownModel}}",
            "content": [
              { "type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(first)}} },
              { "type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(second)}} }
            ],
            "stop_reason": "end_turn",
            "stop_sequence": null,
            "usage": { "input_tokens": 40, "output_tokens": 12 }
          }
          """;

    /// <summary>A message the provider declined, carrying partial text on purpose.</summary>
    /// <remarks>
    /// Both the refusal stop reason and a refusal content block are present. A refusal keeps no text
    /// even when the provider sent a partial one, so this fixture proves the partial text is
    /// discarded rather than published as a draft.
    /// </remarks>
    public static string RefusalBody(string partialText) =>
        $$"""
          {
            "id": "{{MessageId}}",
            "type": "message",
            "role": "assistant",
            "model": "{{KnownModel}}",
            "content": [
              { "type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(partialText)}} },
              { "type": "refusal", "refusal": "I cannot help with that." }
            ],
            "stop_reason": "refusal",
            "stop_sequence": null,
            "usage": { "input_tokens": 300, "output_tokens": 41 }
          }
          """;

    /// <summary>An answer cut off by the output ceiling.</summary>
    public static string TruncatedBody(string partialText) =>
        $$"""
          {
            "id": "{{MessageId}}",
            "type": "message",
            "role": "assistant",
            "model": "{{KnownModel}}",
            "content": [
              { "type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(partialText)}} }
            ],
            "stop_reason": "max_tokens",
            "stop_sequence": null,
            "usage": { "input_tokens": 300, "output_tokens": 4096 }
          }
          """;

    /// <summary>A response whose content asks for a tool call, in a contract that has no tools.</summary>
    public static string ToolUseBody(string partialText) =>
        $$"""
          {
            "id": "{{MessageId}}",
            "type": "message",
            "role": "assistant",
            "model": "{{KnownModel}}",
            "content": [
              { "type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(partialText)}} },
              { "type": "tool_use", "id": "toolu_test_wire_shape_01", "name": "publish_draft", "input": {} }
            ],
            "stop_reason": "tool_use",
            "stop_sequence": null,
            "usage": { "input_tokens": 300, "output_tokens": 41 }
          }
          """;

    /// <summary>A finished message carrying no text at all.</summary>
    public static string EmptyContentBody =>
        $$"""
          {
            "id": "{{MessageId}}",
            "type": "message",
            "role": "assistant",
            "model": "{{KnownModel}}",
            "content": [],
            "stop_reason": "end_turn",
            "stop_sequence": null
          }
          """;

    /// <summary>A message stopped for a condition nobody set, so it is not a finish this transport reads.</summary>
    /// <remarks>
    /// This adapter never sends <c>stop_sequences</c>. A <c>stop_sequence</c> answer is therefore a
    /// stop for a condition the call did not name, and it is unusable rather than complete - even
    /// with text in it.
    /// </remarks>
    public static string StopSequenceBody(string text) =>
        $$"""
          {
            "id": "{{MessageId}}",
            "type": "message",
            "role": "assistant",
            "model": "{{KnownModel}}",
            "content": [
              { "type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(text)}} }
            ],
            "stop_reason": "stop_sequence",
            "stop_sequence": "Human:",
            "usage": { "input_tokens": 300, "output_tokens": 41 }
          }
          """;

    /// <summary>A completed response with no usage block, so usage must be reported unknown.</summary>
    /// <remarks>
    /// The text is a schema-shaped draft on purpose: with no native schema enforcement the delivered
    /// contract validates the text locally, so an unshaped text would be refused for its shape rather
    /// than accepted with unknown usage - and this fixture is about the usage, not the shape.
    /// </remarks>
    public static string NoUsageBody =>
        $$"""
          {
            "id": "{{MessageId}}",
            "type": "message",
            "role": "assistant",
            "model": "{{KnownModel}}",
            "content": [
              { "type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(NoUsageDraft)}} }
            ],
            "stop_reason": "end_turn",
            "stop_sequence": null
          }
          """;

    /// <summary>A schema-shaped draft, so a no-usage answer is accepted with unknown usage.</summary>
    private const string NoUsageDraft =
        """{"title":"第一次可复现发布","summary":"发布流程改成了可复现的流水线。","knownLimitations":"只覆盖了 Linux。","nextSteps":"补上 Windows。"}""";

    /// <summary>The documented error body shape, for every non-success status.</summary>
    /// <remarks>
    /// Used for 401, 403, 429, 500, 503, 529 and a 3xx alike. An adapter that read a message out of
    /// an error body and put it in a result or a log would leak provider prose - which is where a
    /// customer's name and occasionally a key are most likely to be sitting in plain text - so the
    /// suite asserts against these bodies that nothing came back out.
    /// </remarks>
    public const string ErrorBody = """
        {
          "type": "error",
          "error": {
            "type": "authentication_error",
            "message": "invalid x-api-key: the provided key does not match any known credential. See https://platform.claude.com/docs/en/api/errors for details."
          }
        }
        """;

    /// <summary>A 200 whose body is not JSON, so it is an answer and not a draft.</summary>
    public const string MalformedBody = "{ this is not json";

    /// <summary>A 200 whose body is JSON but not the object this transport reads.</summary>
    public const string NotAnObjectBody = "[\"unexpected\",\"shape\"]";
}
