using System.IO;
using System.IO.Compression;
using System.Text;

namespace VRCX_Image_Discord_Uploader.Core;

/// <summary>Minimal reader for PNG tEXt / zTXt / iTXt chunks (where VRCX and VRChat store metadata).</summary>
public static class PngText
{
    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static List<(string Keyword, string Value)> ReadAll(Stream s)
    {
        var result = new List<(string, string)>();
        var sig = new byte[8];
        if (s.Read(sig, 0, 8) != 8 || !sig.SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG file");

        var head = new byte[8];
        while (ReadExact(s, head, 8))
        {
            int len = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
            string type = Encoding.ASCII.GetString(head, 4, 4);
            if (len < 0) break;

            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                var data = new byte[len];
                if (!ReadExact(s, data, len)) break;
                var parsed = Parse(type, data);
                if (parsed != null) result.Add(parsed.Value);
                s.Seek(4, SeekOrigin.Current); // CRC
            }
            else if (type == "IEND") break;
            else s.Seek(len + 4L, SeekOrigin.Current); // skip image data + CRC
        }
        return result;
    }

    static (string, string)? Parse(string type, byte[] d)
    {
        int z = Array.IndexOf(d, (byte)0);
        if (z <= 0) return null;
        string key = Encoding.Latin1.GetString(d, 0, z);
        int p = z + 1;

        switch (type)
        {
            case "tEXt":
                return (key, Encoding.Latin1.GetString(d, p, d.Length - p));
            case "zTXt":
                return (key, Encoding.Latin1.GetString(Inflate(d, p + 1)));
            case "iTXt":
                if (p + 2 > d.Length) return null;
                bool compressed = d[p] == 1;
                p += 2; // compression flag + method
                int langEnd = Array.IndexOf(d, (byte)0, p); if (langEnd < 0) return null;
                int transEnd = Array.IndexOf(d, (byte)0, langEnd + 1); if (transEnd < 0) return null;
                p = transEnd + 1;
                var bytes = compressed ? Inflate(d, p) : d[p..];
                return (key, Encoding.UTF8.GetString(bytes));
        }
        return null;
    }

    static byte[] Inflate(byte[] d, int offset)
    {
        using var input = new MemoryStream(d, offset, d.Length - offset);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    static bool ReadExact(Stream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
