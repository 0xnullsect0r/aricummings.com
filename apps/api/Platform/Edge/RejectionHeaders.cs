using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Connections;

namespace Ari.Platform.Edge;

/// <summary>
/// Kestrel answers malformed requests before any middleware runs, with no hook to add
/// headers. This wraps each connection's transport and injects the security headers into
/// the first write when it is a self-written error response: a status line 4xx/5xx whose
/// write ends the header block and does not already carry the headers (app responses
/// get them from the edge middleware). The block is inserted before the header
/// terminator, never appended after it, so no byte ever lands in a body.
/// </summary>
public static class RejectionHeaders
{
    public static readonly string[] Headers =
    {
        "X-Content-Type-Options: nosniff\r\n",
        "Referrer-Policy: strict-origin-when-cross-origin\r\n",
        "X-Frame-Options: SAMEORIGIN\r\n",
        "Permissions-Policy: camera=(), microphone=(), geolocation=()\r\n",
    };

    private static byte[] BuildBlock()
    {
        var block = new byte[Headers.Sum(h => h.Length)];
        var offset = 0;
        foreach (var header in Headers)
        {
            Encoding.ASCII.GetBytes(header, block.AsSpan(offset));
            offset += header.Length;
        }
        return block;
    }

    public static void Use(IConnectionBuilder builder)
    {
        var block = BuildBlock();
        builder.Use(next => async (ConnectionContext connection) =>
        {
            connection.Transport = new WrappedTransport(connection.Transport, block);
            await next(connection);
        });
    }

    private sealed class WrappedTransport(IDuplexPipe inner, byte[] block) : IDuplexPipe
    {
        public PipeReader Input => inner.Input;
        public PipeWriter Output => new InjectingWriter(inner.Output, block);
    }

    private sealed class InjectingWriter(PipeWriter inner, byte[] block) : PipeWriter
    {
        private Memory<byte> _lastMemory;
        private bool _checked;

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            _lastMemory = inner.GetMemory(sizeHint);
            return _lastMemory;
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            _lastMemory = inner.GetMemory(sizeHint);
            return _lastMemory.Span;
        }

        public override void Advance(int bytes)
        {
            if (!_checked && bytes > 0)
            {
                _checked = true;
                var written = _lastMemory.Span[..bytes];
                if (IsSelfWrittenError(written))
                {
                    // Commit everything except the header terminator's final CRLF,
                    // then the block, then the terminator. All ordering stays inside
                    // the header section and nothing is appended after it.
                    inner.Advance(bytes - 2);
                    var target = inner.GetMemory(block.Length + 2);
                    block.CopyTo(target);
                    target.Span[block.Length] = (byte)'\r';
                    target.Span[block.Length + 1] = (byte)'\n';
                    inner.Advance(block.Length + 2);
                    return;
                }
            }
            inner.Advance(bytes);
        }

        private static bool IsSelfWrittenError(ReadOnlySpan<byte> written)
        {
            if (written.Length < 16) return false;
            var text = Encoding.ASCII.GetString(written[..12]);
            if (!text.StartsWith("HTTP/1.", StringComparison.Ordinal)) return false;
            if (text[9] is not ('4' or '5')) return false;
            // The write must close the header block: last four bytes are CRLF CRLF.
            var last4 = written[^4..];
            if (last4[0] != (byte)'\r' || last4[1] != (byte)'\n' || last4[2] != (byte)'\r' || last4[3] != (byte)'\n') return false;
            // The app's own responses carry these headers already.
            var headerText = Encoding.ASCII.GetString(written);
            return !headerText.Contains("X-Content-Type-Options", StringComparison.OrdinalIgnoreCase);
        }

        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellation = default) => inner.FlushAsync(cancellation);
        public override long UnflushedBytes => inner.UnflushedBytes;
    }
}
