using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// The LZ77 variant of AC1021 (AutoCAD 2007) file headers and pages. Ported from the MIT-licensed ACadSharp (DwgLZ77AC21Decompressor; see
/// THIRD_PARTY_NOTICES.md), with every read and write bounds-checked. The stream is a literal run, then instructions that copy from earlier output
/// (each carrying the length of the literal run that follows it). Literal runs are not copied in order: they are cut into pieces of 1 to 16 bytes (a table
/// per run length of 1 to 31 bytes, chunks of 32 beyond that) whose order is reversed, and pieces of 2 and 3 bytes are byte-reversed.
/// </summary>
internal static class DwgLz21
{
    /// <summary>Per literal run length 0 to 31, the pieces as (size, source offset, destination offset) triples.</summary>
    private static readonly int[][] Pieces =
    [
        // 0
        [],
        // 1
        [1, 0, 0],
        // 2
        [2, 0, 0],
        // 3
        [3, 0, 0],
        // 4
        [4, 0, 0],
        // 5
        [1, 4, 0, 4, 0, 1],
        // 6
        [1, 5, 0, 4, 1, 1, 1, 0, 5],
        // 7
        [2, 5, 0, 4, 1, 2, 1, 0, 6],
        // 8
        [8, 0, 0],
        // 9
        [1, 8, 0, 8, 0, 1],
        // 10
        [1, 9, 0, 8, 1, 1, 1, 0, 9],
        // 11
        [2, 9, 0, 8, 1, 2, 1, 0, 10],
        // 12
        [4, 8, 0, 8, 0, 4],
        // 13
        [1, 12, 0, 4, 8, 1, 8, 0, 5],
        // 14
        [1, 13, 0, 4, 9, 1, 8, 1, 5, 1, 0, 13],
        // 15
        [2, 13, 0, 4, 9, 2, 8, 1, 6, 1, 0, 14],
        // 16
        [16, 0, 0],
        // 17
        [8, 9, 0, 1, 8, 8, 8, 0, 9],
        // 18
        [1, 17, 0, 16, 1, 1, 1, 0, 17],
        // 19
        [3, 16, 0, 16, 0, 3],
        // 20
        [4, 16, 0, 8, 8, 4, 8, 0, 12],
        // 21
        [1, 20, 0, 4, 16, 1, 8, 8, 5, 8, 0, 13],
        // 22
        [2, 20, 0, 4, 16, 2, 8, 8, 6, 8, 0, 14],
        // 23
        [3, 20, 0, 4, 16, 3, 8, 8, 7, 8, 0, 15],
        // 24
        [8, 16, 0, 16, 0, 8],
        // 25
        [8, 17, 0, 1, 16, 8, 16, 0, 9],
        // 26
        [1, 25, 0, 8, 17, 1, 1, 16, 9, 16, 0, 10],
        // 27
        [2, 25, 0, 8, 17, 2, 1, 16, 10, 16, 0, 11],
        // 28
        [4, 24, 0, 8, 16, 4, 8, 8, 12, 8, 0, 20],
        // 29
        [1, 28, 0, 4, 24, 1, 8, 16, 5, 8, 8, 13, 8, 0, 21],
        // 30
        [2, 28, 0, 4, 24, 2, 8, 16, 6, 8, 8, 14, 8, 0, 22],
        // 31
        [1, 30, 0, 4, 26, 1, 8, 18, 5, 8, 10, 13, 8, 2, 21, 2, 0, 29],
    ];

    private static void Fail() => throw new ModelFormatException("A compressed DWG page is damaged.");

    private static void Piece(ReadOnlySpan<byte> source, int s, Span<byte> destination, int d, int size)
    {
        var from = source.Slice(s, size);
        var to = destination.Slice(d, size);
        if (size is 2 or 3)
        {
            for (var i = 0; i < size; i++)
            {
                to[i] = from[size - 1 - i];
            }
        }
        else if (size == 16)
        {
            from[8..].CopyTo(to);
            from[..8].CopyTo(to[8..]);
        }
        else
        {
            from.CopyTo(to);
        }
    }

    /// <summary>Copies a literal run of <paramref name="length"/> bytes; the caller has checked that both ranges are in bounds.</summary>
    private static void Literal(ReadOnlySpan<byte> source, int s, Span<byte> destination, int d, int length)
    {
        while (length >= 32)
        {
            for (var block = 0; block < 4; block++)
            {
                source.Slice(s + (24 - block * 8), 8).CopyTo(destination[(d + block * 8)..]);
            }

            s += 32;
            d += 32;
            length -= 32;
        }

        if (length == 0)
        {
            return;
        }

        var pieces = Pieces[length];
        for (var i = 0; i < pieces.Length; i += 3)
        {
            Piece(source, s + pieces[i + 1], destination, d + pieces[i + 2], pieces[i]);
        }
    }

    /// <summary>Decompresses <paramref name="source"/> (its compressed length) into <paramref name="destination"/>; returns the bytes written.</summary>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, CancellationToken cancel = default) => new Decoder(source, destination).Run(cancel);

    private ref struct Decoder
    {
        private readonly ReadOnlySpan<byte> _source;
        private readonly Span<byte> _destination;
        private int _s;
        private int _d;
        private int _op;
        private int _copy;
        private int _offset;

        public Decoder(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            _source = source;
            _destination = destination;
            _s = 0;
            _d = 0;
            _op = 0;
            _copy = 0;
            _offset = 0;
        }

        private int Next()
        {
            if (_s >= _source.Length)
            {
                Fail();
            }

            return _source[_s++];
        }

        public int Run(CancellationToken cancel)
        {
            var end = _source.Length;
            if (end == 0)
            {
                return 0;
            }

            _op = Next();
            var length = 0;
            if (_s >= end)
            {
                return 0;
            }

            if ((_op & 0xF0) == 0x20)
            {
                _s += 2;
                length = Next() & 7;
            }

            var tick = 0L;
            while (_s < end)
            {
                Cancel.Every(cancel, tick++);
                if (length == 0)
                {
                    length = _op + 8;
                    if (length == 0x17)
                    {
                        var n = Next();
                        length += n;
                        if (n == 0xFF)
                        {
                            do
                            {
                                n = Next();
                                n |= Next() << 8;
                                length += n;
                                if (length > end)
                                {
                                    Fail();
                                }
                            }
                            while (n == 0xFFFF);
                        }
                    }
                }

                if (_s + length > end || _d + length > _destination.Length)
                {
                    Fail();
                }

                Literal(_source, _s, _destination, _d, length);
                _s += length;
                _d += length;
                if (_s >= end)
                {
                    break;
                }

                _op = Next();
                Instruction();
                while (true)
                {
                    if (_offset > _d || _d + _copy > _destination.Length)
                    {
                        Fail();
                    }

                    for (var i = 0; i < _copy; i++)
                    {
                        _destination[_d] = _destination[_d - _offset];
                        _d++;
                    }

                    length = _op & 7;
                    if (length != 0 || _s >= end)
                    {
                        break;
                    }

                    _op = Next();
                    if (_op >> 4 == 0)
                    {
                        break;
                    }

                    if (_op >> 4 == 15)
                    {
                        _op &= 15;
                    }

                    Instruction();
                }
            }

            return _d;
        }

        private void Instruction()
        {
            switch (_op >> 4)
            {
                case 0:
                    _copy = (_op & 0xF) + 0x13;
                    _offset = Next();
                    _op = Next();
                    _copy += (_op >> 3) & 0x10;
                    _offset += ((_op & 0x78) << 5) + 1;
                    break;
                case 1:
                    _copy = (_op & 0xF) + 3;
                    _offset = Next();
                    _op = Next();
                    _offset += ((_op & 0xF8) << 5) + 1;
                    break;
                case 2:
                    _offset = Next();
                    _offset |= Next() << 8;
                    _copy = _op & 7;
                    if ((_op & 8) == 0)
                    {
                        _op = Next();
                        _copy += _op & 0xF8;
                    }
                    else
                    {
                        _offset++;
                        _copy += Next() << 3;
                        _op = Next();
                        _copy += ((_op & 0xF8) << 8) + 0x100;
                    }

                    break;
                default:
                    _copy = _op >> 4;
                    _offset = _op & 15;
                    _op = Next();
                    _offset += ((_op & 0xF8) << 1) + 1;
                    break;
            }
        }
    }
}
