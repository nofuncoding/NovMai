using System;
using System.IO;

namespace NovMai.Rendering;

/// <summary>Minimal random-access byte source consumed by FFmpeg's custom AVIO layer.</summary>
public interface IFFmpegInput : IDisposable
{
    long Length { get; }
    long Position { get; }
    int Read(Span<byte> buffer);
    long Seek(long offset, SeekOrigin origin);
}
