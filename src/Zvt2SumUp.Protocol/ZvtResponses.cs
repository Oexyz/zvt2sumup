namespace Zvt2SumUp.Protocol;

public static class ZvtResponses
{
    public static byte[] Ack() => ZvtCodec.BuildApdu(0x80, 0x00);
    public static byte[] NegativeAck(byte resultCode = ZvtResultCode.FunctionNotPossible) => ZvtCodec.BuildApdu(0x84, resultCode);
    public static byte[] Completion(ReadOnlySpan<byte> data = default) => ZvtCodec.BuildApdu(0x06, 0x0F, data);
    public static byte[] CompletionResult(byte resultCode) => Completion([0x27, resultCode]);
    public static byte[] Abort(byte resultCode, ReadOnlySpan<byte> extra = default)
    {
        byte[] data = new byte[1 + extra.Length]; data[0] = resultCode; extra.CopyTo(data.AsSpan(1));
        return ZvtCodec.BuildApdu(0x06, 0x1E, data);
    }
    public static byte[] StatusInfo(ReadOnlySpan<byte> data) => ZvtCodec.BuildApdu(0x04, 0x0F, data);
    public static byte[] IntermediateStatus(string text)
    {
        byte[] encoded = ZvtCodec.EncodeText(text, 40); byte[] data = new byte[encoded.Length + 2];
        data[0] = 0x24; data[1] = (byte)encoded.Length; encoded.CopyTo(data, 2);
        return ZvtCodec.BuildApdu(0x04, 0xFF, data);
    }
    public static byte[] TransactionStatus(byte resultCode, long? amountCents = null, ReadOnlySpan<byte> currency = default,
        int? traceNumber = null, byte? cardType = null)
    {
        List<byte> data = [0x27, resultCode];
        if (amountCents.HasValue) { data.Add(0x04); data.AddRange(ZvtCodec.IntToBcd(Math.Max(0, amountCents.Value), 6)); }
        if (traceNumber.HasValue) { data.Add(0x0B); data.AddRange(ZvtCodec.IntToBcd(Math.Abs(traceNumber.Value) % 1_000_000, 3)); }
        if (currency.Length >= 2) { data.Add(0x49); data.Add(currency[0]); data.Add(currency[1]); }
        if (cardType.HasValue) { data.Add(0x8A); data.Add(cardType.Value); }
        return [.. data];
    }
    public static byte[] PrintLine(string text, byte attribute = 0)
    {
        byte[] encoded = ZvtCodec.EncodeText(text, 40); byte[] data = new byte[encoded.Length + 1];
        data[0] = attribute; encoded.CopyTo(data, 1); return ZvtCodec.BuildApdu(0x06, 0xD1, data);
    }
    public static byte[] PrintTextBlock(string text)
    {
        byte[] content = ZvtCodec.BerTlv([0x25], ZvtCodec.EncodeText(text, 240));
        byte[] final = ZvtCodec.BerTlv([0x09], [0x01]);
        byte[] combined = [.. content, .. final]; byte[] length = ZvtCodec.EncodeLength(combined.Length);
        return ZvtCodec.BuildApdu(0x06, 0xD3, [0x06, .. length, .. combined]);
    }
    public static IReadOnlyList<byte[]> PrintReceipt(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<byte[]> result = [];
        foreach (string source in lines)
        {
            string line = source ?? string.Empty;
            if (line.Length == 0) { result.Add(PrintLine(string.Empty)); continue; }
            for (int index = 0; index < line.Length; index += 40) result.Add(PrintLine(line.Substring(index, Math.Min(40, line.Length - index))));
        }
        return result;
    }
}
