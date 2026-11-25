// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using Shouldly;

namespace DamianH.Http.StructuredFieldValues.RfcCompliance;

public class RfcCorpusTests
{
    // Culture-sensitive formatting only affects numeric and date values, so only those fixtures
    // run under multiple cultures; every other fixture runs once under the invariant culture ("").
    private static readonly string[] NumericCultures = ["en-US", "fr-FR", "de-DE"];
    private static readonly string[] InvariantCulture = [""];
    private static readonly HashSet<string> CultureSensitiveFixtures =
        ["number.json", "number-generated.json", "date.json"];

    public static IEnumerable<object[]> ParseCases() => Cases(requireExpected: false);

    public static IEnumerable<object[]> SerializationCases() => Cases(requireExpected: true);

    [Fact]
    public void Discovery_ShouldIncludeTheEntireCorpus()
    {
        string[] requiredFixtures =
        [
            "binary.json", "boolean.json", "date.json", "dictionary.json", "display-string.json",
            "examples.json", "item.json", "key-generated.json", "large-generated.json", "list.json",
            "listlist.json", "number-generated.json", "number.json", "param-dict.json", "param-list.json",
            "param-listlist.json", "string-generated.json", "string.json", "token-generated.json", "token.json"
        ];

        foreach (var fileName in requiredFixtures)
        {
            RfcTestLoader.Fixtures.ContainsKey(fileName).ShouldBeTrue($"Missing authoritative fixture '{fileName}'.");
        }

        // Lower bounds guard against lost fixture content without excluding future additions.
        RfcTestLoader.Fixtures.Values.Sum(tests => tests.Length).ShouldBeGreaterThanOrEqualTo(1564);
        foreach (var (fileName, tests) in RfcTestLoader.Fixtures)
        {
            foreach (var test in tests)
            {
                var context = $"{fileName}: {test.Name}";
                test.Name.ShouldNotBeNullOrWhiteSpace(context);
                test.Raw.ShouldNotBeEmpty(context);
                (test.HeaderType is "item" or "list" or "dictionary").ShouldBeTrue(context);
                (test.MustFail && test.CanFail).ShouldBeFalse(context);
                if (!test.MustFail && !test.CanFail)
                {
                    test.Expected.HasValue.ShouldBeTrue($"Successful fixture has no Expected value: {context}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_ShouldMatchRfcFixture(string fileName, int caseIndex, string caseName, string cultureName)
    {
        using var culture = new FixtureCulture(cultureName);
        var test = RfcTestLoader.Fixtures[fileName][caseIndex];
        test.Name.ShouldBe(caseName);
        var input = string.Join(", ", test.Raw);

        if (test.MustFail)
        {
            Should.Throw<StructuredFieldParseException>(() => Parse(input, test.HeaderType));
            return;
        }

        object parsed;
        try
        {
            parsed = Parse(input, test.HeaderType);
        }
        catch (StructuredFieldParseException) when (test.CanFail)
        {
            return;
        }

        // Assertions stay outside the optional-failure catch: only parsing may fail.
        if (test.Expected is { } expected)
        {
            RfcExpectedValue.AssertMatches(parsed, test.HeaderType, expected);
        }

        if (test.Canonical != null || test.Expected.HasValue)
        {
            Serialize(parsed).ShouldBe(ExpectedWire(test));
        }
    }

    [Theory]
    [MemberData(nameof(SerializationCases))]
    public void Serialize_ShouldMatchRfcFixture(string fileName, int caseIndex, string caseName, string cultureName)
    {
        using var culture = new FixtureCulture(cultureName);
        var test = RfcTestLoader.Fixtures[fileName][caseIndex];
        test.Name.ShouldBe(caseName);
        var expected = test.Expected!.Value;
        var constructed = RfcExpectedValue.Construct(test.HeaderType, expected);

        RfcExpectedValue.AssertMatches(constructed, test.HeaderType, expected);
        var wire = Serialize(constructed);
        wire.ShouldBe(ExpectedWire(test));
        RfcExpectedValue.AssertMatches(Parse(wire, test.HeaderType), test.HeaderType, expected);
    }

    private static IEnumerable<object[]> Cases(bool requireExpected)
    {
        foreach (var (fileName, tests) in RfcTestLoader.Fixtures)
        {
            var cultures = CultureSensitiveFixtures.Contains(fileName) ? NumericCultures : InvariantCulture;
            for (var index = 0; index < tests.Length; index++)
            {
                var test = tests[index];
                if (requireExpected && (test.MustFail || !test.Expected.HasValue))
                {
                    continue;
                }

                foreach (var culture in cultures)
                {
                    // Primitive theory arguments are serializable by xUnit v3/MTP.
                    yield return [fileName, index, test.Name, culture];
                }
            }
        }
    }

    private static string ExpectedWire(RfcTestCase test) => test.Canonical != null
        ? string.Join(", ", test.Canonical)
        : RfcExpectedValue.Canonical(test.HeaderType, test.Expected!.Value);

    private static object Parse(string input, string? headerType) => headerType switch
    {
        "item" => StructuredFieldParser.ParseItem(input),
        "list" => StructuredFieldParser.ParseList(input),
        "dictionary" => StructuredFieldParser.ParseDictionary(input),
        _ => throw new InvalidDataException($"Unknown fixture header type '{headerType}'.")
    };

    private static string Serialize(object value) => value switch
    {
        StructuredFieldItem item => StructuredFieldSerializer.SerializeItem(item),
        StructuredFieldList list => StructuredFieldSerializer.SerializeList(list),
        StructuredFieldDictionary dictionary => StructuredFieldSerializer.SerializeDictionary(dictionary),
        _ => throw new InvalidDataException($"Unknown structured field type '{value.GetType()}'.")
    };

    private sealed class FixtureCulture : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

        public FixtureCulture(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }
}
