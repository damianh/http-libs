// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

using Shouldly;

namespace DamianH.Http.StructuredFieldValues;

public class ParametersTests
{
    [Fact]
    public void Constructor_WithInitialValues_Success()
    {
        var initial = new[]
        {
            new KeyValuePair<string, BareItem>("a", new IntegerItem(1)),
            new KeyValuePair<string, BareItem>("b", new IntegerItem(2))
        };

        var parameters = new Parameters(initial);

        parameters.Count.ShouldBe(2);
        parameters["a"].ShouldBeOfType<IntegerItem>();
        parameters["b"].ShouldBeOfType<IntegerItem>();
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException() => Should.Throw<ArgumentNullException>(() => new Parameters(null!));

    [Fact]
    public void Add_Flag_IsNonNullBooleanTrue()
    {
        var parameters = new Parameters();
        parameters.Add("flag");

        parameters.Count.ShouldBe(1);
        parameters["flag"].ShouldBeSameAs(BooleanItem.True);
    }

    [Fact]
    public void NullValues_AreRejectedByAllEntryPoints()
    {
        var parameters = new Parameters();
        Should.Throw<ArgumentNullException>(() => parameters.Add("flag", null!));
        Should.Throw<ArgumentNullException>(() => parameters["flag"] = null!);
        Should.Throw<ArgumentNullException>(() =>
            new Parameters([new KeyValuePair<string, BareItem>("flag", null!)]));
        parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void Replacement_PreservesFirstPosition()
    {
        var parameters = new Parameters { { "a", new IntegerItem(1) }, { "b", new IntegerItem(2) } };
        parameters["a"] = BooleanItem.True;
        parameters.Select(x => x.Key).ShouldBe(["a", "b"]);
        parameters["a"].ShouldBeSameAs(BooleanItem.True);
    }

    [Fact]
    public void Add_DuplicateKey_ThrowsArgumentException()
    {
        var parameters = new Parameters { { "test", new IntegerItem(1) } };

        Should.Throw<ArgumentException>(() => parameters.Add("test", new IntegerItem(2)));
    }

    [Fact]
    public void Add_InvalidKey_ThrowsArgumentException()
    {
        var parameters = new Parameters();
        var exception = Should.Throw<ArgumentException>(() => parameters.Add("invalid key", new IntegerItem(1)));
        exception.Message.ShouldContain("Parameter key 'invalid key' is not a valid RFC 9651 key.");
    }

    [Fact]
    public void GetEnumerator_PreservesInsertionOrder()
    {
        var parameters = new Parameters
        {
            { "c", new IntegerItem(3) },
            { "a", new IntegerItem(1) },
            { "b", new IntegerItem(2) }
        };

        parameters.Select(kvp => kvp.Key).ShouldBe(["c", "a", "b"]);
    }
}
