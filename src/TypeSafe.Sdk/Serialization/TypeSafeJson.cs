using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeSafeAI.Internal;

namespace TypeSafeAI.Serialization;

/// <summary>
/// Serialization helpers for the SDK's model types, and the options the SDK itself uses.
/// </summary>
/// <remarks>
/// <para>
/// The helpers exist so that a result or a single answer can be cached and read back without
/// reflection. <c>JsonSerializer.Serialize(result, TypeSafeJson.Options)</c> also works — the types
/// carry their own converters — but those overloads are annotated as requiring unreferenced code,
/// so they warn under trimming and ahead-of-time compilation. The methods here do not.
/// </para>
/// <para>
/// Question and answer kinds are discriminated by a <c>type</c> member and handled by dedicated
/// converters rather than by <c>System.Text.Json</c>'s built-in polymorphism, which throws on an
/// unrecognised discriminator.
/// </para>
/// </remarks>
public static class TypeSafeJson
{
    /// <summary>
    /// Gets the serializer options the SDK uses, including the question and answer converters.
    /// </summary>
    /// <remarks>
    /// The instance is cached and read-only. Pass it to
    /// <see cref="JsonSerializer"/> when serializing the SDK's types alongside your own.
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Serializes a result to JSON.
    /// </summary>
    /// <param name="result">The result to serialize.</param>
    /// <returns>The JSON representation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <see cref="SystemOneResult.RequestId"/> is not part of the response body and is therefore not
    /// included. Every answer keeps its id, so a cached result can be read back whole.
    /// </remarks>
    public static string Serialize(SystemOneResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Serialize(writer => result.WriteTo(writer));
    }

    /// <summary>
    /// Serializes a single answer to JSON.
    /// </summary>
    /// <param name="answer">The answer to serialize.</param>
    /// <returns>The JSON representation, including the answer's id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="answer"/> is <see langword="null"/>.</exception>
    public static string Serialize(Answer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return Serialize(writer => AnswerJsonConverter.WriteElement(writer, answer));
    }

    /// <summary>
    /// Serializes a single question to JSON.
    /// </summary>
    /// <param name="question">The question to serialize.</param>
    /// <returns>The JSON representation, including the question's id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="question"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Serialization is deterministic, so the same question always produces the same JSON. That
    /// makes the output safe to hash, cache, and replay.
    /// </remarks>
    public static string Serialize(Question question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return Serialize(writer => QuestionWriter.Write(writer, question, includeId: true));
    }

    /// <summary>
    /// Serializes a request to the JSON body the client would send for it.
    /// </summary>
    /// <param name="request">The request to serialize.</param>
    /// <returns>The request body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The body is one the client would reject: the state is <see langword="null"/>, there are no
    /// questions, two questions share an id, or the additional properties carry the removed
    /// <c>document</c> field. Per-call options that are not part of the body, such as the timeout,
    /// are not validated here.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The body comes from the same writer that
    /// <see cref="TypeSafeClient.SystemOneAsync(SystemOneRequest, CancellationToken)"/> uses, so it
    /// is byte for byte what goes on the wire. Use it to measure a request before sending it, with
    /// <see cref="Encoding.UTF8"/>'s <see cref="Encoding.GetByteCount(string)"/>. The writer escapes
    /// each non-ASCII character to a six-character sequence, so counting the characters of the state,
    /// or serializing it with <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>,
    /// undercounts what the SDK sends.
    /// </para>
    /// <para>
    /// <see cref="SystemOneRequest.Questions"/> is enumerated once here and once more when the request
    /// is sent, as it would be for two sends. A sequence that can only be enumerated once has nothing
    /// left for the send.
    /// </para>
    /// <para>
    /// There is no client here to supply a default model. When neither
    /// <see cref="SystemOneRequest.Model"/> nor the <see cref="TypeSafeRequestOptions.Model"/> of its
    /// <see cref="SystemOneRequest.Options"/> is set, the model written is
    /// <see cref="TypeSafeDefaults.DefaultModel"/> rather than the one a particular client is
    /// configured with (an additional property named <c>model</c> still replaces it, as when sending).
    /// The size then differs by the difference between the two model names' lengths; set the model on
    /// the request when the exact bytes matter.
    /// </para>
    /// </remarks>
    public static string Serialize(SystemOneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Same precedence as the client: the request's own values win over its per-call options,
        // and additional properties replace the options' set rather than merging with it.
        var model = request.Model ?? request.Options?.Model ?? TypeSafeDefaults.DefaultModel;
        var additional = request.AdditionalProperties ?? request.Options?.AdditionalBodyProperties;

        return Encoding.UTF8.GetString(
            TypeSafeRequestWriter.Write(request.State, model, request.Questions, additional));
    }

    /// <summary>
    /// Reads a result previously written by <see cref="Serialize(SystemOneResult)"/>.
    /// </summary>
    /// <param name="json">The JSON to read.</param>
    /// <returns>The parsed result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is not a valid result.</exception>
    public static SystemOneResult DeserializeResult(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        return SystemOneResultJsonConverter.ReadElement(document.RootElement, requestId: null);
    }

    /// <summary>
    /// Reads an answer previously written by <see cref="Serialize(Answer)"/>.
    /// </summary>
    /// <param name="json">The JSON to read.</param>
    /// <returns>The parsed answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is not a valid answer.</exception>
    public static Answer DeserializeAnswer(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        return AnswerJsonConverter.ReadElement(document.RootElement, idOverride: null);
    }

    /// <summary>
    /// Reads a question previously written by <see cref="Serialize(Question)"/>.
    /// </summary>
    /// <param name="json">The JSON to read.</param>
    /// <returns>The parsed question.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON is not a valid question.</exception>
    public static Question DeserializeQuestion(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();

        return new QuestionJsonConverter().Read(ref reader, typeof(Question), Options);
    }

    private static string Serialize(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        options.Converters.Add(new QuestionJsonConverter());
        options.Converters.Add(new AnswerJsonConverter());
        options.Converters.Add(new SystemOneResultJsonConverter());

        // The instance is deliberately NOT frozen with MakeReadOnly, and no TypeInfoResolver is
        // assigned. Freezing requires a resolver, and the only general-purpose one,
        // DefaultJsonTypeInfoResolver, is annotated as requiring unreferenced and dynamic code,
        // which would make this trimming- and AOT-compatible package fail its own analyzers.
        //
        // Nothing is lost: every type the SDK serializes carries one of the converters above, so no
        // resolver is ever consulted on the SDK's own code paths, and System.Text.Json freezes the
        // instance itself the first time a caller hands it to JsonSerializer. The SDK treats this
        // instance as immutable from the moment it is created.

        return options;
    }
}
