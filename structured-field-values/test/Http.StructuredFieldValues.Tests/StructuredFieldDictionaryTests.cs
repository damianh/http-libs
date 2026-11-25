// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

using Shouldly;

namespace DamianH.Http.StructuredFieldValues;

public class StructuredFieldDictionaryTests
{
    [Fact]
    public void Constructor_WithMembers_Success()
    {
        var members = new[]
        {
            new KeyValuePair<string, StructuredFieldMember>("a", StructuredFieldMember.FromItem(new IntegerItem(1))),
            new KeyValuePair<string, StructuredFieldMember>("b", StructuredFieldMember.FromItem(new IntegerItem(2)))
        };

        var dict = new StructuredFieldDictionary(members);

        dict.Count.ShouldBe(2);
        dict["a"].Item.Value.ShouldBeOfType<IntegerItem>();
        dict["b"].Item.Value.ShouldBeOfType<IntegerItem>();
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException() => Should.Throw<ArgumentNullException>(() => new StructuredFieldDictionary(null!));

    [Fact]
    public void Add_Item_WrapsInMember()
    {
        var dict = new StructuredFieldDictionary();
        var item = new IntegerItem(42);

        dict.Add("test", item);

        dict["test"].Item.Value.ShouldBe(item);
    }

    [Fact]
    public void Add_InnerList_WrapsInMember()
    {
        var dict = new StructuredFieldDictionary();
        var innerList = new InnerList([new IntegerItem(1)]);

        dict.Add("test", innerList);

        dict["test"].InnerList.ShouldBe(innerList);
    }

    [Fact]
    public void Add_DuplicateKey_ThrowsArgumentException()
    {
        var dict = new StructuredFieldDictionary { { "test", new IntegerItem(1) } };

        Should.Throw<ArgumentException>(() => dict.Add("test", new IntegerItem(2)));
    }

    [Fact]
    public void Add_InvalidKey_ThrowsArgumentException()
    {
        var dict = new StructuredFieldDictionary();
        var exception = Should.Throw<ArgumentException>(() => dict.Add("invalid key", new IntegerItem(1)));
        exception.Message.ShouldContain("Dictionary key 'invalid key' is not a valid RFC 9651 key.");
    }

    [Fact]
    public void GetEnumerator_PreservesInsertionOrder()
    {
        var dict = new StructuredFieldDictionary
        {
            { "c", new IntegerItem(3) },
            { "a", new IntegerItem(1) },
            { "b", new IntegerItem(2) }
        };

        dict.Select(kvp => kvp.Key).ShouldBe(["c", "a", "b"]);
    }

    [Fact]
    public void ToString_IsDiagnostic()
    {
        var dict = new StructuredFieldDictionary
        {
            { "a", new IntegerItem(1) },
            { "b", new IntegerItem(2) }
        };

        dict.ToString().ShouldBe("Dictionary(2 members)");
    }

    [Fact]
    public void NullMembers_AreRejectedByAllEntryPoints()
    {
        var dictionary = new StructuredFieldDictionary();
        Should.Throw<ArgumentNullException>(() => dictionary.Add("a", (BareItem)null!));
        Should.Throw<ArgumentNullException>(() => dictionary.Add("a", (StructuredFieldMember)null!));
        Should.Throw<ArgumentNullException>(() => dictionary.Add("a", (StructuredFieldItem)null!));
        Should.Throw<ArgumentNullException>(() => dictionary.Add("a", (InnerList)null!));
        Should.Throw<ArgumentNullException>(() => dictionary["a"] = null!);
        Should.Throw<ArgumentNullException>(() => new StructuredFieldDictionary(
            [new KeyValuePair<string, StructuredFieldMember>("a", null!)]));
        dictionary.Count.ShouldBe(0);
    }
}
