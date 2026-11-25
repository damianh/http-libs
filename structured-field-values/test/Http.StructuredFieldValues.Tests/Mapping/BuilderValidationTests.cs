// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Shouldly;

namespace DamianH.Http.StructuredFieldValues.Mapping;

public class BuilderValidationTests
{
    [Fact]
    public void Dictionary_DuplicateKey_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() =>
            StructuredFieldMapper<PriorityHeader>.Dictionary(b => b
                .Member("u", x => x.Urgency)
                .Member("u", x => x.Urgency)));
    }

    [Fact]
    public void Dictionary_Configure_NullThrows()
    {
        Should.Throw<ArgumentNullException>(() =>
            StructuredFieldMapper<PriorityHeader>.Dictionary(null!));
    }

    [Fact]
    public void List_Configure_NullThrows()
    {
        Should.Throw<ArgumentNullException>(() =>
            StructuredFieldMapper<AcceptClientHintHeaderValue>.List(null!));
    }

    [Fact]
    public void Item_Configure_NullThrows()
    {
        Should.Throw<ArgumentNullException>(() =>
            StructuredFieldMapper<EncodingItem>.Item(null!));
    }

    [Fact]
    public void Parse_TypeMismatch_ThrowsStructuredFieldParseException()
    {
        var mapper = StructuredFieldMapper<PriorityHeader>.Dictionary(b => b
            .Member("u", x => x.Urgency));

        // "u" should be an integer, but we pass a string
        Should.Throw<StructuredFieldParseException>(() =>
            mapper.Parse("u=\"notanint\""));
    }

    [Theory]
    [InlineData("U")]         // uppercase not allowed in keys
    [InlineData("MyKey")]     // uppercase not allowed
    [InlineData("a:b")]       // colon not allowed in keys (only in tokens)
    [InlineData("a/b")]       // slash not allowed in keys (only in tokens)
    [InlineData("a~b")]       // tilde not allowed in keys
    public void Dictionary_InvalidKey_ThrowsArgumentException(string key)
    {
        Should.Throw<ArgumentException>(() =>
            StructuredFieldMapper<PriorityHeader>.Dictionary(b => b
                .Member(key, x => x.Urgency)));
    }

    [Theory]
    [InlineData("u")]         // lowercase alpha
    [InlineData("*")]         // star start
    [InlineData("some-key")]  // hyphen
    [InlineData("some_key")]  // underscore
    [InlineData("a.b")]       // dot
    [InlineData("x1")]        // digit after alpha
    public void Dictionary_ValidKey_Succeeds(string key)
    {
        // Should not throw
        StructuredFieldMapper<PriorityHeader>.Dictionary(b => b
            .Member(key, x => x.Urgency));
    }

    [Theory]
    [InlineData("Q")]         // uppercase not allowed in parameter keys
    [InlineData("X-Custom")]  // uppercase and disallowed chars
    public void Item_InvalidParameterKey_ThrowsArgumentException(string key)
    {
        Should.Throw<ArgumentException>(() =>
            StructuredFieldMapper<EncodingItem>.Item(b => b
                .Value(x => x.Encoding, type: ItemType.Token)
                .Parameter(key, x => x.Quality)));
    }
}
