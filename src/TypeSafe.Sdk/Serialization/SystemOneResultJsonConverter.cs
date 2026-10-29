using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace TypeSafeAI.Serialization;

/// <summary>
/// Reads and writes the wire form of a <see cref="SystemOneResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Answers are keyed by question id, which is the only place the id appears in a response body —
/// ids are never sent to the model, so they are not echoed inside each answer. The converter
/// assigns each answer the key it arrived under.
/// </para>
/// <para>
/// Unknown top-level fields, an unknown usage shape, and answers of an unrecognised kind are all
/// tolerated rather than fatal, so a response from a newer API version still produces a usable
/// result.
/// </para>
/// </remarks>
public sealed class SystemOneResultJsonConverter : JsonConverter<SystemOneResult>
{
    /// <inheritdoc />
    /// <exception cref="JsonException">The response body is not a JSON object.</exception>
    public override SystemOneResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ReadElement(document.RootElement, requestId: null);
    }

    /// <summary>
    /// Reads a result from a parsed element.
    /// </summary>
    /// <param name="element">The response object.</param>
    /// <param name="requestId">The request id taken from the response headers, when available.</param>
    /// <returns>The parsed result.</returns>
    /// <exception cref="JsonException">The element is not a JSON object.</exception>
    internal static SystemOneResult ReadElement(JsonElement element, string? requestId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"A System One response must be a JSON object, but found {element.ValueKind}.");
        }

        var model = element.TryGetProperty("model", out var modelProperty) && modelProperty.ValueKind == JsonValueKind.String
            ? modelProperty.GetString()!
            : string.Empty;

        var usage = ReadUsage(element);

        var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);

        if (element.TryGetProperty("answers", out var answersProperty) &&
            answersProperty.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in answersProperty.EnumerateObject())
            {
                answers[entry.Name] = AnswerJsonConverter.ReadElement(entry.Value, entry.Name);
            }
        }

        return new SystemOneResult(
            model,
            usage,
            answers,
            requestId,
            rawJson: element.Clone());
    }

    /// <inheritdoc />
    /// <remarks>
    /// The model, answers and token counts are written from the typed properties, so edits to an
    /// answer's <see cref="Answer.AdditionalProperties"/> are kept. Every other field of
    /// <see cref="SystemOneResult.RawJson"/>, at the top level and inside <c>usage</c>, is copied
    /// verbatim, as is a token count that was read as <see langword="null"/> because it did not fit
    /// an <see cref="int"/>.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, SystemOneResult value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);

        var raw = value.RawJson;

        writer.WriteStartObject();
        writer.WriteString("model", value.Model);

        writer.WriteStartObject("answers");
        foreach (var (id, answer) in value.Answers)
        {
            writer.WritePropertyName(id);
            AnswerJsonConverter.WriteElement(writer, answer);
        }

        writer.WriteEndObject();

        if (value.Usage is { } usage)
        {
            WriteUsage(writer, usage, raw);
        }

        // A usage value that did not read as an object is unmodelled, so it is copied with the rest.
        WriteUnmodelled(writer, raw, name =>
            name is "model" or "answers" || (name is "usage" && value.Usage is not null));

        writer.WriteEndObject();
    }

    private static void WriteUnmodelled(Utf8JsonWriter writer, JsonElement raw, Func<string, bool> written)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in raw.EnumerateObject())
        {
            if (!written(property.Name))
            {
                property.WriteTo(writer);
            }
        }
    }

    private static Usage? ReadUsage(JsonElement element)
    {
        if (!element.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new Usage(
            ReadNullableInt(usage, "input_tokens"),
            ReadNullableInt(usage, "output_tokens"));
    }

    // A count that does not fit an int, fractional or out of range, reads as unreported rather than
    // failing the response; the original value stays available in RawJson.
    private static int? ReadNullableInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;

    private static void WriteUsage(Utf8JsonWriter writer, Usage usage, JsonElement raw)
    {
        writer.WriteStartObject("usage");

        if (usage.InputTokens is { } input)
        {
            writer.WriteNumber("input_tokens", input);
        }

        if (usage.OutputTokens is { } output)
        {
            writer.WriteNumber("output_tokens", output);
        }

        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("usage", out var rawUsage))
        {
            WriteUnmodelled(writer, rawUsage, name =>
                (name is "input_tokens" && usage.InputTokens is not null) ||
                (name is "output_tokens" && usage.OutputTokens is not null));
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads a models response body.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="requestId">The request id from the response headers.</param>
    /// <returns>The parsed models.</returns>
    /// <remarks>
    /// The models payload is tolerated in either a bare array or an object with a <c>models</c> or
    /// <c>data</c> member, because the endpoint is not covered by the published HTTP reference and
    /// the SDK should not be the reason a listing fails to parse.
    /// </remarks>
    internal static ModelsResult ReadModels(JsonElement json, string? requestId)
    {
        var models = new List<ModelMetadata>();

        var array = json.ValueKind switch
        {
            JsonValueKind.Array => json,
            JsonValueKind.Object when json.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array => m,
            JsonValueKind.Object when json.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array => d,
            _ => default,
        };

        if (array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                models.Add(new ModelMetadata(
                    ReadString(item, "name") ?? string.Empty,
                    ReadString(item, "description") ?? string.Empty,
                    ReadString(item, "release_date") ?? string.Empty));
            }
        }

        return new ModelsResult(models, requestId, json.Clone());
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
