using Zvt2SumUp.Protocol;

namespace Zvt2SumUp.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void BcdRoundTripAndStrictValidation()
    {
        byte[] encoded = ZvtCodec.IntToBcd(123456789012, 6);
        Assert.Equal("123456789012", Convert.ToHexString(encoded));
        Assert.Equal(123456789012, ZvtCodec.BcdToInt(encoded));
        Assert.Throws<FormatException>(() => ZvtCodec.BcdToInt([0xFA]));
        Assert.Throws<OverflowException>(() => ZvtCodec.IntToBcd(10000, 2));
    }

    [Theory]
    [InlineData(0, "00")]
    [InlineData(254, "FE")]
    [InlineData(255, "FF00FF")]
    [InlineData(65535, "FFFFFF")]
    public void LengthEncodingMatchesZvt(int length, string expected)
    {
        byte[] value = ZvtCodec.EncodeLength(length); Assert.Equal(expected, Convert.ToHexString(value));
        Assert.True(ZvtCodec.TryDecodeLength(value, out int decoded, out int consumed)); Assert.Equal(length, decoded); Assert.Equal(value.Length, consumed);
    }

    [Fact]
    public void ApduExtendedLengthRoundTrips()
    {
        byte[] payload = Enumerable.Range(0, 300).Select(x => (byte)x).ToArray(); byte[] apdu = ZvtCodec.BuildApdu(0x06, 0xD3, payload);
        Assert.True(ZvtCodec.TryParseApdu(apdu, out ZvtCommand? command, out int consumed)); Assert.Equal(apdu.Length, consumed); Assert.Equal(payload, command!.Data.ToArray());
    }

    [Fact]
    public void BmpAmountAndBerTlvAreParsed()
    {
        byte[] amount = ZvtCodec.IntToBcd(12345, 6); byte[] data = [0x04, .. amount, 0x49, 0x09, 0x78];
        Assert.Equal(12345, ZvtCodec.ExtractAmount(data));
        byte[] longValue = Enumerable.Repeat((byte)0x42, 128).ToArray(); byte[] tlv = ZvtCodec.BerTlv([0x9F, 0x1A], longValue);
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> parsed = ZvtCodec.ParseBerTlv(tlv); Assert.Equal(longValue, parsed["9F1A"][0]); Assert.Equal(0x81, tlv[2]);
    }

    [Fact]
    public void TcpDecoderRecognizesRawAndLengthPrefixAcrossChunks()
    {
        byte[] apdu = ZvtCodec.BuildApdu(0x06, 0x01, [0x27, 0x00]); TcpFrameDecoder raw = new();
        Assert.Empty(raw.Push(apdu.AsSpan(0, 2))); var rawResult = Assert.Single(raw.Push(apdu.AsSpan(2))); Assert.Equal(TcpTransport.RawApdu, rawResult.Transport); Assert.Equal(apdu, rawResult.Apdu);
        byte[] framed = TcpFrameDecoder.Frame(apdu, TcpTransport.LengthPrefixed); TcpFrameDecoder prefixed = new();
        Assert.Empty(prefixed.Push(framed.AsSpan(0, 1))); var prefixResult = Assert.Single(prefixed.Push(framed.AsSpan(1))); Assert.Equal(TcpTransport.LengthPrefixed, prefixResult.Transport); Assert.Equal(apdu, prefixResult.Apdu);
    }

    [Fact]
    public void SerialFrameEscapesDleAndValidatesLrc()
    {
        byte[] apdu = ZvtCodec.BuildApdu(0x06, 0x01, [0x10, 0x02]); byte[] frame = SerialFraming.Frame(apdu);
        Assert.Contains("1010", Convert.ToHexString(frame));
        Assert.True(SerialFraming.TryParse(frame, out byte[]? parsed)); Assert.Equal(apdu, parsed);
        frame[^1] ^= 1; Assert.False(SerialFraming.TryParse(frame, out _));
    }

    [Fact]
    public void RegistryContainsRevision1313CommandsAndUnsupportedAnswer()
    {
        Assert.True(ZvtCommandRegistry.TryGet(0x06, 0x93, out ZvtCommandInfo? initialization)); Assert.Equal("Initialisation", initialization!.Name);
        Assert.Equal("rfu", ZvtCommandRegistry.All[0x0611].Status); Assert.Equal("848300", Convert.ToHexString(ZvtResponses.NegativeAck()));
    }

}
