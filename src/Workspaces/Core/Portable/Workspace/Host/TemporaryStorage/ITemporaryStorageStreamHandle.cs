﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Host;

/// <summary>
/// Represents a handle to data stored to temporary storage (generally a memory mapped file).  As long as this handle is
/// alive, the data should remain in storage and can be readable from any process using the information provided in <see
/// cref="Identifier"/>.  Use <see cref="ITemporaryStorageServiceInternal.WriteToTemporaryStorage(Stream,
/// CancellationToken)"/> to write the data to temporary storage and get a handle to it.  Use <see
/// cref="ReadFromTemporaryStorage"/> to read the data back in any process.
/// </summary>
internal interface ITemporaryStorageStreamHandle
{
    public TemporaryStorageIdentifier Identifier { get; }

    /// <summary>
    /// Reads the data indicated to by this handle into a stream.  This stream can be created in a different process
    /// than the one that wrote the data originally.
    /// </summary>
    Stream ReadFromTemporaryStorage(CancellationToken cancellationToken);
}

internal interface ITemporaryStorageTextHandle
{
    public TemporaryStorageTextIdentifier Identifier { get; }

    SourceText ReadFromTemporaryStorage(CancellationToken cancellationToken);
    Task<SourceText> ReadFromTemporaryStorageAsync(CancellationToken cancellationToken);
}

internal sealed record TemporaryStorageTextIdentifier(
    string Name, long Offset, long Size, SourceHashAlgorithm ChecksumAlgorithm, Encoding? Encoding)
{
    public static TemporaryStorageTextIdentifier ReadFrom(ObjectReader reader)
        => new(
            reader.ReadRequiredString(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            (SourceHashAlgorithm)reader.ReadInt32(),
            reader.ReadEncoding());

    public void WriteTo(ObjectWriter writer)
    {
        writer.WriteString(Name);
        writer.WriteInt64(Offset);
        writer.WriteInt64(Size);
        writer.WriteInt32((int)ChecksumAlgorithm);
        writer.WriteEncoding(Encoding);
    }
}
