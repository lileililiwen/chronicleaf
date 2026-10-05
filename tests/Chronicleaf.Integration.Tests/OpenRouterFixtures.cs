namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The OpenRouter chat completions wire shapes, pinned from the provider's own reference on 2026-09-30
/// (LLM-OPENROUTER-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are.</b> The documented shape of <c>POST /api/v1/chat/completions</c> and the
/// completion object it returns, transcribed as fixtures so a scenario can drive every branch
/// of the mapping - completed, refused, tool calls, truncated, unexpected finish, malformed,
/// throttled, rejected - without a network and without inventing a shape no provider has published.
/// </para>
/// <para>
/// <b>What they are not.</b> Evidence that OpenRouter works. A fixture proves that this adapter reads
/// the documented shape correctly; it cannot prove that a live account, a live key or a live model
/// behaves the same way, and no scenario in this suite may be reported as if it did. The real
/// provider's current field set is the reference's, not this file's - which is exactly why the
/// adapter reads members defensively and treats anything it does not recognise as unusable rather
/// than as text.
/// </para>
/// <para>
/// Every body here is written with the same member names the reference documents -
/// <c>choices[].message.content</c>, <c>choices[].finish_reason</c>,
/// <c>usage.prompt_tokens</c> - so a change to the adapter that broke the real dialect would break
/// these fixtures rather than passing them.
/// </para>
/// </remarks>
internal static class OpenRouterFixtures
{
    /// <summary>
    /// A model this deployment's allowlist names. A fixture, not a claim: the contract deliberately
    /// keeps no model list, and the allowlist is deployment configuration, so a scenario supplies one
    /// and the product still never chooses a model on an owner's behalf.
    /// </summary>
    public const string KnownModel = "openrouter-test-wire-shape";

    /// <summary>A model this deployment's allowlist does not name.</summary>
    public const string UnknownModel = "openrouter-not-in-the-allowlist";

    /// <summary>The secret sealed under the credential a scenario stores. Never leaves this file.</summary>
    public const string StoredSecret = "sk-or-chronicleaf-openrouter-adapter-sentinel-7e2a9c4b1d8f";

    /// <summary>A provider-assigned generation id, carried as the run's correlation handle.</summary>
    public const string GenerationId = "gen_test_wire_shape_01a2b3c4d5e6f";

    /// <summary>The exact origin and path the adapter is allowed to reach.</summary>
    public static string ChatCompletionsUri() => "https://openrouter.ai/api/v1/chat/completions";

    /// <summary>
    /// A completed response: one choice carrying one assistant message.
    /// </summary>
    /// <param name="text">The generated text, as the provider would return it.</param>
    /// <param name="inputTokens">Prompt tokens, or null to omit the usage block entirely.</param>
    /// <param name="outputTokens">Completion tokens, or null to omit them from the block.</param>
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
                  "prompt_tokens": {{(inputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "completion_tokens": {{(outputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "total_tokens": {{((inputTokens ?? 0) + (outputTokens ?? 0)).ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                }
                """;

        return $$"""
                {
                  "id": "{{GenerationId}}",
                  "provider": "Test Upstream",
                  "model": "{{KnownModel}}",
                  "object": "chat.completion",
                  "created": 1759219200,
                  "choices": [
                    {
                      "logprobs": null,
                      "finish_reason": "stop",
                      "native_finish_reason": "stop",
                      "index": 0,
                      "message": {
                        "role": "assistant",
                        "content": {{System.Text.Json.JsonSerializer.Serialize(text)}}
                      }
                    }
                  ]
                  {{usage}}
                }
                """;
    }

    /// <summary>A completed response whose usage block is absent, so usage must be reported unknown.</summary>
    public static string NoUsageBody(string text) => CompletedBody(text, null, null);

    /// <summary>A choice the upstream stopped on its own policy grounds, carrying partial text on purpose.</summary>
    /// <remarks>
    /// A refusal keeps no text even when the provider sent a partial one, so this fixture proves the
    /// partial text is discarded rather than published as a draft.
    /// </remarks>
    public static string RefusalBody(string partialText) =>
        $$"""
          {
            "id": "{{GenerationId}}",
            "provider": "Test Upstream",
            "model": "{{KnownModel}}",
            "object": "chat.completion",
            "created": 1759219200,
            "choices": [
              {
                "finish_reason": "content_filter",
                "native_finish_reason": "content_filter",
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": {{System.Text.Json.JsonSerializer.Serialize(partialText)}}
                }
              }
            ],
            "usage": { "prompt_tokens": 300, "completion_tokens": 41, "total_tokens": 341 }
          }
          """;

    /// <summary>An answer cut off by the output ceiling.</summary>
    /// <remarks>
    /// The partial text is present on purpose. A truncation is never a draft behind this endpoint -
    /// the ceiling may be an upstream hop's rather than the named model's - so this fixture proves the
    /// prefix is refused rather than presented as whole or as short.
    /// </remarks>
    public static string TruncatedBody(string partialText) =>
        $$"""
          {
            "id": "{{GenerationId}}",
            "provider": "Test Upstream",
            "model": "{{KnownModel}}",
            "object": "chat.completion",
            "created": 1759219200,
            "choices": [
              {
                "finish_reason": "length",
                "native_finish_reason": "length",
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": {{System.Text.Json.JsonSerializer.Serialize(partialText)}}
                }
              }
            ],
            "usage": { "prompt_tokens": 300, "completion_tokens": 4096, "total_tokens": 4396 }
          }
          """;

    /// <summary>A response whose choice asks for a tool call, in a contract that has no tools.</summary>
    public static string ToolCallsBody(string text) =>
        $$"""
          {
            "id": "{{GenerationId}}",
            "provider": "Test Upstream",
            "model": "{{KnownModel}}",
            "object": "chat.completion",
            "created": 1759219200,
            "choices": [
              {
                "finish_reason": "tool_calls",
                "native_finish_reason": "tool_calls",
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": {{System.Text.Json.JsonSerializer.Serialize(text)}},
                  "tool_calls": [
                    {
                      "id": "call_test_wire_shape",
                      "type": "function",
                      "function": { "name": "publish_draft", "arguments": "{}" }
                    }
                  ]
                }
              }
            ],
            "usage": { "prompt_tokens": 300, "completion_tokens": 41, "total_tokens": 341 }
          }
          """;

    /// <summary>A stopped choice carrying no text at all.</summary>
    public const string EmptyContentBody = """
        {
          "id": "gen_test_wire_shape_01a2b3c4d5e6f",
          "provider": "Test Upstream",
          "model": "openrouter-test-wire-shape",
          "object": "chat.completion",
          "created": 1759219200,
          "choices": [
            {
              "finish_reason": "stop",
              "native_finish_reason": "stop",
              "index": 0,
              "message": {
                "role": "assistant",
                "content": ""
              }
            }
          ],
          "usage": { "prompt_tokens": 300, "completion_tokens": 0, "total_tokens": 300 }
        }
        """;

    /// <summary>A choice stopped for a reason this transport never asked for, carrying text on purpose.</summary>
    /// <remarks>
    /// The text is real but the finish is not one this transport reads, so publishing it would present
    /// an answer cut by an unnamed condition as if it were whole.
    /// </remarks>
    public static string UnexpectedFinishBody(string text) =>
        $$"""
          {
            "id": "{{GenerationId}}",
            "provider": "Test Upstream",
            "model": "{{KnownModel}}",
            "object": "chat.completion",
            "created": 1759219200,
            "choices": [
              {
                "finish_reason": "upstream_timeout",
                "native_finish_reason": "upstream_timeout",
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": {{System.Text.Json.JsonSerializer.Serialize(text)}}
                }
              }
            ],
            "usage": { "prompt_tokens": 300, "completion_tokens": 41, "total_tokens": 341 }
          }
          """;

    /// <summary>A 200 that is not the JSON this transport reads.</summary>
    public const string MalformedBody = """{"object":"chat.completion","choices":"not-an-array"}""";

    /// <summary>A 200 that is valid JSON but not an object.</summary>
    public const string NotAnObjectBody = """["chat.completion"]""";

    /// <summary>A 200 naming no choice at all.</summary>
    public const string NoChoicesBody = """{"id":"gen_test_wire_shape_01a2b3c4d5e6f","object":"chat.completion"}""";

    /// <summary>The documented error body shape, for every non-success status.</summary>
    /// <remarks>
    /// Used for 401, 403, 429, 500, 503 and a 3xx alike. An adapter that read a message out of an error
    /// body and put it in a result or a log would leak provider prose - which is where a customer's name
    /// and occasionally a key are most likely to be sitting in plain text - so the suite asserts
    /// against these bodies that nothing came back out.
    /// </remarks>
    public const string ErrorBody = """
        {
          "error": {
            "message": "No auth credentials found. Add an OpenRouter API key to this request.",
            "code": 401
          }
        }
        """;
}
