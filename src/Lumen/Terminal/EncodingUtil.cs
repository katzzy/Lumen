using System.Text;

namespace Lumen.Terminal;

public static class EncodingUtil
{
    static EncodingUtil()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // ignore
        }
    }

    public static Encoding Resolve(string name)
    {
        var value = (name ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(false),
            "gbk" or "gb2312" or "gb18030" or "cp936" or "936" => GetCodePage(936),
            "ascii" or "us-ascii" => Encoding.ASCII,
            "latin1" or "iso-8859-1" => GetCodePage(28591),
            _ => new UTF8Encoding(false)
        };
    }

    private static Encoding GetCodePage(int codepage)
    {
        try
        {
            return Encoding.GetEncoding(codepage);
        }
        catch
        {
            return new UTF8Encoding(false);
        }
    }
}
