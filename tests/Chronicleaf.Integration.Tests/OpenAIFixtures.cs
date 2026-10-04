namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The OpenAI Responses API wire shapes, pinned from the provider's own reference on 2026-09-30
/// (LLM-OPENAI-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are.</b> The documented shape of <c>POST /v1/responses</c> and the response object it
/// returns, transcribed as fixtures so a scenario can drive every branch of the mapping - completed,
/// refused, truncated, malformed, throttled, rejected - without a network and without inventing a
/// shape no provider has published.
/// </para>
/// <para>
/// <b>What they are not.</b> Evidence that OpenAI works. A fixture proves that this adapter reads the
/// documented shape correctly; it cannot prove that a live account, a live key or a live model behaves
/// the same way, and no scenario in this suite may be reported as if it did. The real provider's
/// current field set is the reference's, not this file's - which is exactly why the adapter reads
/// members defensively and treats anything it does not recognise as unusable rather than as text.
/// </para>
/// <para>
/// Every body here is written with the same member names the reference documents - <c>output</c>,
/// <c>content</c>, <c>type: output_text</c>, <c>type: refusal</c>, <c>incomplete_details.reason</c>,
/// <c>usage.input_tokens</c> - so a change to the adapter that broke the real dialect would break
/// these fixtures rather than passing them.
/// </para>
/// </remarks>
internal static class OpenAIFixtures
{
    /// <summary>
    /// A model this deployment's allowlist names. A fixture, not a claim: the contract deliberately
    /// keeps no model list, and the allowlist is deployment configuration, so a scenario supplies one
    /// and the product still never chooses a model on an owner's behalf.
    /// </summary>
    public const string KnownModel = "gpt-test-wire-shape";

    /// <summary>A model this deployment's allowlist does not name.</summary>
    public const string UnknownModel = "gpt-not-in-the-allowlist";

    /// <summary>The secret sealed under the credential a scenario stores. Never leaves this file.</summary>
    public const string StoredSecret = "sk-chronicleaf-openai-adapter-sentinel-4b7e1d9a2c6f";

    /// <summary>The exact origin and path the adapter is allowed to reach, from the design.</summary>
    public const string ResponsesUri = "https://api.openai.com/v1/responses";

    /// <summary>
    /// A completed response: one output message carrying one <c>output_text</c> part.
    /// </summary>
    /// <param name="text">The generated text, as the provider would return it.</param>
    /// <param name="responseId">The provider's own request id, which the result carries for correlation.</param>
    /// <param name="inputTokens">Input tokens, or null to omit the block entirely.</param>
    /// <param name="outputTokens">Output tokens, or null to omit them.</param>
    public static string CompletedBody(
        string text,
        string responseId = "resp_completed_0001",
        int? inputTokens = 812,
        int? outputTokens = 96)
    {
        var usage = inputTokens is null && outputTokens is null
            ? string.Empty
            : $$"""
                  ,
                "usage": {
                  "input_tokens": {{(inputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "output_tokens": {{(outputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "total_tokens": {{((inputTokens ?? 0) + (outputTokens ?? 0)).ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                }
                """;

        return $$"""
                {
                  "id": "{{responseId}}",
                  "object": "response",
                  "created_at": 1750000000,
                  "model": "{{KnownModel}}",
                  "status": "completed",
                  "error": null,
                  "incomplete_details": null,
                  "output": [
                    {
                      "type": "message",
                      "id": "msg_0001",
                      "status": "completed",
                      "role": "assistant",
                      "content": [
                        {
                          "type": "output_text",
                          "text": {{System.Text.Json.JsonSerializer.Serialize(text)}},
                          "annotations": []
                        }
                      ]
                    }
                  ]
                  {{usage}}
                }
                """;
    }

    /// <summary>A completed response whose text is split across two output messages.</summary>
    /// <remarks>
    /// The reference is explicit that the length and order of the output array depend on the model, so
    /// an adapter that read only the first item would silently truncate an answer the provider split.
    /// This fixture is what makes that a failing assertion rather than a reviewer's hope.
    /// </remarks>
    public static string SplitOutputBody(string first, string second, string responseId = "resp_split_0001") =>
        $$"""
          {
            "id": "{{responseId}}",
            "object": "response",
            "status": "completed",
            "output": [
              {
                "type": "message",
                "role": "assistant",
                "content": [ { "type": "output_text", "text": {{System.Text.Json.JsonSerializer.Serialize(first)}} } ]
              },
              {
                "type": "message",
                "role": "assistant",
                "content": [ { "type": "output_text", "text": {{System.Text.Json.JsonSerializer.Serialize(second)}} } ]
              }
            ],
            "usage": { "input_tokens": 40, "output_tokens": 12 }
          }
          """;

    /// <summary>A refusal: the provider declined, and sent its own explanation.</summary>
    /// <remarks>
    /// The <c>refusal</c> text is present on purpose. A refusal's explanation is provider prose about
    /// the owner's content, and the contract keeps no text on a refusal - so this fixture proves the
    /// explanation is discarded rather than published.
    /// </remarks>
    public static string RefusedBody(string responseId = "resp_refused_0001") =>
        $$"""
          {
            "id": "{{responseId}}",
            "object": "response",
            "status": "completed",
            "error": null,
            "incomplete_details": null,
            "output": [
              {
                "type": "message",
                "id": "msg_0002",
                "status": "completed",
                "role": "assistant",
                "content": [
                  { "type": "refusal", "refusal": "I can't help with that request." }
                ]
              }
            ]
          }
          """;

    /// <summary>An incomplete response cut off by the output ceiling.</summary>
    public static string TruncatedBody(string partialText, string responseId = "resp_truncated_0001") =>
        $$"""
          {
            "id": "{{responseId}}",
            "object": "response",
            "status": "incomplete",
            "error": null,
            "incomplete_details": { "reason": "max_output_tokens" },
            "output": [
              {
                "type": "message",
                "id": "msg_0003",
                "status": "incomplete",
                "role": "assistant",
                "content": [ { "type": "output_text", "text": {{System.Text.Json.JsonSerializer.Serialize(partialText)}} } ]
              }
            ],
            "usage": { "input_tokens": 300, "output_tokens": 4096 }
          }
          """;

    /// <summary>An incomplete response the provider's own safety policy cut off.</summary>
    public static string ContentFilteredBody(string responseId = "resp_filtered_0001") =>
        $$"""
          {
            "id": "{{responseId}}",
            "object": "response",
            "status": "incomplete",
            "error": null,
            "incomplete_details": { "reason": "content_filter" },
            "output": []
          }
          """;

    /// <summary>A failed response: the provider answered and reported that it failed.</summary>
    public static string FailedBody(string responseId = "resp_failed_0001") =>
        $$"""
          {
            "id": "{{responseId}}",
            "object": "response",
            "status": "failed",
            "error": { "code": "server_error", "message": "The model failed to produce a response." },
            "incomplete_details": null,
            "output": []
          }
          """;

    /// <summary>A response whose output is a function call, in a contract that has no tools.</summary>
    public static string ToolCallBody(string responseId = "resp_toolcall_0001") =>
        $$"""
          {
            "id": "{{responseId}}",
            "object": "response",
            "status": "completed",
            "error": null,
            "incomplete_details": null,
            "output": [
              {
                "type": "function_call",
                "id": "fc_0001",
                "call_id": "call_0001",
                "name": "publish_draft",
                "arguments": "{}"
              }
            ]
          }
          """;

    /// <summary>A completed response carrying no text at all.</summary>
    public static string EmptyOutputBody(string responseId = "resp_empty_0001") =>
        $$"""
          {
            "id": "{{responseId}}",
            "object": "response",
            "status": "completed",
            "error": null,
            "incomplete_details": null,
            "output": [
              { "type": "message", "id": "msg_0004", "status": "completed", "role": "assistant", "content": [] }
            ]
          }
          """;

    /// <summary>A completed response with no usage block, so usage must be reported unknown.</summary>
    public const string NoUsageBody = """
        {
          "id": "resp_nousage_0001",
          "object": "response",
          "status": "completed",
          "error": null,
          "incomplete_details": null,
          "output": [
            {
              "type": "message",
              "id": "msg_0005",
              "status": "completed",
              "role": "assistant",
              "content": [ { "type": "output_text", "text": "{\"title\":\"t\"}" } ]
            }
          ]
        }
        """;

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
            "message": "Incorrect API key provided: sk-proj-***. You can find your API key at https://platform.openai.com/account/api-keys.",
            "type": "invalid_request_error",
            "param": null,
            "code": "invalid_api_key"
          }
        }
        """;

    /// <summary>A 200 whose body is not JSON, so it is an answer and not a draft.</summary>
    public const string MalformedBody = "{ this is not json";

    /// <summary>A 200 whose body is JSON but not the object this transport reads.</summary>
    public const string NotAnObjectBody = "[\"unexpected\",\"shape\"]";
}
