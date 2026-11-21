using System;
using System.IO;
using Godot;
using FileAccess = Godot.FileAccess;

namespace NovMai.Rendering;

/// <summary>
/// <see cref="IFFmpegInput"/> backed by <see cref="Godot.FileAccess"/>.
/// This makes FFmpeg work with <c>res://</c> PCK-packed files, <c>user://</c>
/// files and absolute operating system paths exactly like Godot's own loaders.
/// </summary>
public sealed class GodotFileInput : IFFmpegInput
{
    private readonly FileAccess _file;
    private bool _disposed;

    public GodotFileInput(FileAccess file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    public long Length => (long)_file.GetLength();

    public long Position => (long)_file.GetPosition();

    public int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var chunk = _file.GetBuffer(buffer.Length);
        if (chunk is null || chunk.Length == 0)
        {
            return 0;
        }

        var count = Math.Min(chunk.Length, buffer.Length);
        chunk.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    public long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => -1,
        };
        if (target < 0)
        {
            return -1;
        }

        _file.Seek((ulong)target);
        return (long)_file.GetPosition();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _file.Dispose();
    }
}
