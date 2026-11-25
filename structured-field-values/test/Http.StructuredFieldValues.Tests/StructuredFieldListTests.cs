// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

using Shouldly;

namespace DamianH.Http.StructuredFieldValues;

public class StructuredFieldListTests
{
    [Fact]
    public void Constructor_WithMembers_Success()
    {
        var members = new[]
        {
            StructuredFieldMember.FromItem(new IntegerItem(1)),
            StructuredFieldMember.FromItem(new IntegerItem(2))
        };

        var list = new StructuredFieldList(members);

        list.Count.ShouldBe(2);
        list[0].Item.Value.ShouldBeOfType<IntegerItem>();
        list[1].Item.Value.ShouldBeOfType<IntegerItem>();
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException() => Should.Throw<ArgumentNullException>(() => new StructuredFieldList(null!));

    [Fact]
    public void Add_Item_WrapsInMember()
    {
        var list = new StructuredFieldList();
        var item = new IntegerItem(42);

        list.Add(item);

        list[0].Item.Value.ShouldBe(item);
    }

    [Fact]
    public void Add_InnerList_WrapsInMember()
    {
        var list = new StructuredFieldList();
        var innerList = new InnerList([new IntegerItem(1)]);

        list.Add(innerList);

        list[0].InnerList.ShouldBe(innerList);
    }

    [Fact]
    public void ToString_IsDiagnostic()
    {
        var list = new StructuredFieldList();
        list.Add(new InnerList([new IntegerItem(1), new IntegerItem(2)]));
        list.Add(new IntegerItem(3));

        list.ToString().ShouldBe("List(2 members)");
    }

    [Fact]
    public void NullElements_AreRejectedAtomically()
    {
        var list = new StructuredFieldList([new IntegerItem(1)]);
        Should.Throw<ArgumentNullException>(() => new StructuredFieldList([new IntegerItem(2), null!]));
        Should.Throw<ArgumentNullException>(() => list.AddRange(null!));
        Should.Throw<ArgumentNullException>(() => list.AddRange([new IntegerItem(2), null!]));
        Should.Throw<ArgumentNullException>(() => list.Add((BareItem)null!));
        Should.Throw<ArgumentNullException>(() => list.Add((StructuredFieldMember)null!));
        Should.Throw<ArgumentNullException>(() => list.Add((StructuredFieldItem)null!));
        Should.Throw<ArgumentNullException>(() => list.Add((InnerList)null!));
        Should.Throw<ArgumentNullException>(() => list[0] = null!);
        list.Count.ShouldBe(1);
    }
}
