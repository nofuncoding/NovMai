using System;
using System.IO;
using FFmpeg.AutoGen;

namespace NovMai.Rendering;

/// <summary>
/// Owns a custom <see cref="AVIOContext"/> that reads through
/// <see cref="IFFmpegInput"/>. Keeping this layer separate from
/// <see cref="FFmpegMediaSource"/> is what allows the decoder to run on packed
/// <c>res://</c> files and to be unit tested with a plain <see cref="Stream"/>.
/// </summary>
internal sealed unsafe class FFmpegIoContext : IDisposable
{
    private const int AvSeekSize = 0x10000;
    private const int AvSeekForce = 0x20000;
    private const int BufferSize = 64 * 1024;
    private const int AVERROR_EIO = -5;

    private readonly IFFmpegInput _input;

    // The delegates must stay reachable for as long as AVIOContext holds the
    // native function pointers created from them.
    private readonly avio_alloc_context_read_packet _readDelegate;
    private readonly avio_alloc_context_seek _seekDelegate;

    private AVIOContext* _context;
    private bool _disposed;

    public FFmpegIoContext(IFFmpegInput input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _readDelegate = ReadPacket;
        _seekDelegate = SeekCallback;

        var buffer = (byte*)ffmpeg.av_malloc(BufferSize);
        if (buffer == null)
        {
            throw new OutOfMemoryException("FFmpeg av_malloc failed while allocating the AVIO buffer.");
        }

        _context = ffmpeg.avio_alloc_context(buffer, BufferSize, 0, null, _readDelegate, null, _seekDelegate);
        if (_context == null)
        {
            ffmpeg.av_free(buffer);
            throw new InvalidOperationException("FFmpeg avio_alloc_context failed.");
        }
    }

    public AVIOContext* Context => _context;

    public Exception LastError { get; private set; }

    private int ReadPacket(void* opaque, byte* buffer, int bufferSize)
    {
        try
        {
            if (bufferSize <= 0)
            {
                return 0;
            }

            // FFmpeg 9 expects AVERROR_EOF (not 0) from a synchronous read
            // callback: a plain 0 is treated as "no data yet" and makes the
            // demuxer spin forever at the end of the file.
            var read = _input.Read(new Span<byte>(buffer, bufferSize));
            return read > 0 ? read : ffmpeg.AVERROR_EOF;
        }
        catch (Exception error)
        {
            LastError = error;
            return AVERROR_EIO;
        }
    }

    private long SeekCallback(void* opaque, long offset, int whence)
    {
        try
        {
            if ((whence & AvSeekSize) != 0)
            {
                return _input.Length;
            }

            var adjusted = whence & ~AvSeekForce;
            var origin = adjusted switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => (SeekOrigin)(-1),
            };
            if ((int)origin < 0)
            {
                return -1;
            }

            return _input.Seek(offset, origin);
        }
        catch (Exception error)
        {
            LastError = error;
            return -1;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_context == null)
        {
            return;
        }

        var context = _context;
        _context = null;

        // libavformat may have replaced the AVIO buffer while probing, so free
        // whatever pointer is current. The buffer is owned by the caller, not
        // by avio_context_free().
        if (context->buffer != null)
        {
            ffmpeg.av_free(context->buffer);
        }
        ffmpeg.avio_context_free(&context);
    }
}
