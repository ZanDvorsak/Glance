using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using Glance.Core.Display;

namespace Glance.Core.Service;

/// <summary>A problem reported by the Glance service. <see cref="Denied"/>: another session is using the display.</summary>
public sealed class DisplayServiceException : IOException
{
    public DisplayServiceException(string message, bool denied) : base(message) { Denied = denied; }
    public bool Denied { get; }
}

/// <summary>
/// The display as seen through the Glance service: each <see cref="Blit"/> is a request that returns once the service has
/// sent the frame over USB, so <see cref="DisplayPusher"/> keeps its timing and its USB-error handling unchanged.
/// </summary>
internal sealed class RemoteFrameDisplay : IFrameDisplay
{
    /// <summary>A frame on a stalled display costs about one 3 s USB timeout; leave room for a power blank holding the device too.</summary>
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly NamedPipeClientStream _pipe;
    private readonly TimeSpan _replyTimeout;
    private readonly object _io = new();
    private bool _broken, _disposed;

    private RemoteFrameDisplay(NamedPipeClientStream pipe, TimeSpan replyTimeout) { _pipe = pipe; _replyTimeout = replyTimeout; }

    public int Width => DisplayWire.FrameWidth;
    public int Height => DisplayWire.FrameHeight;

    /// <summary>Connects, checks who serves the pipe and says hello.</summary>
    /// <exception cref="TimeoutException">No service pipe (the service is not running).</exception>
    /// <exception cref="UnauthorizedAccessException">The pipe is not served by SYSTEM or Administrators (or <paramref name="trustedOwners"/>).</exception>
    /// <exception cref="DisplayServiceException">The service refused (another session is using the display) or failed.</exception>
    public static RemoteFrameDisplay Connect(string pipeName, IEnumerable<SecurityIdentifier>? trustedOwners = null, int connectTimeoutMs = 500, TimeSpan? replyTimeout = null)
    {
        var pipe = OpenVerified(pipeName, trustedOwners, connectTimeoutMs);
        var display = new RemoteFrameDisplay(pipe, replyTimeout ?? DefaultReplyTimeout);
        try
        {
            var version = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(version, DisplayWire.Version);
            display.Request(DisplayOp.Hello, version);
            return display;
        }
        catch { display.Dispose(); throw; }
    }

    /// <summary>Asks the service how the display is doing. Null when there is no (trusted) service or it did not answer.</summary>
    public static string? QueryStatus(string pipeName, IEnumerable<SecurityIdentifier>? trustedOwners = null, int connectTimeoutMs = 500)
    {
        try
        {
            using var display = new RemoteFrameDisplay(OpenVerified(pipeName, trustedOwners, connectTimeoutMs), TimeSpan.FromSeconds(3));
            return display.Request(DisplayOp.Status, ReadOnlySpan<byte>.Empty);
        }
        catch { return null; }
    }

    private static NamedPipeClientStream OpenVerified(string pipeName, IEnumerable<SecurityIdentifier>? trustedOwners, int connectTimeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(connectTimeoutMs);              // TimeoutException when no service pipe exists
            PipeServerCheck.Verify(pipe, trustedOwners);  // a squatter is never sent a frame
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    /// <summary>Sends one request and waits for the reply. Returns the message of an OK reply.</summary>
    /// <exception cref="DisplayServiceException">The service answered with an error or a refusal.</exception>
    /// <exception cref="IOException">The connection broke or the service did not answer in time; this object is then unusable.</exception>
    private string Request(DisplayOp op, ReadOnlySpan<byte> payload)
    {
        var bytes = DisplayWire.EncodeRequest(op, payload);
        lock (_io)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_broken) throw new IOException("The connection to the Glance service was lost.");
            using var cts = new CancellationTokenSource(_replyTimeout);
            (ReplyStatus Status, string Message) reply;
            try
            {
                _pipe.WriteAsync(bytes, cts.Token).AsTask().GetAwaiter().GetResult();
                reply = DisplayWire.ReadReplyAsync(_pipe, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { _broken = true; throw new IOException("The Glance service did not answer in time."); }
            catch (InvalidDataException ex) { _broken = true; throw new IOException("The Glance service sent a broken reply: " + ex.Message); }
            catch (IOException) { _broken = true; throw; }
            if (reply.Status == ReplyStatus.Ok) return reply.Message;
            throw new DisplayServiceException(reply.Message, reply.Status == ReplyStatus.Denied);
        }
    }

    public void Blit(int x, int y, int w, int h, byte[] rgb565Be)
    {
        if (x != 0 || y != 0 || w != Width || h != Height || rgb565Be.Length != DisplayWire.FrameBytes)
            throw new ArgumentException($"The display only takes whole {Width}x{Height} frames.");
        Request(DisplayOp.Frame, rgb565Be);
    }

    public void SetBrightness(int level) => Request(DisplayOp.Brightness, new[] { (byte)Math.Clamp(level, 0, 7) });

    /// <summary>Asks the service to turn the display black and release it. Never throws.</summary>
    public void Blank()
    {
        try { Request(DisplayOp.Blank, ReadOnlySpan<byte>.Empty); } catch { /* best effort, like the USB display */ }
    }

    /// <summary>Like <see cref="Blank"/>, but reports failure (for "turn the display off" in the editor).</summary>
    internal void BlankOrThrow() => Request(DisplayOp.Blank, ReadOnlySpan<byte>.Empty);

    public void Dispose()
    {
        lock (_io)
        {
            if (_disposed) return;
            _disposed = true;
            try { _pipe.Dispose(); } catch { /* best effort */ }
        }
    }
}
