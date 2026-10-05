namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The Ollama chat wire shapes, pinned from the provider's own reference on 2026-09-30
/// (LLM-OLLAMA-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are.</b> The documented shape of <c>POST /api/chat</c> and the chat object it
/// returns, transcribed as fixtures so a scenario can drive every branch of the mapping -
/// completed, unfinished, truncated, unexpected stop reason, malformed, throttled, rejected -
/// without a network and without inventing a shape no provider has published.
/// </para>
/// <para>
/// <b>What they are not.</b> Evidence that Ollama works. A fixture proves that this adapter reads
/// the documented shape correctly; it cannot prove that a live daemon, a live model or a live
/// endpoint behaves the same way, and no scenario in this suite may be reported as if it did. The
/// real provider's current field set is the reference's, not this file's - which is exactly why the
/// adapter reads members defensively and treats anything it does not recognise as unusable rather
/// than as text.
/// </para>
/// <para>
/// Every body here is written with the same member names the reference documents -
/// <c>message.content</c>, <c>done</c>, <c>done_reason</c>,
/// <c>prompt_eval_count</c>/<c>eval_count</c> - so a change to the adapter that broke the real
/// dialect would break these fixtures rather than passing them.
/// </para>
/// </remarks>
internal static class OllamaFixtures
{
    /// <summary>
    /// A model this deployment's allowlist names. A fixture, not a claim: the contract deliberately
    /// keeps no model list, and the allowlist is deployment configuration, so a scenario supplies one
    /// and the product still never chooses a model on an owner's behalf.
    /// </summary>
    public const string KnownModel = "ollama-test-wire-shape";

    /// <summary>A model this deployment's allowlist does not name.</summary>
    public const string UnknownModel = "ollama-not-in-the-allowlist";

    /// <summary>The secret sealed under the credential a scenario stores. Never leaves this file.</summary>
    public const string StoredSecret = "ollama-proxy-chronicleaf-adapter-sentinel-4f1e8b2a9d6c";

    /// <summary>
    /// An origin this deployment's endpoint allowlist names. A fixture, not a claim: the adapter
    /// never defaults to it, and a scenario supplies it as the consented endpoint.
    /// </summary>
    public const string AllowedEndpoint = "http://127.0.0.1:11434";

    /// <summary>An origin no deployment allowlists.</summary>
    public const string ForeignEndpoint = "http://192.0.2.1:11434";

    /// <summary>The exact origin and path the adapter is allowed to reach.</summary>
    public static string ChatUri() => AllowedEndpoint + "/api/chat";

    /// <summary>
    /// A completed response: one assistant message, finished for the documented reason.
    /// </summary>
    /// <param name="text">The generated text, as the provider would return it.</param>
    /// <param name="inputTokens">Prompt eval count, or null to omit the usage counts entirely.</param>
    /// <param name="outputTokens">Eval count, or null to omit it from the body.</param>
    public static string CompletedBody(
        string text,
        int? inputTokens = 812,
        int? outputTokens = 96)
    {
        var counts = inputTokens is null && outputTokens is null
            ? string.Empty
            : $$"""
                ,
                "prompt_eval_count": {{(inputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                "eval_count": {{(outputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                """;

        return $$"""
                {
                  "model": "{{KnownModel}}",
                  "created_at": "2026-09-30T12:00:00.000Z",
                  "message": {
                    "role": "assistant",
                    "content": {{System.Text.Json.JsonSerializer.Serialize(text)}}
                  },
                  "done": true,
                  "done_reason": "stop",
                  "total_duration": 123456789
                  {{counts}}
                }
                """;
    }

    /// <summary>A completed response whose usage counts are absent, so usage must be reported unknown.</summary>
    public static string NoUsageBody(string text) => CompletedBody(text, null, null);

    /// <summary>
    /// A generation that did not finish, carrying partial text on purpose.
    /// </summary>
    /// <remarks>
    /// The partial text is real, and the scenario proves it is discarded rather than published: an
    /// incomplete output is never a valid draft.
    /// </remarks>
    public static string IncompleteBody(string partialText) =>
        $$"""
          {
            "model": "{{KnownModel}}",
            "created_at": "2026-09-30T12:00:00.000Z",
            "message": {
              "role": "assistant",
              "content": {{System.Text.Json.JsonSerializer.Serialize(partialText)}}
            },
            "done": false,
            "total_duration": 123456789,
            "prompt_eval_count": 300,
            "eval_count": 41
          }
          """;

    /// <summary>An answer cut off by the output ceiling.</summary>
    /// <remarks>
    /// The partial text is present on purpose. A truncation is never a draft: the prefix is refused
    /// rather than presented as whole or as short.
    /// </remarks>
    public static string TruncatedBody(string partialText) =>
        $$"""
          {
            "model": "{{KnownModel}}",
            "created_at": "2026-09-30T12:00:00.000Z",
            "message": {
              "role": "assistant",
              "content": {{System.Text.Json.JsonSerializer.Serialize(partialText)}}
            },
            "done": true,
            "done_reason": "length",
            "total_duration": 123456789,
            "prompt_eval_count": 300,
            "eval_count": 4096
          }
          """;

    /// <summary>A finished generation carrying no text at all.</summary>
    public const string EmptyContentBody = """
        {
          "model": "ollama-test-wire-shape",
          "created_at": "2026-09-30T12:00:00.000Z",
          "message": {
            "role": "assistant",
            "content": ""
          },
          "done": true,
          "done_reason": "stop",
          "total_duration": 123456789,
          "prompt_eval_count": 300,
          "eval_count": 0
        }
        """;

    /// <summary>
    /// A finished generation stopped for a reason this transport never sends, carrying text on purpose.
    /// </summary>
    /// <remarks>
    /// Ollama documents <c>stop</c> and <c>length</c>. Anything else - including another provider's
    /// vocabulary - is not a finish this transport reads, so publishing its text would present an
    /// answer cut by an unnamed condition as if it were whole.
    /// </remarks>
    public static string UnexpectedDoneReasonBody(string text) =>
        $$"""
          {
            "model": "{{KnownModel}}",
            "created_at": "2026-09-30T12:00:00.000Z",
            "message": {
              "role": "assistant",
              "content": {{System.Text.Json.JsonSerializer.Serialize(text)}}
            },
            "done": true,
            "done_reason": "content_filter",
            "total_duration": 123456789,
            "prompt_eval_count": 300,
            "eval_count": 41
          }
          """;

    /// <summary>A 200 whose message is not an object this transport reads.</summary>
    public const string MalformedBody = """{"model":"ollama-test-wire-shape","message":"not-an-object","done":true}""";

    /// <summary>A 200 that is valid JSON but not an object.</summary>
    public const string NotAnObjectBody = """["chat"]""";

    /// <summary>A 200 naming no message at all.</summary>
    public const string NoMessageBody = """{"model":"ollama-test-wire-shape","done":true,"done_reason":"stop"}""";

    /// <summary>The documented error body shape, for every non-success status.</summary>
    /// <remarks>
    /// Used for 401, 403, 429, 500, 503 and a 3xx alike. An adapter that read a message out of an error
    /// body and put it in a result or a log would leak provider prose - which is where a customer's name
    /// and occasionally a key are most likely to be sitting in plain text - so the suite asserts
    /// against these bodies that nothing came back out.
    /// </remarks>
    public const string ErrorBody = """
        {
          "error": "unauthorized: a proxy in front of this Ollama refused the credential"
        }
        """;
}
