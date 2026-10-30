using System;
using System.Text;

namespace OpenCloud.Core;

/// <summary>
/// Decodes the fixed-size, NUL-terminated names stored in retail records. Names are ASCII in
/// most files but some (frame names, texture names) carry Shift-JIS text.
/// </summary>
public static class GameText
{
    private static readonly Encoding ShiftJis;

    static GameText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShiftJis = Encoding.GetEncoding(932);
    }

    /// <summary>Decodes a NUL-terminated string of at most <paramref name="maxLength"/> bytes.</summary>
    public static string ReadCString(ReadOnlySpan<byte> data, int offset, int maxLength)
    {
        if (offset < 0 || offset >= data.Length)
            return string.Empty;
        var field = data.Slice(offset, Math.Min(maxLength, data.Length - offset));
        int end = field.IndexOf((byte)0);
        if (end >= 0)
            field = field[..end];
        return Decode(field);
    }

    /// <summary>Decodes a NUL-terminated string that runs until the first NUL or the end of the data.</summary>
    public static string ReadCString(ReadOnlySpan<byte> data, int offset) =>
        ReadCString(data, offset, int.MaxValue);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b >= 0x80)
                return ShiftJis.GetString(bytes);
        }
        return Encoding.ASCII.GetString(bytes);
    }
}
