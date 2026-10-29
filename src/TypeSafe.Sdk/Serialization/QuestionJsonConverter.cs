using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace TypeSafeAI.Serialization;

/// <summary>
/// Writes the wire form of a <see cref="Question"/>, and reads it back.
/// </summary>
/// <remarks>
/// <para>
/// One converter handles every question kind, because the wire format discriminates on a
/// <c>type</c> member rather than on the shape of the object. Writing is deterministic: fields
/// appear in a fixed order, criteria keep the order they were supplied in, and no field is ever
/// written as <see langword="null"/> unless the caller supplied it. A request body can therefore
/// be hashed, cached, and replayed.
/// </para>
/// <para>
/// Reading an unrecognised <c>type</c> produces a <see cref="RawQuestion"/> rather than throwing,
/// so JSON produced by a newer SDK or by hand still round-trips.
/// </para>
/// </remarks>
public sealed class QuestionJsonConverter : JsonConverter<Question>
{
    /// <inheritdoc />
    /// <exception cref="JsonException">The question is not a JSON object, or its <c>type</c> is missing.</exception>
    public override Question Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"A question must be a JSON object, but found {element.ValueKind}.");
        }

        if (!element.TryGetProperty("type", out var typeProperty) ||
            typeProperty.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("A question must carry a string 'type' member.");
        }

        var type = typeProperty.GetString()!;

        // Standalone round-trips carry the id so an answer or question can be cached on its own.
        // On the wire the id is the key of the questions map and is never in the body.
        var id = element.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
            ? idProperty.GetString()!
            : string.Empty;

        var instructions = element.TryGetProperty("instructions", out var instructionsElement)
            ? JsonNode.Parse(instructionsElement.GetRawText())
            : null;

        switch (type)
        {
            case "noul":
                return new NoulQuestion(id, instructions, ReadNoulCriteria(element), ReadAdditional(element, "criteria"));

            case "choice":
                return new ChoiceQuestion(id, instructions, ReadChoiceCriteria(element), ReadAdditional(element, "criteria"));

            case "score":
                return new ScoreQuestion(id, instructions, ReadScoreCriteria(element), ReadAdditional(element, "criteria"));

            case "box":
                return new BoxQuestion(id, instructions, ReadAdditional(element, criteriaName: null));

            default:
                return new RawQuestion(id, type, ReadRawBody(element));
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Question value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        QuestionWriter.Write(writer, value, includeId: true);
    }

    private static NoulCriteria? ReadNoulCriteria(JsonElement element)
    {
        if (!element.TryGetProperty("criteria", out var criteria) || criteria.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var yes = criteria.TryGetProperty("true", out var trueElement)
            ? JsonNode.Parse(trueElement.GetRawText())
            : null;
        var no = criteria.TryGetProperty("false", out var falseElement)
            ? JsonNode.Parse(falseElement.GetRawText())
            : null;

        return yes is null && no is null ? null : new NoulCriteria(yes, no);
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> ReadChoiceCriteria(JsonElement element)
    {
        if (!element.TryGetProperty("criteria", out var criteria) || criteria.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A choice question must carry an object 'criteria' member.");
        }

        foreach (var property in criteria.EnumerateObject())
        {
            yield return new KeyValuePair<string, JsonNode?>(
                property.Name,
                property.Value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(property.Value.GetRawText()));
        }
    }

    private static IEnumerable<JsonNode?> ReadScoreCriteria(JsonElement element)
    {
        if (!element.TryGetProperty("criteria", out var criteria) || criteria.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("A score question must carry an array 'criteria' member.");
        }

        foreach (var level in criteria.EnumerateArray())
        {
            yield return level.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(level.GetRawText());
        }
    }

    private static JsonObject ReadRawBody(JsonElement element)
    {
        var body = new JsonObject();

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("type") || property.NameEquals("id"))
            {
                continue;
            }

            body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return body;
    }

    private static Dictionary<string, JsonNode?>? ReadAdditional(JsonElement element, string? criteriaName)
    {
        Dictionary<string, JsonNode?>? additional = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("type") ||
                property.NameEquals("id") ||
                property.NameEquals("instructions") ||
                (criteriaName is not null && property.NameEquals(criteriaName)))
            {
                continue;
            }

            additional ??= new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            additional[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return additional;
    }
}
