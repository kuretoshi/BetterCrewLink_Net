using System.Text;

namespace TanukiBCL.VoiceProbe;

// Records only public DTLS header fields for bounded stall diagnostics. No
// packet body, network endpoint, certificate, key, or audio is retained.
internal static class DtlsRecordHeaderTrace
{
    public static string Describe(ReadOnlySpan<byte> datagram)
    {
        var description = new StringBuilder();
        var offset = 0;
        var records = 0;
        while (offset < datagram.Length && records < 8)
        {
            if (datagram.Length - offset < 13)
            {
                AppendSeparator(description);
                description.Append("short");
                break;
            }

            var contentType = datagram[offset];
            var epoch = datagram[offset + 3] << 8 | datagram[offset + 4];
            ulong recordSequence = 0;
            for (var index = 5; index <= 10; index++)
                recordSequence = recordSequence << 8 | datagram[offset + index];
            var recordLength = datagram[offset + 11] << 8 | datagram[offset + 12];
            AppendSeparator(description);
            if (recordLength > datagram.Length - offset - 13)
            {
                description.Append($"truncated:{contentType}e{epoch}r{recordSequence}");
                break;
            }

            if (contentType == 22 && epoch == 0 && recordLength >= 12)
            {
                var handshakeType = datagram[offset + 13];
                var messageSequence = datagram[offset + 17] << 8 | datagram[offset + 18];
                description.Append($"H{handshakeType}m{messageSequence}r{recordSequence}");
            }
            else
            {
                var kind = contentType switch { 20 => "C", 21 => "A", 22 => "H*", 23 => "D", _ => "?" };
                description.Append($"{kind}e{epoch}r{recordSequence}");
            }

            offset += 13 + recordLength;
            records++;
        }

        if (offset < datagram.Length && records == 8) description.Append("+more");
        return description.ToString();
    }

    private static void AppendSeparator(StringBuilder description)
    {
        if (description.Length > 0) description.Append('+');
    }

    internal static void Verify()
    {
        var hello = new byte[25];
        hello[0] = 22;
        hello[1] = 0xfe;
        hello[2] = 0xfd;
        hello[10] = 2;
        hello[12] = 12;
        hello[13] = 2;
        hello[18] = 1;
        var changeCipher = new byte[14];
        changeCipher[0] = 20;
        changeCipher[1] = 0xfe;
        changeCipher[2] = 0xfd;
        changeCipher[10] = 3;
        changeCipher[12] = 1;
        var combined = hello.Concat(changeCipher).ToArray();
        if (Describe(combined) != "H2m1r2+Ce0r3" ||
            Describe(hello.AsSpan(0, 13)) != "truncated:22e0r2" ||
            Describe([22]) != "short")
            throw new InvalidOperationException("DTLS header-only trace parser failed.");
    }
}
