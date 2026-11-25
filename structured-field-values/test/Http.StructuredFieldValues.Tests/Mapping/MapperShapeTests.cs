// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Shouldly;

namespace DamianH.Http.StructuredFieldValues.Mapping;

public class MapperShapeTests
{
    private static readonly StructuredFieldMapper<EncodingItem> EncodingMapper =
        StructuredFieldMapper<EncodingItem>.Item(b => b
            .Value(x => x.Encoding, type: ItemType.Token)
            .Parameter("q", x => x.Quality));

    private static readonly StructuredFieldMapper<PriorityHeader> PriorityMapper =
        StructuredFieldMapper<PriorityHeader>.Dictionary(b => b
            .Member("u", x => x.Urgency)
            .Member("i", x => x.Incremental));

    private static readonly StructuredFieldMapper<AcceptClientHintHeaderValue> AcceptChMapper =
        StructuredFieldMapper<AcceptClientHintHeaderValue>.List(b => b
            .Elements(x => x.Hints, type: ItemType.Token));

    private static readonly StructuredFieldMapper<FeaturePolicy> FeaturePolicyMapper =
        StructuredFieldMapper<FeaturePolicy>.Dictionary(b => b
            .InnerList("camera", x => x.Camera, type: ItemType.Token)
            .InnerList("microphone", x => x.Microphone, type: ItemType.Token));

    private static readonly StructuredFieldMapper<AcceptEncodingHeader> AcceptEncodingMapper =
        StructuredFieldMapper<AcceptEncodingHeader>.List(b => b
            .Elements(x => x.Encodings, EncodingMapper));

    private static readonly StructuredFieldMapper<EncodingDictionary> EncodingDictionaryMapper =
        StructuredFieldMapper<EncodingDictionary>.Dictionary(b => b
            .InnerList("enc", x => x.Encodings, EncodingMapper));

    [Fact]
    public void Item_TokenWithOptionalParameter_ParsesAndSerializes()
    {
        var item = EncodingMapper.Parse("gzip;q=0.9");
        item.Encoding.ShouldBe("gzip");
        item.Quality.ShouldBe(0.9m);
        EncodingMapper.Serialize(item).ShouldBe("gzip;q=0.9");

        var bare = EncodingMapper.Parse("identity");
        bare.Quality.ShouldBeNull();
        EncodingMapper.Serialize(bare).ShouldBe("identity");
    }

    [Fact]
    public void Item_InferredBooleanAndInteger_Parse()
    {
        StructuredFieldMapper<BoolItemHolder>.Item(b => b.Value(x => x.Flag)).Parse("?1").Flag.ShouldBe(true);
        StructuredFieldMapper<IntItemHolder>.Item(b => b.Value(x => x.Count)).Parse("42").Count.ShouldBe(42);
    }

    [Theory]
    [InlineData("u=3, i", 3, true)]
    [InlineData("u=5", 5, null)]
    [InlineData("i", null, true)]
    [InlineData("", null, null)]
    public void Dictionary_MembersAndBooleanShorthand_ParseAndSerialize(string input, int? urgency, bool? incremental)
    {
        var priority = PriorityMapper.Parse(input);

        priority.Urgency.ShouldBe(urgency);
        priority.Incremental.ShouldBe(incremental);
        PriorityMapper.Serialize(priority).ShouldBe(input);
    }

    [Fact]
    public void Dictionary_IntegerOverflowForInt32_ThrowsParseException()
    {
        var ex = Should.Throw<StructuredFieldParseException>(() => PriorityMapper.Parse("u=999999999999"));
        ex.Message.ShouldContain("overflows Int32");
    }

    [Fact]
    public void List_TokenElements_ParseAndSerialize()
    {
        const string input = "Sec-CH-UA, Sec-CH-UA-Platform, Sec-CH-UA-Mobile";

        var header = AcceptChMapper.Parse(input);

        header.Hints.ShouldBe(["Sec-CH-UA", "Sec-CH-UA-Platform", "Sec-CH-UA-Mobile"]);
        AcceptChMapper.Serialize(header).ShouldBe(input);
    }

    [Fact]
    public void List_InferredIntegerElements_Parse()
    {
        var mapper = StructuredFieldMapper<LongListHolder>.List(b => b.Elements(x => x.Values));

        mapper.Parse("1, 2, 3").Values.ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public void InnerList_TokenInnerLists_ParseAndSerialize()
    {
        const string input = "camera=(self), microphone=(self https://example.com)";

        var policy = FeaturePolicyMapper.Parse(input);

        policy.Camera.ShouldBe(["self"]);
        policy.Microphone.ShouldBe(["self", "https://example.com"]);
        FeaturePolicyMapper.Serialize(policy).ShouldBe(input);
    }

    [Fact]
    public void InnerList_MissingIsNullAndEmptyIsEmpty()
    {
        var policy = FeaturePolicyMapper.Parse("camera=()");

        policy.Camera.ShouldNotBeNull();
        policy.Camera.ShouldBeEmpty();
        policy.Microphone.ShouldBeNull();
    }

    [Fact]
    public void InnerList_InferredIntegerElements_Parse()
    {
        var mapper = StructuredFieldMapper<LongInnerListHolder>.Dictionary(b => b
            .InnerList<long>("vals", x => x.Values));

        mapper.Parse("vals=(1 2 3)").Values.ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public void NestedItems_InList_ParseAndSerialize()
    {
        const string input = "gzip;q=0.9, br;q=0.8, identity";

        var header = AcceptEncodingMapper.Parse(input);

        header.Encodings.Select(x => (x.Encoding, x.Quality))
            .ShouldBe([("gzip", 0.9m), ("br", 0.8m), ("identity", (decimal?)null)]);
        AcceptEncodingMapper.Serialize(header).ShouldBe(input);
    }

    [Fact]
    public void NestedItems_InDictionaryInnerList_ParseAndSerialize()
    {
        const string input = "enc=(gzip;q=0.9 br;q=0.8)";

        var value = EncodingDictionaryMapper.Parse(input);

        value.Encodings.ShouldNotBeNull();
        value.Encodings.Select(x => (x.Encoding, x.Quality)).ShouldBe([("gzip", 0.9m), ("br", 0.8m)]);
        EncodingDictionaryMapper.Serialize(value).ShouldBe(input);
    }

    [Fact]
    public void TryParse_InvalidInput_ReturnsFalseForEveryShape()
    {
        const string invalid = "<<<invalid>>>";

        EncodingMapper.TryParse(invalid, out var item).ShouldBeFalse();
        item.ShouldBeNull();
        AcceptChMapper.TryParse(invalid, out var list).ShouldBeFalse();
        list.ShouldBeNull();
        PriorityMapper.TryParse(invalid, out var dictionary).ShouldBeFalse();
        dictionary.ShouldBeNull();
    }

    private class BoolItemHolder
    {
        public bool? Flag { get; init; }
    }

    private class IntItemHolder
    {
        public int? Count { get; init; }
    }

    private class LongListHolder
    {
        public IReadOnlyList<long> Values { get; init; } = [];
    }

    private class LongInnerListHolder
    {
        public IReadOnlyList<long>? Values { get; init; }
    }

    private class FeaturePolicy
    {
        public IReadOnlyList<string>? Camera { get; init; }
        public IReadOnlyList<string>? Microphone { get; init; }
    }

    private class AcceptEncodingHeader
    {
        public IReadOnlyList<EncodingItem> Encodings { get; init; } = [];
    }

    private class EncodingDictionary
    {
        public IReadOnlyList<EncodingItem>? Encodings { get; init; }
    }
}
