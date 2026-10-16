using System.Diagnostics;

namespace SwarmCore.Abstractions.Networking;

/// <summary>
/// Recognizes an unsolicited plaintext 421 refusal on an implicit-TLS mail port.
/// Inspect only the four-byte status prefix, including fragmented reads. Never
/// retain server text, inspect decrypted traffic, or permit plaintext fallback.
/// All other bytes pass unchanged to the TLS implementation and its validation.
/// </summary>
public sealed class MailTlsGreetingStream(Stream inner) : Stream
{
    private int _prefixLength;
    private bool _inspect = true;

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        if (!_inspect) return;
        foreach (var value in bytes)
        {
            if (_prefixLength == 3)
            {
                _inspect = false;
                if (value is (byte)' ' or (byte)'-') throw new MailServerBusyException();
                return;
            }
            if (value != "421"u8[_prefixLength++])
            {
                _inspect = false;
                return;
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Inspect(buffer[..read]);
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    [DebuggerDisableUserUnhandledExceptions]
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            Inspect(buffer.Span[..read]);
            return read;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is part of the mail connection timeout/shutdown
            // protocol. Catch it at this user-code boundary so .NET's async
            // user-unhandled debugger support can honor the attribute above,
            // then preserve the cancellation for the owning connection stage.
            throw;
        }
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override bool CanTimeout => inner.CanTimeout;
    public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
    public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
