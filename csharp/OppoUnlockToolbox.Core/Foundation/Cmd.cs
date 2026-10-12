

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OppoUnlockToolbox.Core.Foundation;

public static class Cmd
{
    public const int CreateNoWindow = 0x08000000;
    public const int CreateNewConsole = 0x00000010;

    public static string QuoteArgv(IEnumerable<string?> argv)
    {
        var result = new StringBuilder();
        foreach (var arg0 in argv)
        {
            var arg = arg0 ?? "";
            var bsBuf = 0;
            if (result.Length > 0)
                result.Append(' ');
            var needQuote = arg.Contains(' ') || arg.Contains('\t') || arg.Length == 0;
            if (needQuote)
                result.Append('"');
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    bsBuf++;
                }
                else if (c == '"')
                {
                    result.Append('\\', bsBuf * 2);
                    bsBuf = 0;
                    result.Append("\\\"");
                }
                else
                {
                    if (bsBuf > 0)
                    {
                        result.Append('\\', bsBuf);
                        bsBuf = 0;
                    }
                    result.Append(c);
                }
            }
            if (bsBuf > 0)
                result.Append('\\', bsBuf);
            if (needQuote)
            {

                result.Append('\\', bsBuf);
                result.Append('"');
            }
        }
        return result.ToString();
    }
}
