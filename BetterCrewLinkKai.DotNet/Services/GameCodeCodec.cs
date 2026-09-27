using System.Text;

namespace BetterCrewLinkKai.DotNet.Services;

public static class GameCodeCodec
{
    private const string V2Alphabet = "QWXRTYLPESDFGHUJKZOCVBINMA";

    private static readonly int[] V2Map =
    [
        25, 21, 19, 10, 8, 11, 12, 13, 22, 15, 16, 6, 24,
        23, 18, 7, 0, 3, 9, 4, 14, 20, 1, 2, 5, 17
    ];

    public static string IntToGameCode(int input)
    {
        if (input == 0)
        {
            return string.Empty;
        }

        return input <= -1000 ? IntToGameCodeV2(input) : IntToGameCodeV1(input);
    }

    public static int GameCodeToInt(string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return normalized.Length == 4 ? GameCodeToIntV1(normalized) : GameCodeToIntV2(normalized);
    }

    private static string IntToGameCodeV1(int input)
    {
        return Encoding.ASCII.GetString(BitConverter.GetBytes(input)).TrimEnd('\0');
    }

    private static string IntToGameCodeV2(int input)
    {
        var a = input & 0x3ff;
        var b = (input >> 10) & 0xfffff;
        return string.Create(6, (a, b), static (span, state) =>
        {
            span[0] = V2Alphabet[(int)Math.Floor(state.a % 26d)];
            span[1] = V2Alphabet[(int)Math.Floor(state.a / 26d)];
            span[2] = V2Alphabet[(int)Math.Floor(state.b % 26d)];
            span[3] = V2Alphabet[(int)Math.Floor((state.b / 26d) % 26d)];
            span[4] = V2Alphabet[(int)Math.Floor((state.b / 676d) % 26d)];
            span[5] = V2Alphabet[(int)Math.Floor((state.b / 17576d) % 26d)];
        });
    }

    private static int GameCodeToIntV1(string code)
    {
        Span<byte> bytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(code.AsSpan(0, Math.Min(4, code.Length)), bytes);
        return BitConverter.ToInt32(bytes);
    }

    private static int GameCodeToIntV2(string code)
    {
        if (code.Length != 6)
        {
            throw new ArgumentException("Game code must be 4 or 6 characters.", nameof(code));
        }

        var a = V2Map[code[0] - 'A'];
        var b = V2Map[code[1] - 'A'];
        var c = V2Map[code[2] - 'A'];
        var d = V2Map[code[3] - 'A'];
        var e = V2Map[code[4] - 'A'];
        var f = V2Map[code[5] - 'A'];
        var one = (a + (26 * b)) & 0x3ff;
        var two = c + (26 * (d + (26 * (e + (26 * f)))));
        return one | ((two << 10) & unchecked((int)0x3ffffc00)) | unchecked((int)0x80000000);
    }
}
