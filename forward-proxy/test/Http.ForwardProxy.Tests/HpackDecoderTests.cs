// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class HpackDecoderTests
{
    [Fact]
    public void DecodesHuffmanEncodedResponsesWithDynamicTableEviction()
    {
        // RFC 7541 Appendix C.6, prefixed with a dynamic table size update to 256 bytes so entries are evicted.
        var decoder = new Http2ConnectClient.HpackDecoder();

        Decode(decoder, "3fe101" +
            "488264025885aec3771a4b6196d07abe941054d444a8200595040b8166e082a62d1bff6e919d29ad171863c78f0b97c8e9ae82ae43d3")
            .ShouldBe(
            [
                new(":status", "302"),
                new("cache-control", "private"),
                new("date", "Mon, 21 Oct 2013 20:13:21 GMT"),
                new("location", "https://www.example.com"),
            ]);

        Decode(decoder, "4883640effc1c0bf").ShouldBe(
        [
            new(":status", "307"),
            new("cache-control", "private"),
            new("date", "Mon, 21 Oct 2013 20:13:21 GMT"),
            new("location", "https://www.example.com"),
        ]);

        Decode(decoder,
            "88c16196d07abe941054d444a8200595040b8166e084a62d1bffc05a839bd9ab77ad94e7821dd7f2e6c7b335dfdfcd5b3960d5af27087f3672c1ab270fb5291f9587316065c003ed4ee5b1063d5007")
            .ShouldBe(
            [
                new(":status", "200"),
                new("cache-control", "private"),
                new("date", "Mon, 21 Oct 2013 20:13:22 GMT"),
                new("location", "https://www.example.com"),
                new("content-encoding", "gzip"),
                new("set-cookie", "foo=ASDJKHQKBZXOQWEOPIUAXQWEOIU; max-age=3600; version=1"),
            ]);
    }

    [Theory]
    [InlineData("0f1981" + "00")] // '0' followed by three zero padding bits.
    [InlineData("0f1984" + "ffffffff")] // Contains the 30-bit EOS symbol.
    public void RejectsMalformedHuffmanStrings(string block) =>
        Should.Throw<InvalidDataException>(() => Decode(new Http2ConnectClient.HpackDecoder(), block));

    private static IReadOnlyList<KeyValuePair<string, string>> Decode(Http2ConnectClient.HpackDecoder decoder, string hex) =>
        decoder.Decode(Convert.FromHexString(hex));
}
