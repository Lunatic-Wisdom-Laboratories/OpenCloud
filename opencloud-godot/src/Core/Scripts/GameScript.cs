using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenCloud.Core.Scripts;

/// <summary>Kind of a script argument, as the interpreter's stack types it.</summary>
public enum ScriptValueKind
{
    Int,
    Float,
    String,
}

/// <summary>One argument of a script command.</summary>
public readonly record struct ScriptValue(ScriptValueKind Kind, int Int, float Float, string? String)
{
    public static ScriptValue FromInt(int value) => new(ScriptValueKind.Int, value, 0, null);
    public static ScriptValue FromFloat(float value) => new(ScriptValueKind.Float, 0, value, null);
    public static ScriptValue FromString(string? value) => new(ScriptValueKind.String, 0, 0, value);

    /// <summary>The value as <c>spiGetStackInt</c> reads it: floats truncate, strings read 0.</summary>
    public int AsInt => Kind switch
    {
        ScriptValueKind.Int => Int,
        ScriptValueKind.Float => (int)Float,
        _ => 0,
    };

    /// <summary>The value as <c>spiGetStackFloat</c> reads it: integers convert, strings read 0.</summary>
    public float AsFloat => Kind switch
    {
        ScriptValueKind.Float => Float,
        ScriptValueKind.Int => Int,
        _ => 0,
    };

    /// <summary>The value as <c>spiGetStackString</c> reads it; null for numbers.</summary>
    public string? AsString => Kind == ScriptValueKind.String ? String : null;

    public override string ToString() => Kind switch
    {
        ScriptValueKind.Int => Int.ToString(CultureInfo.InvariantCulture),
        ScriptValueKind.Float => Float.ToString("0.0###", CultureInfo.InvariantCulture),
        _ => $"\"{String}\"",
    };
}

/// <summary>One tagged command of a script with its arguments.</summary>
public sealed record ScriptCommand(string Tag, IReadOnlyList<ScriptValue> Args)
{
    public ScriptValue Arg(int index) => index < Args.Count ? Args[index] : ScriptValue.FromString(null);
}

/// <summary>
/// Reads the game's configuration scripts (<c>CScriptInterpreter</c>)
/// into a flat list of commands, without running them. Two encodings exist:
/// <list type="bullet">
/// <item>Text: <c>TAG arg, arg, ...;</c>. <c>//</c> and <c>/* */</c> comments are blanked first;
/// a quoted argument is a string, one containing '.' a float, anything else an integer
/// (<c>atoi</c>). Tags start with an upper-case letter.</item>
/// <item>Binary (<c>"BIN\0"</c> header): per command an s16 index into the consumer's tag
/// table (negative ends the script), an s16 argument count, one type byte per argument
/// (1 int, 2 float, 3 string), padding to four bytes, then the values; strings are
/// NUL-terminated and padded to four bytes.</item>
/// </list>
/// Binary scripts store tag indices, so callers pass the tag table of the routine that reads
/// them (for instance <see cref="CharacterInfo.Tags"/>).
/// </summary>
public static class GameScript
{
    public static List<ScriptCommand> Parse(ReadOnlySpan<byte> data, IReadOnlyList<string> binaryTags)
    {
        return data.Length >= 4 && data[0] == 'B' && data[1] == 'I' && data[2] == 'N'
            ? ParseBinary(data, binaryTags)
            : ParseText(data);
    }

    private static List<ScriptCommand> ParseBinary(ReadOnlySpan<byte> data, IReadOnlyList<string> tags)
    {
        var commands = new List<ScriptCommand>();
        int position = 4;
        while (position + 2 <= data.Length)
        {
            int index = LittleEndian.I16(data, position);
            position += 2;
            if (index < 0)
                break;
            if (position + 2 > data.Length)
                break;

            int count = LittleEndian.I16(data, position);
            position += 2;
            var args = new List<ScriptValue>(Math.Max(count, 0));
            if (count > 0)
            {
                if (position + count > data.Length)
                    break;
                var types = data.Slice(position, count).ToArray();
                position += count;
                position = (position + 3) & ~3;

                foreach (byte type in types)
                {
                    if (type == 3)
                    {
                        string text = GameText.ReadCString(data, position);
                        int end = data[position..].IndexOf((byte)0);
                        int length = (end < 0 ? data.Length - position : end) + 1;
                        position += (length + 3) & ~3;
                        args.Add(ScriptValue.FromString(text));
                    }
                    else
                    {
                        if (position + 4 > data.Length)
                            return commands;
                        args.Add(type == 2
                            ? ScriptValue.FromFloat(LittleEndian.F32(data, position))
                            : ScriptValue.FromInt(LittleEndian.I32(data, position)));
                        position += 4;
                    }
                }
            }
            // An index outside the table runs nothing, as in GetNextTAG.
            if (index < tags.Count)
                commands.Add(new ScriptCommand(tags[index], args));
        }
        return commands;
    }

    private static List<ScriptCommand> ParseText(ReadOnlySpan<byte> source)
    {
        var text = StripComments(source.ToArray());
        var commands = new List<ScriptCommand>();
        int position = 0;

        while (true)
        {
            SkipSpace(text, ref position);
            if (position >= text.Length)
                break;

            int start = position;
            while (position < text.Length && !IsSpace(text[position]) && text[position] != ';')
                position++;
            string tag = Encoding.ASCII.GetString(text, start, position - start);

            var args = ReadArgs(text, ref position);
            // SearchCommand rejects words not starting with an upper-case letter; the
            // interpreter then skips to the next ';'.
            if (tag.Length > 0 && tag[0] is >= 'A' and <= 'Z')
                commands.Add(new ScriptCommand(tag, args));
        }
        return commands;
    }

    /// <summary>Reads arguments up to and including the ';' that ends the command.</summary>
    private static List<ScriptValue> ReadArgs(byte[] text, ref int position)
    {
        var args = new List<ScriptValue>();
        var token = new List<byte>();
        bool quoted = false, inQuotes = false, any = false;

        void Flush()
        {
            if (!any)
                return;
            args.Add(ToValue(token.ToArray(), quoted));
            token.Clear();
            quoted = false;
            any = false;
        }

        while (position < text.Length)
        {
            byte c = text[position++];
            if (inQuotes)
            {
                if (c == '\\' && position < text.Length && text[position] == '"')
                {
                    token.Add((byte)'"');
                    position++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    token.Add(c);
                    // Shift-JIS lead bytes take the next byte with them.
                    if (c >= 0x80 && c is not (>= 0xA1 and <= 0xDF) && position < text.Length)
                        token.Add(text[position++]);
                }
                continue;
            }

            switch (c)
            {
                case (byte)';':
                    Flush();
                    return args;
                case (byte)',':
                    any = true;
                    Flush();
                    break;
                case (byte)'"':
                    inQuotes = quoted = any = true;
                    break;
                default:
                    if (!IsSpace(c))
                    {
                        token.Add(c);
                        any = true;
                    }
                    break;
            }
        }
        Flush();
        return args;
    }

    private static ScriptValue ToValue(byte[] token, bool quoted)
    {
        if (quoted)
            return ScriptValue.FromString(GameText.Decode(token));

        string text = Encoding.ASCII.GetString(token);
        if (text.Contains('.'))
            return ScriptValue.FromFloat(float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0);
        return ScriptValue.FromInt(Atoi(text));
    }

    /// <summary>C <c>atoi</c>: optional sign, then leading digits; anything else reads 0.</summary>
    private static int Atoi(string text)
    {
        int i = 0, sign = 1;
        long value = 0;
        if (i < text.Length && (text[i] == '-' || text[i] == '+'))
            sign = text[i++] == '-' ? -1 : 1;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            value = Math.Min(value * 10 + (text[i++] - '0'), int.MaxValue);
        return (int)(value * sign);
    }

    private static byte[] StripComments(byte[] text)
    {
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '/' && text[i + 1] == '/')
            {
                for (; i < text.Length && text[i] != '\n' && text[i] != '\r'; i++)
                    text[i] = (byte)' ';
            }
            else if (text[i] == '/' && text[i + 1] == '*')
            {
                for (; i < text.Length; i++)
                {
                    if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        text[i] = text[i + 1] = (byte)' ';
                        i++;
                        break;
                    }
                    text[i] = (byte)' ';
                }
            }
        }
        return text;
    }

    private static void SkipSpace(byte[] text, ref int position)
    {
        while (position < text.Length && (IsSpace(text[position]) || text[position] == 0))
            position++;
    }

    private static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';
}
