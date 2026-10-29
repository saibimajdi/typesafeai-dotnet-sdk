using System.Net;

// Parallelization is disabled through xunit.runner.json rather than
// [assembly: CollectionBehavior(DisableTestParallelization = true)], because xunit.v3 4.0.1 marks
// that property obsolete-as-error (CS0619, which cannot be suppressed) in favour of a
// ParallelizationAttribute that does not exist in the package yet. The suite mutates process-wide
// environment variables to exercise the documented TYPESAFE_* configuration behaviour, so parallel
// collections would interfere with each other; serialising a suite this small costs seconds.

namespace TypeSafeAI.Tests;

/// <summary>
/// Builds clients wired to a scripted transport.
/// </summary>
internal static class TestClient
{
    /// <summary>
    /// The API key used by tests, chosen to be obviously fake.
    /// </summary>
    public const string ApiKey = "tsk_test_key_not_real";

    /// <summary>
    /// Creates a client whose transport is a stub.
    /// </summary>
    /// <param name="responder">Returns the response for a one-based attempt number.</param>
    /// <param name="options">Options to use, or <see langword="null"/> for test defaults.</param>
    /// <returns>The client and the stub, so assertions can inspect what was sent.</returns>
    public static (TypeSafeClient Client, StubHttpMessageHandler Handler) Create(
        Func<int, CapturedRequest, HttpResponseMessage> responder,
        TypeSafeClientOptions? options = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        return (Create(handler, options), handler);
    }

    /// <summary>
    /// Creates a client whose transport always returns the same body.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="statusCode">The status code to return.</param>
    /// <param name="options">Options to use, or <see langword="null"/> for test defaults.</param>
    /// <param name="requestId">The value of the request-id response header, or <see langword="null"/>.</param>
    /// <returns>The client and the stub.</returns>
    public static (TypeSafeClient Client, StubHttpMessageHandler Handler) Returning(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        TypeSafeClientOptions? options = null,
        string? requestId = "req_01a0ab8265a27733a1bc672bfe98d906") =>
        Create((_, _) => StubHttpMessageHandler.Json(json, statusCode, requestId), options);

    /// <summary>
    /// Creates a client over an existing stub handler.
    /// </summary>
    /// <param name="handler">The stub handler.</param>
    /// <param name="options">Options to use, or <see langword="null"/> for test defaults.</param>
    /// <returns>The client.</returns>
    public static TypeSafeClient Create(
        StubHttpMessageHandler handler,
        TypeSafeClientOptions? options = null)
    {
        options ??= new TypeSafeClientOptions();
        options.ApiKey ??= ApiKey;

        // A supplied HttpClient is never disposed by the SDK, so it is safe to hand ownership of
        // this one to the test without a using block.
        return new TypeSafeClient(options, new HttpClient(handler, disposeHandler: false));
    }

    /// <summary>
    /// The three-question request used across the behavioural tests.
    /// </summary>
    /// <returns>The questions.</returns>
    public static Question[] TriageQuestions() =>
    [
        new NoulQuestion("is_urgent", "Does this convey urgency?"),
        new ChoiceQuestion("department", "Which team should handle this?", ["billing", "technical", "sales"]),
        new ScoreQuestion("frustration", "How frustrated is the customer?", ["Calm", "Frustrated", "Very angry"]),
    ];

    /// <summary>
    /// A mixed response answering <see cref="TriageQuestions"/>.
    /// </summary>
    public const string TriageResponse = """
        {
          "model": "jev-1.14.0",
          "answers": {
            "is_urgent": { "type": "noul", "noul": 0.92 },
            "department": {
              "type": "choice",
              "choice": "technical",
              "probabilities": { "billing": 0.08, "technical": 0.85, "sales": 0.07 },
              "confidence": 0.82
            },
            "frustration": {
              "type": "score",
              "score": 1.6,
              "legend": { "0": "Calm", "1": "Frustrated", "2": "Very angry" },
              "probabilities": { "0": 0.05, "1": 0.3, "2": 0.65 },
              "confidence": 0.78
            }
          },
          "usage": { "input_tokens": 312, "output_tokens": 48 }
        }
        """;
}

/// <summary>
/// Runs an action with an environment variable set, restoring the previous value afterwards.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

    /// <summary>
    /// Sets an environment variable for the lifetime of the scope.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value, or <see langword="null"/> to remove the variable.</param>
    /// <returns>This scope.</returns>
    public EnvironmentScope Set(string name, string? value)
    {
        if (!_original.ContainsKey(name))
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _original.Clear();
    }
}
