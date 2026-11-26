using System.Buffers.Binary;
using System.Text;

namespace Glance.Core.Service;

public enum ReplyStatus : byte { Ok = 0, Error = 1, Denied = 2 }

public sealed record DisplayRequest(DisplayOp Op, byte[] Payload);

/// <summary>
/// The display pipe's wire format. Binary and strict, because the server runs as SYSTEM and the client is a normal user.
/// <para>Request: 1 byte <see cref="DisplayOp"/>, 4 bytes payload length (little endian), payload. Each op has one exact length,
/// checked before the payload is read, so a client cannot make the service allocate more than one frame.</para>
/// <para>Reply: 1 byte <see cref="ReplyStatus"/>, 2 bytes message length (little endian), UTF-8 message (at most
/// <see cref="MaxMessageBytes"/>).</para>
/// </summary>
public static class DisplayWire
{
    public const int Version = 1;
    public const int FrameWidth = 480, FrameHeight = 320, FrameBytes = FrameWidth * FrameHeight * 2;
    public const int HeaderBytes = 5, MaxMessageBytes = 1024;

    /// <summary>The exact payload length of <paramref name="op"/>, or null for an unknown op.</summary>
    public static int? PayloadLength(DisplayOp op) => op switch
    {
        DisplayOp.Hello => 4,
        DisplayOp.Frame => FrameBytes,
        DisplayOp.Brightness => 1,
        DisplayOp.Blank or DisplayOp.Status => 0,
        _ => null,
    };

    public static byte[] EncodeRequest(DisplayOp op, ReadOnlySpan<byte> payload)
    {
        if (PayloadLength(op) is not { } expected) throw new ArgumentException("Unknown request " + op, nameof(op));
        if (payload.Length != expected) throw new ArgumentException($"{op} needs {expected} bytes, not {payload.Length}.", nameof(payload));
        var bytes = new byte[HeaderBytes + payload.Length];
        bytes[0] = (byte)op;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), payload.Length);
        payload.CopyTo(bytes.AsSpan(HeaderBytes));
        return bytes;
    }

    /// <summary>Reads one request. Null when the client closed the pipe cleanly between requests.</summary>
    /// <exception cref="InvalidDataException">An unknown op, a wrong length or a request cut short.</exception>
    public static async Task<DisplayRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderBytes];
        int got = await ReadUpTo(stream, header, ct);
        if (got == 0) return null;
        if (got < HeaderBytes) throw new InvalidDataException("The request was cut short.");
        var op = (DisplayOp)header[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (PayloadLength(op) is not { } expected) throw new InvalidDataException($"Unknown request {header[0]}.");
        if (length != expected) throw new InvalidDataException($"{op} must carry {expected} bytes, not {length}.");
        var payload = new byte[length];
        if (await ReadUpTo(stream, payload, ct) < length) throw new InvalidDataException("The request was cut short.");
        return new DisplayRequest(op, payload);
    }

    public static byte[] EncodeReply(ReplyStatus status, string message)
    {
        var text = Encoding.UTF8.GetBytes(message ?? "");
        int length = Math.Min(text.Length, MaxMessageBytes);
        while (length < text.Length && length > 0 && (text[length] & 0xC0) == 0x80) length--;   // do not cut a character in half
        var bytes = new byte[3 + length];
        bytes[0] = (byte)status;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), (ushort)length);
        Array.Copy(text, 0, bytes, 3, length);
        return bytes;
    }

    /// <exception cref="InvalidDataException">A reply cut short, too long or with an unknown status.</exception>
    public static async Task<(ReplyStatus Status, string Message)> ReadReplyAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[3];
        if (await ReadUpTo(stream, header, ct) < 3) throw new InvalidDataException("The reply was cut short.");
        if (header[0] > (byte)ReplyStatus.Denied) throw new InvalidDataException($"Unknown reply status {header[0]}.");
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
        if (length > MaxMessageBytes) throw new InvalidDataException("The reply is too long.");
        var text = new byte[length];
        if (await ReadUpTo(stream, text, ct) < length) throw new InvalidDataException("The reply was cut short.");
        return ((ReplyStatus)header[0], Encoding.UTF8.GetString(text));
    }

    /// <summary>Fills <paramref name="buffer"/> unless the stream ends first; returns how much was read.</summary>
    private static async Task<int> ReadUpTo(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
