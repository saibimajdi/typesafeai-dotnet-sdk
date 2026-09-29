using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace TypeSafeAI.Serialization;

/// <summary>
/// Reads and writes the wire form of an <see cref="Answer"/>.
/// </summary>
/// <remarks>
/// <para>
/// An unrecognised <c>type</c> deserializes to <see cref="UnknownAnswer"/> instead of throwing.
/// <c>System.Text.Json</c>'s built-in polymorphic deserialization throws on an unknown
/// discriminator, which would make a response containing one newly introduced answer kind fail
/// entirely — including the answers the SDK does understand. This converter exists to prevent
/// exactly that.
/// </para>
/// <para>
/// Score <c>legend</c> and <c>probabilities</c> arrive with numeric string keys such as
/// <c>"0"</c> and <c>"1"</c>. They are projected to <see cref="int"/> here, because a level index
/// is what they mean, so callers never have to parse them.
/// </para>
/// </remarks>
public sealed class AnswerJsonConverter : JsonConverter<Answer>
{
    /// <inheritdoc />
    /// <exception cref="JsonException">The answer is not a JSON object.</exception>
    public override Answer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ReadElement(document.RootElement, idOverride: null);
    }

    /// <summary>
    /// Reads one answer from a parsed element.
    /// </summary>
    /// <param name="element">The answer object.</param>
    /// <param name="idOverride">
    /// The id to use, or <see langword="null"/> to take it from the body. Inside a response the id
    /// is the key of the answers map, because question ids are never sent to the model and so are
    /// never echoed in the body.
    /// </param>
    /// <returns>The parsed answer.</returns>
    /// <exception cref="JsonException">The element is not a JSON object.</exception>
    internal static Answer ReadElement(JsonElement element, string? idOverride)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"An answer must be a JSON object, but found {element.ValueKind}.");
        }

        var type = element.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String
            ? typeProperty.GetString()!
            : string.Empty;

        var id = idOverride
            ?? (element.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()!
                : string.Empty);

        return type switch
        {
            "noul" => new NoulAnswer(
                id,
                ReadRequiredDouble(element, "noul"),
                ReadAdditional(element, "noul")),

            "choice" => new ChoiceAnswer(
                id,
                ReadRequiredString(element, "choice"),
                ReadStringKeyedProbabilities(element, "probabilities"),
                ReadRequiredDouble(element, "confidence"),
                ReadAdditional(element, "choice", "probabilities", "confidence")),

            "score" => new ScoreAnswer(
                id,
                ReadRequiredDouble(element, "score"),
                ReadLegend(element),
                ReadLevelProbabilities(element),
                ReadRequiredDouble(element, "confidence"),
                ReadAdditional(element, "score", "legend", "probabilities", "confidence")),

            _ => new UnknownAnswer(
                id,
                type.Length == 0 ? "unknown" : type,
                element.Clone(),
                ReadAdditional(element)),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Answer value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteElement(writer, value);
    }

    /// <summary>
    /// Writes one answer in the wire format.
    /// </summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The answer to write.</param>
    /// <remarks>
    /// Internal rather than private so the result converter can serialize answers directly, with no
    /// reflection-based <c>JsonSerializer</c> overload in the path. That is what keeps the SDK
    /// clean under trimming and ahead-of-time compilation.
    /// </remarks>
    internal static void WriteElement(Utf8JsonWriter writer, Answer value)
    {
        writer.WriteStartObject();

        if (value.Id.Length > 0)
        {
            // Answers are keyed by id in a response body, so the id is not a wire field there.
            // Writing it here is what makes a single answer round-trip losslessly through a cache.
            writer.WriteString("id", value.Id);
        }

        writer.WriteString("type", value.Type);

        switch (value)
        {
            case NoulAnswer noul:
                writer.WriteNumber("noul", noul.Probability);
                break;

            case ChoiceAnswer choice:
                writer.WriteString("choice", choice.Label);
                WriteStringKeyedProbabilities(writer, choice.Probabilities);
                writer.WriteNumber("confidence", choice.Confidence);
                break;

            case ScoreAnswer score:
                writer.WriteNumber("score", score.Score);
                WriteLegend(writer, score.Legend);
                WriteLevelProbabilities(writer, score.Probabilities);
                writer.WriteNumber("confidence", score.Confidence);
                break;

            case UnknownAnswer unknown:
                foreach (var property in unknown.Raw.EnumerateObject())
                {
                    if (property.NameEquals("type") || property.NameEquals("id"))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }

                break;
        }

        WriteAdditional(writer, value);
        writer.WriteEndObject();
    }

    private static void WriteAdditional(Utf8JsonWriter writer, Answer value)
    {
        foreach (var (name, extra) in value.AdditionalProperties)
        {
            // An unknown answer's extras mirror its raw body, which WriteElement has already written.
            // Writing them again would duplicate every key, so the raw copy wins and only the rest
            // are added.
            if (value is UnknownAnswer written && written.Raw.TryGetProperty(name, out _))
            {
                continue;
            }

            if (extra is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WritePropertyName(name);
                extra.WriteTo(writer);
            }
        }
    }

    private static double ReadRequiredDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException($"An answer of this kind must carry a numeric '{name}' member.");
        }

        return property.GetDouble();
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"An answer of this kind must carry a string '{name}' member.");
        }

        return property.GetString()!;
    }

    private static Dictionary<string, double> ReadStringKeyedProbabilities(JsonElement element, string name)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);

        if (!element.TryGetProperty(name, out var probabilities) ||
            probabilities.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in probabilities.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                result[property.Name] = property.Value.GetDouble();
            }
        }

        return result;
    }

    private static Dictionary<int, double> ReadLevelProbabilities(JsonElement element)
    {
        var result = new Dictionary<int, double>();

        if (!element.TryGetProperty("probabilities", out var probabilities) ||
            probabilities.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in probabilities.EnumerateObject())
        {
            // Wire keys are JSON strings holding a level index. A key that is not an index is
            // skipped rather than thrown on, so an unexpected shape cannot fail the response.
            if (property.Value.ValueKind == JsonValueKind.Number &&
                int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            {
                result[level] = property.Value.GetDouble();
            }
        }

        return result;
    }

    private static Dictionary<int, JsonNode?> ReadLegend(JsonElement element)
    {
        var result = new Dictionary<int, JsonNode?>();

        if (!element.TryGetProperty("legend", out var legend) || legend.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in legend.EnumerateObject())
        {
            if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            {
                result[level] = property.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return result;
    }

    private static void WriteStringKeyedProbabilities(Utf8JsonWriter writer, IReadOnlyDictionary<string, double> probabilities)
    {
        writer.WriteStartObject("probabilities");

        foreach (var (label, probability) in probabilities)
        {
            writer.WriteNumber(label, probability);
        }

        writer.WriteEndObject();
    }

    private static void WriteLevelProbabilities(Utf8JsonWriter writer, IReadOnlyDictionary<int, double> probabilities)
    {
        writer.WriteStartObject("probabilities");

        foreach (var (level, probability) in probabilities)
        {
            writer.WriteNumber(level.ToString(CultureInfo.InvariantCulture), probability);
        }

        writer.WriteEndObject();
    }

    private static void WriteLegend(Utf8JsonWriter writer, IReadOnlyDictionary<int, JsonNode?> legend)
    {
        writer.WriteStartObject("legend");

        foreach (var (level, description) in legend)
        {
            var name = level.ToString(CultureInfo.InvariantCulture);

            if (description is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WritePropertyName(name);
                description.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static Dictionary<string, JsonNode?>? ReadAdditional(
        JsonElement element,
        params string[] known)
    {
        Dictionary<string, JsonNode?>? additional = null;

        foreach (var property in element.EnumerateObject())
        {
            var isKnown = property.NameEquals("type") || property.NameEquals("id");
            foreach (var name in known)
            {
                isKnown |= property.NameEquals(name);
            }

            if (isKnown)
            {
                continue;
            }

            additional ??= new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            additional[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return additional;
    }
}
