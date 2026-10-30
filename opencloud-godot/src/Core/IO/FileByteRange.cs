using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace OpenCloud.Core.IO;

/// <summary>
/// A file on disk read with positional I/O, so any number of threads can read at once without
/// sharing a stream position.
/// </summary>
public sealed class FileByteRange : IByteRange, IDisposable
{
    private readonly SafeFileHandle _handle;

    public FileByteRange(string path)
    {
        Path = path;
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public string Path { get; }

    public long Length { get; }

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Length)
            throw new EndOfStreamException($"Read of {destination.Length} bytes at {offset} exceeds {Path} ({Length} bytes).");
        while (destination.Length > 0)
        {
            int read = RandomAccess.Read(_handle, destination, offset);
            if (read <= 0)
                throw new EndOfStreamException($"Unexpected end of {Path}.");
            destination = destination[read..];
            offset += read;
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>A window into another byte range, such as one file stored inside a disc image.</summary>
public sealed class SubByteRange : IByteRange
{
    private readonly IByteRange _parent;
    private readonly long _start;

    public SubByteRange(IByteRange parent, long start, long length)
    {
        if (start < 0 || length < 0 || start + length > parent.Length)
            throw new ArgumentOutOfRangeException(nameof(start), "Sub-range lies outside its parent.");
        _parent = parent;
        _start = start;
        Length = length;
    }

    public long Length { get; }

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Length)
            throw new EndOfStreamException("Read outside sub-range.");
        _parent.Read(_start + offset, destination);
    }
}
