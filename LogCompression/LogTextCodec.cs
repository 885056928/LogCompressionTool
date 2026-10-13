using System.Text;

namespace LogCompression;

internal static class LogTextCodec
{
    static LogTextCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static (string Content, string EncodingName, bool HasBom) Decode(byte[] bytes, bool preview)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        try { return (DecodeText(encoding, bytes, skip, preview), encoding.WebName, skip > 0); }
        catch (DecoderFallbackException) when (skip == 0)
        {
            // 老设备日志常见 GB18030/GBK，仅在严格 UTF-8 解码失败时使用兼容编码。
            encoding = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return (DecodeText(encoding, bytes, 0, preview), encoding.WebName, false);
        }
    }

    public static byte[] Encode(string text, string name, bool bom)
    {
        var encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var data = encoding.GetBytes(text);
        if (!bom) return data;
        return encoding.GetPreamble().Concat(data).ToArray();
    }

    private static string DecodeText(Encoding encoding, byte[] bytes, int skip, bool preview)
    {
        if (!preview) return encoding.GetString(bytes, skip, bytes.Length - skip);
        // 预览可能停在多字节字符中间，不能将未完成字符误判为其他编码。
        var chars = new char[encoding.GetMaxCharCount(bytes.Length - skip)];
        var count = encoding.GetDecoder().GetChars(bytes, skip, bytes.Length - skip, chars, 0, false);
        return new string(chars, 0, count);
    }
}
