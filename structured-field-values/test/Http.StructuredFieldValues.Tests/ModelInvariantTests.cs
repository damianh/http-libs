// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

using Shouldly;

namespace DamianH.Http.StructuredFieldValues;

public class ModelInvariantTests
{
    [Fact]
    public void ByteSequence_DefensivelyCopiesEveryArrayBoundary()
    {
        byte[] source = [1, 2, 3];
        var value = new ByteSequenceItem(source);
        var expected = new ByteSequenceItem([1, 2, 3]);
        var hash = value.GetHashCode();
        var lookup = new HashSet<BareItem> { value };

        source[0] = 9;
        value.ToArray()[0] = 8;
        value.ByteArrayValue[0] = 7;
        ((byte[])value.Value)[0] = 6;

        value.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }).ShouldBeTrue();
        value.ShouldBe(expected);
        value.GetHashCode().ShouldBe(hash);
        lookup.Contains(expected).ShouldBeTrue();
        value.Base64Value.ShouldBe("AQID");
        value.ToArray().ShouldNotBeSameAs(value.ToArray());
    }

    [Fact]
    public void BareValues_CompareKindAndValue()
    {
        BareItem[] first =
        [
            new IntegerItem(1), new DecimalItem(1m), new StringItem("x"), new TokenItem("x"),
            new ByteSequenceItem([1]), new BooleanItem(true), new DateItem(1), new DisplayStringItem("x")
        ];
        BareItem[] second =
        [
            new IntegerItem(1), new DecimalItem(1m), new StringItem("x"), new TokenItem("x"),
            new ByteSequenceItem([1]), new BooleanItem(true), new DateItem(1), new DisplayStringItem("x")
        ];
        for (var i = 0; i < first.Length; i++)
        {
            first[i].Equals(second[i]).ShouldBeTrue();
            first[i].GetHashCode().ShouldBe(second[i].GetHashCode());
            for (var j = 0; j < first.Length; j++)
            {
                first[i].Equals(second[j]).ShouldBe(i == j);
            }
        }
    }

    [Fact]
    public void ItemAndInnerList_CopyParametersButNotBareValues()
    {
        var parameters = new Parameters { { "a", new ByteSequenceItem([1, 2]) } };
        var first = new StructuredFieldItem(BooleanItem.True, parameters);
        var second = new StructuredFieldItem(BooleanItem.True, parameters);
        var inner = new InnerList([first], parameters);

        first.Parameters.ShouldNotBeSameAs(parameters);
        second.Parameters.ShouldNotBeSameAs(first.Parameters);
        inner.Parameters.ShouldNotBeSameAs(parameters);
        first.Parameters["a"].ShouldBeSameAs(parameters["a"]);
        first.Parameters.Add("flag");
        parameters.Clear();
        second.Parameters.Count.ShouldBe(1);
        inner.Parameters.Count.ShouldBe(1);
        first.Parameters.Count.ShouldBe(2);
        inner[0].ShouldBeSameAs(first);
    }

    [Fact]
    public void MutableNodes_UseStableReferenceEquality()
    {
        var first = new StructuredFieldItem(new IntegerItem(1));
        var second = new StructuredFieldItem(new IntegerItem(1));
        var hash = first.GetHashCode();
        var lookup = new HashSet<StructuredFieldItem> { first };
        first.Equals(second).ShouldBeFalse();
        first.Value = new StringItem("changed");
        first.Parameters.Add("flag");
        first.GetHashCode().ShouldBe(hash);
        lookup.Contains(first).ShouldBeTrue();
        new Parameters().Equals(new Parameters()).ShouldBeFalse();
        new InnerList([first]).Equals(new InnerList([first])).ShouldBeFalse();
        new StructuredFieldList([first]).Equals(new StructuredFieldList([first])).ShouldBeFalse();
        new StructuredFieldDictionary().Equals(new StructuredFieldDictionary()).ShouldBeFalse();
        StructuredFieldMember.FromItem(first).Equals(StructuredFieldMember.FromItem(first)).ShouldBeFalse();
    }

    [Fact]
    public void Item_RejectsNullValueAndOwnsDefaultParameters()
    {
        Should.Throw<ArgumentNullException>(() => new StructuredFieldItem(null!));
        Should.Throw<ArgumentNullException>(() => (StructuredFieldItem)(BareItem)null!);
        var first = new StructuredFieldItem(BooleanItem.True);
        var second = new StructuredFieldItem(BooleanItem.True);
        Should.Throw<ArgumentNullException>(() => first.Value = null!);
        first.Value.ShouldBeSameAs(BooleanItem.True);
        first.Parameters.ShouldNotBeSameAs(second.Parameters);
        first.Value = new DateItem(7);
        first.Type.ShouldBe(ItemType.Date);
    }

    [Fact]
    public void Scalars_RejectNullInputs()
    {
        Should.Throw<ArgumentNullException>(() => new StringItem(null!));
        Should.Throw<ArgumentNullException>(() => new TokenItem(null!));
        Should.Throw<ArgumentNullException>(() => new DisplayStringItem(null!));
        Should.Throw<ArgumentNullException>(() => new ByteSequenceItem(null!));
        Should.Throw<ArgumentNullException>(() => ByteSequenceItem.FromBase64(null!));
    }

    [Theory]
    [InlineData("Zh==", "Zg==")]
    [InlineData("Zm9=", "Zm8=")]
    [InlineData("iZ==", "iQ==")]
    [InlineData("iZ==\r\n", "iQ==")]
    [InlineData("iZ= =", "iQ==")]
    [InlineData("Z h =\t=", "Zg==")]
    public void ByteSequences_AcceptNonZeroPadBits(string input, string expected)
    {
        ByteSequenceItem.FromBase64(input).Base64Value.ShouldBe(expected);
    }

    [Fact]
    public void DisplayStrings_RejectUnpairedSurrogates()
    {
        foreach (var input in new[] { "\ud800", "\udc00", "x\ud800y", "\udc00\ud800" })
        {
            Should.Throw<ArgumentException>(() => new DisplayStringItem(input));
        }
    }

    [Fact]
    public void DisplayStrings_AllowAllUnicodeScalarsIncludingControls()
    {
        var input = "\0\r\n\t\ud83d\ude80\uFEFF";
        var value = new DisplayStringItem(input);
        value.StringValue.ShouldBe(input);
        StructuredFieldSerializer.SerializeBareItem(value).ShouldBe("%\"%00%0d%0a%09%f0%9f%9a%80%ef%bb%bf\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0a")]
    [InlineData("_a")]
    [InlineData("-a")]
    [InlineData(".a")]
    [InlineData("A")]
    [InlineData("a\n")]
    [InlineData("a b")]
    public void KeyValidation_RejectsInvalidStartsAndTerminators(string key)
    {
        TokenItem.IsValidKey(key).ShouldBeFalse();
        var parameters = new Parameters();
        var dictionary = new StructuredFieldDictionary();
        Should.Throw<ArgumentException>(() => parameters.Add(key, BooleanItem.True));
        Should.Throw<ArgumentException>(() => parameters[key] = BooleanItem.True);
        Should.Throw<ArgumentException>(() => dictionary.Add(key, BooleanItem.True));
        Should.Throw<ArgumentException>(() => dictionary[key] = BooleanItem.True);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*a._-09")]
    [InlineData("a0._-*")]
    public void KeyValidation_AcceptsExactKeyGrammar(string key)
    {
        TokenItem.IsValidKey(key).ShouldBeTrue();
        new Parameters { { key, BooleanItem.True } }.Count.ShouldBe(1);
        new StructuredFieldDictionary { { key, BooleanItem.True } }.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("a'`^|")]
    [InlineData("*!#$%&'*+-.^_`|~:/09AZaz")]
    public void Tokens_AcceptEveryTchar(string token)
    {
        TokenItem.IsValidToken(token).ShouldBeTrue();
        new TokenItem(token).TokenValue.ShouldBe(token);
        StructuredFieldParser.ParseItem(token).Value.ShouldBe(new TokenItem(token));
    }

    [Theory]
    [InlineData("a\n")]
    [InlineData("a\r")]
    [InlineData("a b")]
    [InlineData("1abc")]
    [InlineData("é")]
    public void Tokens_RejectInvalidInput(string token)
    {
        TokenItem.IsValidToken(token).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new TokenItem(token));
    }
}
