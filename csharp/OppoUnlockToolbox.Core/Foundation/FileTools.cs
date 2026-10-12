using System.IO;
using System.Security.Cryptography;

namespace OppoUnlockToolbox.Core.Foundation;

public static class FileTools
{

    public static string Md5File(string path, CancellationToken cancel = default, long limit = 0)
    {
        try
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(path);
            var buffer = new byte[1024 * 1024];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (cancel.IsCancellationRequested)
                    return "";
                if (limit > 0)
                {
                    var remain = limit - total;
                    if (remain <= 0)
                        break;
                    if (read > remain)
                        read = (int)remain;
                }
                md5.TransformBlock(buffer, 0, read, null, 0);
                total += read;
            }
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(md5.Hash ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    public static long? FileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
        }
    }

    public static string FmtSize(object? num)
    {
        double value;
        if (num is null)
            return "—";
        try
        {
            value = Convert.ToDouble(num);
        }
        catch
        {
            return "—";
        }
        string[] units = { "B", "KB", "MB", "GB" };
        for (var i = 0; i < units.Length; i++)
        {
            if (value < 1024 || i == units.Length - 1)
            {
                return i == 0
                    ? string.Format("{0:0} {1}", value, units[i])
                    : string.Format("{0:0.0} {1}", value, units[i]);
            }
            value /= 1024.0;
        }
        return "—";
    }

    public static string FmtMd5(string? md5, int width = 8)
    {
        if (string.IsNullOrEmpty(md5))
            return "—";
        return md5!.Substring(0, Math.Min(width, md5.Length));
    }
}
