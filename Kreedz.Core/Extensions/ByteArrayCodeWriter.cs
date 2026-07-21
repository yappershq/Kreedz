using System.Collections.Generic;
using Iced.Intel;

namespace Kreedz.Extensions;

internal sealed class ByteArrayCodeWriter : CodeWriter
{
    private readonly List<byte> _bytes = [];

    public override void WriteByte(byte value) => _bytes.Add(value);

    public byte[] ToArray() => [.. _bytes];
}
