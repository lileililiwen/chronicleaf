namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The Gemini generateContent wire shapes, pinned from the provider's own reference on 2026-09-30
/// (LLM-GEMINI-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>What these are.</b> The documented shape of <c>POST /v1beta/models/{model}:generateContent</c>
/// and the response object it returns, transcribed as fixtures so a scenario can drive every branch
/// of the mapping - completed, refused, safety-blocked, truncated, malformed, throttled, rejected -
/// without a network and without inventing a shape no provider has published.
/// </para>
/// <para>
/// <b>What they are not.</b> Evidence that Gemini works. A fixture proves that this adapter reads the
/// documented shape correctly; it cannot prove that a live account, a live key or a live model behaves
/// the same way, and no scenario in this suite may be reported as if it did. The real provider's
/// current field set is the reference's, not this file's - which is exactly why the adapter reads
/// members defensively and treats anything it does not recognise as unusable rather than as text.
/// </para>
/// <para>
/// Every body here is written with the same member names the reference documents -
/// <c>candidates</c>, <c>content.parts[].text</c>, <c>finishReason</c>,
/// <c>promptFeedback.blockReason</c>, <c>usageMetadata.promptTokenCount</c> - so a change to the
/// adapter that broke the real dialect would break these fixtures rather than passing them.
/// </para>
/// </remarks>
internal static class GeminiFixtures
{
    /// <summary>
    /// A model this deployment's allowlist names. A fixture, not a claim: the contract deliberately
    /// keeps no model list, and the allowlist is deployment configuration, so a scenario supplies one
    /// and the product still never chooses a model on an owner's behalf.
    /// </summary>
    public const string KnownModel = "gemini-test-wire-shape";

    /// <summary>A model this deployment's allowlist does not name.</summary>
    public const string UnknownModel = "gemini-not-in-the-allowlist";

    /// <summary>The secret sealed under the credential a scenario stores. Never leaves this file.</summary>
    public const string StoredSecret = "AIza-chronicleaf-gemini-adapter-sentinel-9d3f7a1b5e8c";

    /// <summary>The exact origin and path the adapter is allowed to reach, for a model.</summary>
    public static string GenerateContentUri(string model) =>
        $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

    /// <summary>
    /// A completed response: one candidate carrying one text part.
    /// </summary>
    /// <param name="text">The generated text, as the provider would return it.</param>
    /// <param name="inputTokens">Prompt tokens, or null to omit the block entirely.</param>
    /// <param name="outputTokens">Candidate tokens, or null to omit them.</param>
    public static string CompletedBody(
        string text,
        int? inputTokens = 812,
        int? outputTokens = 96)
    {
        var usage = inputTokens is null && outputTokens is null
            ? string.Empty
            : $$"""
                  ,
                "usageMetadata": {
                  "promptTokenCount": {{(inputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "candidatesTokenCount": {{(outputTokens ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "totalTokenCount": {{((inputTokens ?? 0) + (outputTokens ?? 0)).ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                }
                """;

        return $$"""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          {
                            "text": {{System.Text.Json.JsonSerializer.Serialize(text)}}
                          }
                        ],
                        "role": "model"
                      },
                      "finishReason": "STOP",
                      "index": 0
                    }
                  ],
                  "modelVersion": "{{KnownModel}}"
                  {{usage}}
                }
                """;
    }

    /// <summary>A completed response whose text is split across two candidates.</summary>
    /// <remarks>
    /// The provider may return more than one candidate and may split an answer across parts, so an
    /// adapter that read only the first string would silently truncate an answer the provider split.
    /// This fixture is what makes that a failing assertion rather than a reviewer's hope.
    /// </remarks>
    public static string SplitCandidatesBody(string first, string second) =>
        $$"""
          {
            "candidates": [
              {
                "content": {
                  "parts": [ { "text": {{System.Text.Json.JsonSerializer.Serialize(first)}} } ],
                  "role": "model"
                },
                "finishReason": "STOP",
                "index": 0
              },
              {
                "content": {
                  "parts": [ { "text": {{System.Text.Json.JsonSerializer.Serialize(second)}} } ],
                  "role": "model"
                },
                "finishReason": "STOP",
                "index": 1
              }
            ],
            "usageMetadata": { "promptTokenCount": 40, "candidatesTokenCount": 12 }
          }
          """;

    /// <summary>A prompt the provider's own safety policy blocked before generating.</summary>
    /// <remarks>
    /// No candidates at all, and the reason sits in <c>promptFeedback</c> rather than on a candidate.
    /// An adapter that treated "no candidates" as "empty draft" would turn a safety block into
    /// completed text; this fixture proves it is a refusal instead.
    /// </remarks>
    public const string SafetyBlockedBody = """
        {
          "promptFeedback": {
            "blockReason": "SAFETY",
            "safetyRatings": [
              { "category": "HARM_CATEGORY_DANGEROUS_CONTENT", "probability": "HIGH", "blocked": true }
            ]
          }
        }
        """;

    /// <summary>A candidate the provider's own safety policy stopped mid-answer.</summary>
    /// <remarks>
    /// The partial text is present on purpose. A safety stop is the provider declining, and the
    /// contract keeps no text on a refusal - so this fixture proves the partial text is discarded
    /// rather than published as a draft.
    /// </remarks>
    public static string CandidateSafetyBody(string partialText) =>
        $$"""
          {
            "candidates": [
              {
                "content": {
                  "parts": [ { "text": {{System.Text.Json.JsonSerializer.Serialize(partialText)}} } ],
                  "role": "model"
                },
                "finishReason": "SAFETY",
                "index": 0
              }
            ],
            "usageMetadata": { "promptTokenCount": 300, "candidatesTokenCount": 41 }
          }
          """;

    /// <summary>A candidate stopped because it recited, which is the provider declining on policy grounds.</summary>
    public const string RecitationBody = """
        {
          "candidates": [
            {
              "content": {
                "parts": [],
                "role": "model"
              },
              "finishReason": "RECITATION",
              "index": 0
            }
          ]
        }
        """;

    /// <summary>An answer cut off by the output ceiling.</summary>
    public static string TruncatedBody(string partialText) =>
        $$"""
          {
            "candidates": [
              {
                "content": {
                  "parts": [ { "text": {{System.Text.Json.JsonSerializer.Serialize(partialText)}} } ],
                  "role": "model"
                },
                "finishReason": "MAX_TOKENS",
                "index": 0
              }
            ],
            "usageMetadata": { "promptTokenCount": 300, "candidatesTokenCount": 4096 }
          }
          """;

    /// <summary>A response whose candidate asks for a function call, in a contract that has no tools.</summary>
    public const string FunctionCallBody = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  { "functionCall": { "name": "publish_draft", "args": {} } }
                ],
                "role": "model"
              },
              "finishReason": "STOP",
              "index": 0
            }
          ]
        }
        """;

    /// <summary>A stopped candidate carrying no text at all.</summary>
    public const string EmptyPartsBody = """
        {
          "candidates": [
            {
              "content": {
                "parts": [],
                "role": "model"
              },
              "finishReason": "STOP",
              "index": 0
            }
          ]
        }
        """;

    /// <summary>A completed response with no usage block, so usage must be reported unknown.</summary>
    public const string NoUsageBody = """
        {
          "candidates": [
            {
              "content": {
                "parts": [ { "text": "{\"title\":\"t\"}" } ],
                "role": "model"
              },
              "finishReason": "STOP",
              "index": 0
            }
          ],
          "modelVersion": "gemini-test-wire-shape"
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
            "code": 401,
            "message": "API key not valid. Please pass a valid API key. AI_KEY_INVALID. See https://ai.google.dev/gemini-api/docs/api-key for details.",
            "status": "UNAUTHENTICATED"
          }
        }
        """;

    /// <summary>A 200 whose body is not JSON, so it is an answer and not a draft.</summary>
    public const string MalformedBody = "{ this is not json";

    /// <summary>A 200 whose body is JSON but not the object this transport reads.</summary>
    public const string NotAnObjectBody = "[\"unexpected\",\"shape\"]";
}
