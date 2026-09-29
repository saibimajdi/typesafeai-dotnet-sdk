using System.Text.Json.Nodes;

using System.Text.Json;

namespace TypeSafeAI.Tests;

/// <summary>
/// Verifies response parsing, including the forward-compatibility guarantees.
/// </summary>
public sealed class ResponseParsingTests
{
    [Fact]
    public async Task NoulAnswerExposesTheProbabilityAndNoConfidence()
    {
        var (client, _) = TestClient.Returning(Fixtures.NoulResponse);

        var result = await client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "question?")]);
        var answer = result.Noul("is_urgent");

        Assert.Equal(0.92, answer.Probability);

        // Noul answers carry no confidence. A Try-shaped accessor keeps an absent confidence from
        // being mistaken for a real value of zero.
        Assert.False(answer.TryGetConfidence(out var confidence));
        Assert.Equal(0, confidence);
    }

    [Fact]
    public async Task ChoiceAnswerExposesLabelConfidenceAndDistribution()
    {
        var (client, _) = TestClient.Returning(Fixtures.ChoiceResponse);

        var result = await client.SystemOneAsync("text", [new ChoiceQuestion("department", "q?", ["billing", "technical", "sales"])]);
        var answer = result.Choice("department");

        Assert.Equal("technical", answer.Label);
        Assert.Equal(0.82, answer.Confidence);
        Assert.Equal(3, answer.Probabilities.Count);
        Assert.Equal("technical", answer.TopLabel);
        Assert.Equal(0.85, answer.TopProbability);
        Assert.True(answer.TryGetConfidence(out var reported));
        Assert.Equal(0.82, reported);
    }

    [Fact]
    public async Task ChoiceAnswersCanBeRanked()
    {
        var (client, _) = TestClient.Returning(Fixtures.ChoiceResponse);

        var result = await client.SystemOneAsync("text", [new ChoiceQuestion("department", "q?", ["billing", "technical", "sales"])]);
        var ranked = result.Choice("department").Ranked().ToList();

        Assert.Equal(["technical", "billing", "sales"], ranked.Select(static pair => pair.Key));
        Assert.Equal(["technical", "billing"], result.Choice("department").Top(2).Select(static pair => pair.Key));
    }

    [Fact]
    public async Task AnAbsentLabelIsDistinctFromAProbabilityOfZero()
    {
        var (client, _) = TestClient.Returning(Fixtures.ChoiceResponse);

        var result = await client.SystemOneAsync("text", [new ChoiceQuestion("department", "q?", ["billing", "technical", "sales"])]);
        var answer = result.Choice("department");

        // The API does not promise that every requested label is reported. Coercing an absent label
        // to 0.0 would fabricate a result the model never gave.
        Assert.Null(answer.ProbabilityOrDefault("refunds"));
        Assert.False(answer.TryGetProbability("refunds", out _));
        Assert.Equal(0.08, answer.ProbabilityOrDefault("billing"));
    }

    [Fact]
    public async Task ScoreAnswerProjectsStringLevelKeysToIntegers()
    {
        var (client, _) = TestClient.Returning(Fixtures.ScoreResponse);

        var result = await client.SystemOneAsync("text", [new ScoreQuestion("frustration", "q?", ["Calm", "Frustrated", "Very angry"])]);
        var answer = result.Score("frustration");

        // On the wire the keys are JSON strings; a level index is what they mean.
        Assert.Equal(1.6, answer.Score);
        Assert.Equal(0.78, answer.Confidence);
        Assert.Equal(3, answer.LevelCount);
        Assert.Equal(2, answer.MaxLevel);
        Assert.Equal(0.65, answer.ProbabilityAtLevel(2));
        Assert.Equal("Very angry", answer.LegendTextAtLevel(2));
        Assert.Equal(1.6, answer.ExpectedLevel, precision: 10);
    }

    [Fact]
    public async Task ScoreAnswerNormalisesAndMeasuresSpread()
    {
        var (client, _) = TestClient.Returning(Fixtures.ThreeScoreResponse);

        var result = await client.SystemOneAsync(
            "text",
            [
                new ScoreQuestion("severity", "q?", ["low", "medium", "high"]),
                new ScoreQuestion("frustration", "q?", ["calm", "frustrated", "very angry"]),
                new ScoreQuestion("reproducibility", "q?", ["none", "vague", "partial", "exact"]),
            ]);

        // Normalising by the answer's own rubric is what lets rubrics of different lengths be
        // combined without looking back at the request.
        Assert.Equal(1.0, result.Score("reproducibility").NormalizedScore, precision: 10);
        Assert.Equal(1.24 / 2, result.Score("severity").NormalizedScore, precision: 10);

        // A one-hot distribution has no spread; a split one does.
        Assert.Equal(0, result.Score("reproducibility").Variance, precision: 10);
        Assert.True(result.Score("frustration").Variance > 0.2);
    }

    [Fact]
    public async Task StructuredLegendValuesSurviveIntact()
    {
        var (client, _) = TestClient.Returning(Fixtures.StructuredLegendResponse);

        var result = await client.SystemOneAsync("text", [new ScoreQuestion("amount_due", "q?", ["a", "b", "c"])]);
        var answer = result.Score("amount_due");

        // The server echoes the request's criteria entries verbatim, so a legend value is the same
        // free-form content that was sent, not necessarily a string.
        var first = answer.LegendAtLevel(0);
        Assert.NotNull(first);
        Assert.Equal("small", first!["label"]!.GetValue<string>());
        Assert.Equal(1000, first["max_usd"]!.GetValue<int>());

        var third = answer.LegendAtLevel(2);
        Assert.NotNull(third);
        Assert.Equal(JsonValueKind.Array, third!.GetValueKind());
        Assert.Null(answer.LegendTextAtLevel(0));
        Assert.Null(answer.LegendTextAtLevel(99));
    }

    [Fact]
    public async Task UnknownAnswerKindsDoNotFailTheResponse()
    {
        const string Json = """
            {
              "model": "jev-latest",
              "answers": {
                "known": { "type": "noul", "noul": 0.5 },
                "future": { "type": "ranking", "ranking": ["a", "b"], "confidence": 0.9 }
              },
              "usage": { "input_tokens": 1, "output_tokens": 2 }
            }
            """;

        var (client, _) = TestClient.Returning(Json);

        var result = await client.SystemOneAsync("text", [new NoulQuestion("known", "q?")]);

        // A newly introduced answer kind must not cost the caller the answers the SDK does model.
        Assert.Equal(0.5, result.Noul("known").Probability);

        var unknown = Assert.Single(result.UnknownAnswers).Value;
        Assert.Equal("ranking", unknown.Type);
        Assert.Equal("future", unknown.Id);

        // The complete body stays reachable so a caller that understands the new kind can read it.
        Assert.Equal(2, result.RawJson.GetProperty("answers").GetProperty("future").GetProperty("ranking").GetArrayLength());
    }

    [Fact]
    public async Task UnknownFieldsOnKnownAnswersArePreserved()
    {
        const string Json = """
            {
              "model": "jev-latest",
              "answers": {
                "a": { "type": "noul", "noul": 0.4, "future_field": 7 }
              },
              "usage": { "input_tokens": 1, "output_tokens": 2 }
            }
            """;

        var (client, _) = TestClient.Returning(Json);

        var result = await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")]);

        Assert.Equal(7, result.Noul("a").AdditionalProperties["future_field"]!.GetValue<int>());
    }

    [Fact]
    public async Task MissingUsageIsNullRatherThanZero()
    {
        const string Json = """
            { "model": "jev-latest", "answers": { "a": { "type": "noul", "noul": 0.4 } } }
            """;

        var (client, _) = TestClient.Returning(Json);

        var result = await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")]);

        // The token counts are nullable because the API does not always report them, and an
        // unreported count is not a count of zero.
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task NullableTokenCountsArePreservedIndividually()
    {
        const string Json = """
            {
              "model": "jev-latest",
              "answers": { "a": { "type": "noul", "noul": 0.4 } },
              "usage": { "input_tokens": 12 }
            }
            """;

        var (client, _) = TestClient.Returning(Json);

        var result = await client.SystemOneAsync("text", [new NoulQuestion("a", "q?")]);

        Assert.NotNull(result.Usage);
        Assert.Equal(12, result.Usage!.InputTokens);
        Assert.Null(result.Usage.OutputTokens);
        Assert.Equal(12, result.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("2147483648", "2", null, 2)]
    [InlineData("-2147483649", "2", null, 2)]
    [InlineData("1.5", "2", null, 2)]
    [InlineData("1e10", "2", null, 2)]
    [InlineData("2", "2147483648", 2, null)]
    [InlineData("2", "1.5", 2, null)]
    [InlineData("2147483648", "1.5", null, null)]
    public async Task ATokenCountThatDoesNotFitAnIntDoesNotFailTheResponse(
        string inputTokens, string outputTokens, int? expectedInput, int? expectedOutput)
    {
        var result = await ReadUsageAsync(inputTokens, outputTokens);

        // Usage is optional metadata; a count the model cannot hold used to throw FormatException
        // and discard the answers along with it.
        Assert.Equal(0.4, result.Noul("a").Probability);
        Assert.Equal(expectedInput, result.Usage!.InputTokens);
        Assert.Equal(expectedOutput, result.Usage.OutputTokens);
        Assert.Equal(inputTokens, result.RawJson.GetProperty("usage").GetProperty("input_tokens").GetRawText());
        Assert.Equal(outputTokens, result.RawJson.GetProperty("usage").GetProperty("output_tokens").GetRawText());
    }

    [Theory]
    [InlineData("2147483647", "0", int.MaxValue, 0, int.MaxValue)]
    [InlineData("-2147483648", "0", int.MinValue, 0, int.MinValue)]
    [InlineData("2147483647", "1", int.MaxValue, 1, null)]
    public async Task ATokenCountAtTheIntLimitIsKeptAndAnOverflowingTotalIsUnknown(
        string inputTokens, string outputTokens, int expectedInput, int expectedOutput, int? expectedTotal)
    {
        var result = await ReadUsageAsync(inputTokens, outputTokens);

        // Representable counts are never rejected; a total that does not fit an int is unknown
        // rather than wrapping around to a negative number.
        Assert.Equal(expectedInput, result.Usage!.InputTokens);
        Assert.Equal(expectedOutput, result.Usage.OutputTokens);
        Assert.Equal(expectedTotal, result.Usage.TotalTokens);
    }

    private static Task<SystemOneResult> ReadUsageAsync(string inputTokens, string outputTokens)
    {
        var json = $$"""
            {
              "model": "jev-latest",
              "answers": { "a": { "type": "noul", "noul": 0.4 } },
              "usage": { "input_tokens": {{inputTokens}}, "output_tokens": {{outputTokens}} }
            }
            """;

        var (client, _) = TestClient.Returning(json);
        return client.SystemOneAsync("text", [new NoulQuestion("a", "q?")], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheResolvedModelIsReportedNotTheAlias()
    {
        var (client, _) = TestClient.Returning(Fixtures.ThreeScoreResponse);

        var result = await client.SystemOneAsync("text", [new ScoreQuestion("severity", "q?", ["low", "medium", "high"])]);

        // The request asked for jev-latest; the response reports what the alias resolved to, which
        // is what a reproducible decision must record.
        Assert.Equal("jev-1.13.0", result.Model);
    }

    [Fact]
    public async Task RequestIdIsSurfacedFromTheResponseHeader()
    {
        var (client, _) = TestClient.Returning(Fixtures.NoulResponse, requestId: "req_abc123");

        var result = await client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")]);

        Assert.Equal("req_abc123", result.RequestId);
    }

    [Fact]
    public async Task AnAbsentRequestIdIsNull()
    {
        var (client, _) = TestClient.Returning(Fixtures.NoulResponse, requestId: null);

        var result = await client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")]);

        Assert.Null(result.RequestId);
    }

    [Fact]
    public async Task AMissingAnswerIsNotAnError()
    {
        // The API does not guarantee an answer for every question that was asked, which is what
        // makes speculative fan-out practical.
        var (client, _) = TestClient.Returning(Fixtures.NoulResponse);

        var result = await client.SystemOneAsync(
            "text",
            [new NoulQuestion("is_urgent", "q?"), new NoulQuestion("is_speculative", "q?")]);

        Assert.True(result.Contains("is_urgent"));
        Assert.False(result.Contains("is_speculative"));
        Assert.False(result.TryGet("is_speculative", out _));
        Assert.Throws<KeyNotFoundException>(() => result.Noul("is_speculative"));
    }

    [Fact]
    public async Task AskingForTheWrongAnswerKindFails()
    {
        var (client, _) = TestClient.Returning(Fixtures.NoulResponse);

        var result = await client.SystemOneAsync("text", [new NoulQuestion("is_urgent", "q?")]);

        Assert.Throws<KeyNotFoundException>(() => result.Choice("is_urgent"));
    }

    [Fact]
    public async Task ResponsesRoundTripThroughJson()
    {
        var (client, _) = TestClient.Returning(TestClient.TriageResponse);

        var result = await client.SystemOneAsync("state", TestClient.TriageQuestions());

        // Cached answers can be re-scored without paying for inference again, which is the point of
        // making answers serializable.
        var json = Serialization.TypeSafeJson.Serialize(result);
        var restored = Serialization.TypeSafeJson.DeserializeResult(json);

        Assert.Equal(result.Model, restored.Model);
        Assert.Equal(0.92, restored.Noul("is_urgent").Probability);
        Assert.Equal("technical", restored.Choice("department").Label);
        Assert.Equal(1.6, restored.Score("frustration").Score);
        Assert.Equal("Very angry", restored.Score("frustration").LegendTextAtLevel(2));
    }

    [Fact]
    public async Task IndividualAnswersRoundTripThroughJson()
    {
        var (client, _) = TestClient.Returning(Fixtures.ScoreResponse);

        var result = await client.SystemOneAsync("text", [new ScoreQuestion("frustration", "q?", ["a", "b", "c"])]);
        var answer = result.Score("frustration");

        var restored = Assert.IsType<ScoreAnswer>(
            Serialization.TypeSafeJson.DeserializeAnswer(Serialization.TypeSafeJson.Serialize(answer)));

        Assert.Equal(answer.Id, restored.Id);
        Assert.Equal(answer.Score, restored.Score);
        Assert.Equal(answer.Confidence, restored.Confidence);
    }

    [Fact]
    public void UnknownAnswersRoundTripWithEachPropertyWrittenOnce()
    {
        const string Json = """{"id":"future","type":"ranking","ranking":["a","b"],"confidence":0.9}""";

        var once = Serialization.TypeSafeJson.Serialize(Serialization.TypeSafeJson.DeserializeAnswer(Json));
        var twice = Serialization.TypeSafeJson.Serialize(Serialization.TypeSafeJson.DeserializeAnswer(once));

        // Strict parsers reject duplicate keys, and a cache that round-trips repeatedly must not
        // pile up copies.
        Assert.Equal(once, twice);
        using var document = JsonDocument.Parse(twice);
        Assert.Equal(
            ["id", "type", "ranking", "confidence"],
            document.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void UnknownAnswersAlsoWriteExtrasThatAreNotInRaw()
    {
        using var raw = JsonDocument.Parse("""{"type":"ranking","ranking":["a"]}""");
        var answer = new UnknownAnswer(
            "future",
            "ranking",
            raw.RootElement.Clone(),
            new Dictionary<string, JsonNode?> { ["ranking"] = new JsonArray("x"), ["note"] = "kept" });

        using var document = JsonDocument.Parse(Serialization.TypeSafeJson.Serialize(answer));

        // Raw is the body exactly as it arrived, so it wins a name clash; an extra it lacks is kept.
        Assert.Equal(
            ["id", "type", "ranking", "note"],
            document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("a", document.RootElement.GetProperty("ranking")[0].GetString());
    }

    [Fact]
    public void QuestionsRoundTripThroughJson()
    {
        Question original = new ChoiceQuestion(
            "department",
            new JsonObject { ["field"] = "team" },
            new Dictionary<string, string?> { ["billing"] = "Payments", ["technical"] = null });

        var restored = Serialization.TypeSafeJson.DeserializeQuestion(
            Serialization.TypeSafeJson.Serialize(original));

        var choice = Assert.IsType<ChoiceQuestion>(restored);
        Assert.Equal("department", choice.Id);
        Assert.Equal(["billing", "technical"], choice.Labels);
        Assert.Equal("Payments", choice.Criteria["billing"]!.GetValue<string>());
        Assert.Null(choice.Criteria["technical"]);
    }

    [Fact]
    public void RawQuestionsKeepInstructionsWhenRoundTrippedThroughJson()
    {
        Question original = new RawQuestion(
            "future",
            "ranking",
            new JsonObject
            {
                ["instructions"] = new JsonObject { ["question"] = "Rank these values." },
                ["criteria"] = new JsonArray("a", "b"),
            });

        var restored = Assert.IsType<RawQuestion>(
            Serialization.TypeSafeJson.DeserializeQuestion(Serialization.TypeSafeJson.Serialize(original)));

        Assert.Equal(
            "Rank these values.",
            restored.Body["instructions"]!["question"]!.GetValue<string>());
        Assert.Equal(2, restored.Body["criteria"]!.AsArray().Count);
    }

    [Fact]
    public async Task AResponseWithoutAnAnswersMapIsRejected()
    {
        var (client, _) = TestClient.Returning("""{"model":"jev-latest","usage":{}}""");

        // Returning an empty result here would surface later as a confusing missing-key error.
        var exception = await Assert.ThrowsAsync<TypeSafeResponseValidationException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")]));

        Assert.Equal("answers", exception.FieldPath);
    }

    [Fact]
    public async Task ANonJsonSuccessBodyIsRejected()
    {
        var (client, _) = TestClient.Returning("<html>hello</html>");

        await Assert.ThrowsAsync<TypeSafeResponseValidationException>(
            () => client.SystemOneAsync("text", [new NoulQuestion("a", "q?")]));
    }

    [Fact]
    public void NormalizedEntropyIsOfferedButIsNotTheApiConfidence()
    {
        // The published preview formula. The SDK never uses it to populate Confidence, because the
        // current formula is not published and produces different values.
        var uniform = ProbabilityMath.NormalizedEntropy([0.25, 0.25, 0.25, 0.25]);
        var concentrated = ProbabilityMath.NormalizedEntropy([0.0, 0.0, 1.0]);

        Assert.Equal(0, uniform, precision: 10);
        Assert.Equal(1, concentrated, precision: 10);
    }
}
