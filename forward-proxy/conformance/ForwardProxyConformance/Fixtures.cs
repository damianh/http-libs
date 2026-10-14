// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DamianH.Http.ForwardProxy.Conformance;

internal static class Expectations
{
    public const string Forward = "forward";
    public const string Reject = "reject";
    public const string NotForwarded = "not-forwarded";
    public const string RejectOrForward = "reject-or-forward";

    public static readonly IReadOnlyList<string> All = [Forward, Reject, NotForwarded, RejectOrForward];
}

internal sealed class FixtureCorpus
{
    public required IReadOnlyList<Fixture> Fixtures { get; init; }

    public static FixtureCorpus Load(string path) =>
        JsonSerializer.Deserialize<FixtureCorpus>(File.ReadAllText(path), ConformanceJson.Options)
        ?? throw new InvalidDataException($"{path} is empty.");
}

internal sealed class Fixture
{
    public required string Id { get; init; }
    public string Category { get; init; } = "";
    public required string Reference { get; init; }
    public required string Description { get; init; }

    /// <summary>Raw request bytes (Latin-1); a list sends each piece as a separate write.</summary>
    [JsonConverter(typeof(StringOrListConverter))]
    public required IReadOnlyList<string> Payload { get; init; }

    public required string Expect { get; init; }
    public IReadOnlyList<FixtureCheck> Checks { get; init; } = [];

    /// <summary>Allowed proxy statuses for a rejection; any 4xx when omitted.</summary>
    public IReadOnlyList<int>? Status { get; init; }
}

internal sealed class FixtureCheck
{
    public int Request { get; init; }
    public int? Count { get; init; }
    public string? Method { get; init; }
    public string? Uri { get; init; }
    public string? Version { get; init; }
    public string? Body { get; init; }
    public string? Header { get; init; }
    public bool Absent { get; init; }

    [JsonPropertyName("equals")]
    public string? EqualTo { get; init; }

    [JsonPropertyName("notEquals")]
    public string? NotEqualTo { get; init; }

    public string? Matches { get; init; }
    public string? NotMatches { get; init; }
}

internal sealed class StringOrListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? [reader.GetString()!]
            : JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? [];

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

internal static class ConformanceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Formats a value for a failure reason, escaping control characters.</summary>
    public static string Quote(object? value) => JsonSerializer.Serialize(value, Compact);

    private static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
