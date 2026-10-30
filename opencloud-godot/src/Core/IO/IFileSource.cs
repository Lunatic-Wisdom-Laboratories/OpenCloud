using System;
using System.Collections.Generic;

namespace OpenCloud.Core.IO;

/// <summary>
/// One mounted container of game files: a directory, <c>data.zip</c>, a disc image, the retail
/// <c>DATA.HD4</c>/<c>DATA.DAT</c> pair or a PACK archive. Paths are <see cref="VfsPath"/>
/// normalized. Implementations must be safe for concurrent reads.
/// </summary>
public interface IFileSource : IDisposable
{
    /// <summary>Human-readable description for logs.</summary>
    string Description { get; }

    bool Contains(string path);

    /// <summary>Reads the whole file, or returns null when the source does not hold it.</summary>
    byte[]? TryRead(string path);

    IEnumerable<string> EnumerateFiles();
}

/// <summary>Random access to a byte range, such as a file on disk or a file inside a disc image.</summary>
public interface IByteRange
{
    long Length { get; }

    void Read(long offset, Span<byte> destination);
}
