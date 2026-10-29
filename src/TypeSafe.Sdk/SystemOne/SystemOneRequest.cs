using System.Text.Json.Nodes;

namespace TypeSafeAI;

/// <summary>
/// A complete System One request: one state, and the questions to ask about it.
/// </summary>
/// <remarks>
/// <para>
/// Prefer this overload when you need per-call control over the model, retry policy, timeout,
/// headers, or request-body fields the SDK does not model. The state-and-questions overloads of
/// <see cref="TypeSafeClient.SystemOneAsync(SystemOneRequest, CancellationToken)"/> cover the
/// common case.
/// </para>
/// <para>
/// All questions in one request see the same state and are evaluated independently and in
/// parallel. Batching every question that shares a state into a single request is dramatically
/// cheaper and faster than one request per question, so it should be the default shape of a call.
/// </para>
/// </remarks>
public sealed class SystemOneRequest
{
    /// <summary>
    /// Gets the content to evaluate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A JSON string for plain text, or a JSON object or array for structured data such as a
    /// conversation, a record, or the current state of the application. Values nested inside an
    /// object or array may be <see langword="null"/>; the state itself may not.
    /// </para>
    /// <para>
    /// A <see cref="string"/> converts implicitly, so <c>State = "My card was charged twice."</c>
    /// compiles directly.
    /// </para>
    /// </remarks>
    public required JsonNode? State { get; init; }

    /// <summary>
    /// Gets the questions to ask, keyed internally by each question's
    /// <see cref="Question.Id"/>.
    /// </summary>
    /// <remarks>
    /// Must contain at least one question, and no two questions may share an id.
    /// </remarks>
    public required IEnumerable<Question> Questions { get; init; }

    /// <summary>
    /// Gets the images to send with this request, keyed by the reference string that stands for
    /// each one, or <see langword="null"/> to send none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Write a reference wherever its image belongs, as a whole string value or inside a longer
    /// string in the state, a question's instructions, or its criteria, and the model receives the
    /// image in its place. Every asset must be referenced at least once or the API returns
    /// <c>400</c>, and object keys are never replaced. A new <see cref="Guid"/> per image is the
    /// safest reference.
    /// </para>
    /// <para>
    /// Images are an experimental API feature. The SDK sends the
    /// <see cref="TypeSafeDefaults.ImagesHeader"/> header automatically when this map is non-empty.
    /// </para>
    /// <code>
    /// var door = Guid.NewGuid().ToString();
    /// var request = new SystemOneRequest
    /// {
    ///     State = new JsonObject { ["door"] = door },
    ///     Assets = new Dictionary&lt;string, ImageAsset&gt;
    ///     {
    ///         [door] = await ImageAsset.FromFileAsync("door-camera.jpg"),
    ///     },
    ///     Questions = [new NoulQuestion("person_present", "Is there a person in `door`?")],
    /// };
    /// </code>
    /// </remarks>
    public IReadOnlyDictionary<string, ImageAsset>? Assets { get; init; }

    /// <summary>
    /// Gets the model to use for this request, or <see langword="null"/> to use the client default.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets per-call overrides for retry, timeout, headers, and unmodelled body fields.
    /// </summary>
    public TypeSafeRequestOptions? Options { get; init; }

    /// <summary>
    /// Gets extra top-level request-body fields, for API features this SDK version does not model.
    /// </summary>
    /// <remarks>
    /// Merged into the body after <c>state</c>, <c>model</c>, and <c>questions</c>, so these win on
    /// a name collision. Merging is shallow: an object value replaces the existing value rather
    /// than being merged into it.
    /// </remarks>
    public IReadOnlyDictionary<string, JsonNode?>? AdditionalProperties { get; init; }
}
