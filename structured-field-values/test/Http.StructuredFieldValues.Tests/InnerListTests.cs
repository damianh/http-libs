// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

using Shouldly;

namespace DamianH.Http.StructuredFieldValues;

public class InnerListTests
{
    [Fact]
    public void Constructor_WithNullItems_ThrowsArgumentNullException() => Should.Throw<ArgumentNullException>(() => new InnerList(null!));

    [Fact]
    public void Constructor_WithNullParameters_CreatesOwnedEmptyParameters()
    {
        var items = new StructuredFieldItem[] { new IntegerItem(1) };
        new InnerList(items, null).Parameters.Count.ShouldBe(0);
    }

    [Fact]
    public void Constructor_CopiesParameters()
    {
        var parameters = new Parameters { { "test", new IntegerItem(42) } };

        var innerList = new InnerList([], parameters);
        parameters.Clear();

        innerList.Parameters["test"].ShouldBeOfType<IntegerItem>();
    }

    [Fact]
    public void Items_ReturnsReadOnlyList()
    {
        var innerList = new InnerList();
        innerList.Add(new IntegerItem(1));

        var items = innerList.Items;

        items.ShouldBeOfType<System.Collections.ObjectModel.ReadOnlyCollection<StructuredFieldItem>>();
        items.Count.ShouldBe(1);
    }

    [Fact]
    public void ToString_IsDiagnostic()
    {
        var innerList = new InnerList();
        innerList.Add(new IntegerItem(1));
        innerList.Parameters.Add("test", new IntegerItem(42));

        innerList.ToString().ShouldBe("InnerList(1 items; 1 parameters)");
    }

    [Fact]
    public void NullElements_AreRejectedAtomically()
    {
        var list = new InnerList([new IntegerItem(1)]);
        Should.Throw<ArgumentNullException>(() => new InnerList([new IntegerItem(2), null!]));
        Should.Throw<ArgumentNullException>(() => list.AddRange(null!));
        Should.Throw<ArgumentNullException>(() => list.AddRange([new IntegerItem(2), null!]));
        Should.Throw<ArgumentNullException>(() => list.Add((StructuredFieldItem)null!));
        Should.Throw<ArgumentNullException>(() => list.Add((BareItem)null!));
        Should.Throw<ArgumentNullException>(() => list[0] = null!);
        list.Count.ShouldBe(1);
        list[0].Value.ShouldBe(new IntegerItem(1));
    }
}
