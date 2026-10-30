using System.Buffers.Binary;
using System.Text;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// The DWG bit stream: most significant bit first, with the variable width codes (BS, BL, BD, DD, handles). From AC1021 on, text lives in a second
/// stream at the end of the object (<see cref="Strings"/>), as UTF-16; before that it is inline, one byte per character.
/// </summary>
internal sealed class DwgBits
{
    private readonly byte[] _data;
    private readonly long _end;

    public DwgBits(byte[] data, long startBit, long endBit)
    {
        _data = data;
        Position = startBit;
        _end = Math.Min(endBit, data.Length * 8L);
    }

    public long Position;

    /// <summary>The string stream of an AC1021 or later object, or null (text is inline).</summary>
    public DwgBits? Strings;

    /// <summary>Where the object's handle stream starts, in bits.</summary>
    public long HandleStart;

    public long End => _end;

    public bool Overrun => Position > _end;

    public uint Read(int count)
    {
        uint value = 0;
        while (count > 0)
        {
            var index = Position >> 3;
            var used = (int)(Position & 7);
            var take = Math.Min(8 - used, count);
            uint b = 0;
            if (index < _data.Length && Position < _end)
            {
                b = (uint)((_data[index] >> (8 - used - take)) & ((1 << take) - 1));
            }

            value = (value << take) | b;
            Position += take;
            count -= take;
        }

        return value;
    }

    public bool Bit() => Read(1) != 0;

    public int RC() => (int)Read(8);

    public int RS() => (int)(Read(8) | (Read(8) << 8));

    public uint RL() => Read(8) | (Read(8) << 8) | (Read(8) << 16) | (Read(8) << 24);

    public int BS() => Read(2) switch { 0 => RS(), 1 => RC(), 2 => 0, _ => 256 };

    public int BL() => Read(2) switch { 0 => (int)RL(), 1 => RC(), _ => 0 };

    public long BLL()
    {
        var n = (int)Read(3);
        long v = 0;
        for (var i = 0; i < n; i++)
        {
            v |= (long)RC() << (8 * i);
        }

        return v;
    }

    public double RD()
    {
        Span<byte> b = stackalloc byte[8];
        for (var i = 0; i < 8; i++)
        {
            b[i] = (byte)Read(8);
        }

        return BinaryPrimitives.ReadDoubleLittleEndian(b);
    }

    public double BD() => Read(2) switch { 0 => RD(), 1 => 1.0, _ => 0.0 };

    public double DD(double fallback)
    {
        var code = Read(2);
        if (code == 0)
        {
            return fallback;
        }

        if (code == 3)
        {
            return RD();
        }

        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, fallback);
        if (code == 1)
        {
            for (var i = 0; i < 4; i++)
            {
                b[i] = (byte)Read(8);
            }
        }
        else
        {
            b[4] = (byte)Read(8);
            b[5] = (byte)Read(8);
            for (var i = 0; i < 4; i++)
            {
                b[i] = (byte)Read(8);
            }
        }

        return BinaryPrimitives.ReadDoubleLittleEndian(b);
    }

    public (double X, double Y, double Z) BD3() => (BD(), BD(), BD());

    public (double X, double Y) RD2() => (RD(), RD());

    /// <summary>Thickness: one bit says zero, otherwise a bit double.</summary>
    public double BT() => Bit() ? 0 : BD();

    /// <summary>Extrusion: one bit says (0, 0, 1), otherwise three bit doubles.</summary>
    public (double X, double Y, double Z) BE() => Bit() ? (0, 0, 1) : BD3();

    /// <summary>The object type: a bit short up to AC1018, a two bit code and one or two bytes from AC1024.</summary>
    public int ObjectType(int generation)
    {
        if (generation < 24)
        {
            return BS();
        }

        return Read(2) switch { 0 => RC(), 1 => RC() + 0x1F0, _ => RS() };
    }

    /// <summary>A handle: code (4 bits), byte count (4 bits), the bytes; relative codes are resolved against <paramref name="own"/>.</summary>
    public long Handle(long own)
    {
        var code = (int)Read(4);
        var counter = (int)Read(4);
        long value = 0;
        for (var i = 0; i < counter; i++)
        {
            value = (value << 8) | Read(8);
        }

        return code switch
        {
            6 => own + 1,
            8 => own - 1,
            0xA => own + value,
            0xC => own - value,
            _ => value,
        };
    }

    public string TV()
    {
        if (Strings is not null)
        {
            return Strings.TU();
        }

        var length = BS();
        if (length < 0 || Position + length * 8L > _end)
        {
            Position = _end + 1;
            return "";
        }

        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)Read(8);
        }

        var zero = Array.IndexOf(bytes, (byte)0);
        return Encoding.Latin1.GetString(bytes, 0, zero < 0 ? length : zero);
    }

    /// <summary>A UTF-16 string: bit short length in characters, then two bytes each.</summary>
    public string TU()
    {
        var length = BS();
        if (length < 0 || Position + length * 16L > _end)
        {
            Position = _end + 1;
            return "";
        }

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)RS();
        }

        var zero = Array.IndexOf(chars, '\0');
        return new string(chars, 0, zero < 0 ? length : zero);
    }

    /// <summary>A colour as AC1018 and later write it on an entity: the index with flags, then an RGB value and a transparency when flagged.</summary>
    public (int Index, int Rgb, bool HasBookHandle) EntityColor()
    {
        var v = BS();
        var rgb = -1;
        if ((v & 0x8000) != 0)
        {
            rgb = BL() & 0xFFFFFF;
        }

        if ((v & 0x2000) != 0)
        {
            BL();
        }

        return (v & 0x1FF, rgb, (v & 0x4000) != 0);
    }

    /// <summary>Opens the string stream that ends at <paramref name="end"/> (the start of the handle stream) inside this object, as AC1021 and later lay it out.</summary>
    public void OpenStrings(long end)
    {
        Strings = null;
        if (end - 1 < 0 || end > _end)
        {
            return;
        }

        var probe = new DwgBits(_data, end - 1, _end);
        if (!probe.Bit())
        {
            return;
        }

        var q = end - 1 - 16;
        if (q < 0)
        {
            return;
        }

        var r = new DwgBits(_data, q, _end);
        long size = r.RS();
        if ((size & 0x8000) != 0)
        {
            q -= 16;
            if (q < 0)
            {
                return;
            }

            var hi = new DwgBits(_data, q, _end);
            size = (size & 0x7FFF) | ((long)hi.RS() << 15);
        }

        var start = q - size;
        if (start < 0)
        {
            return;
        }

        Strings = new DwgBits(_data, start, q);
    }
}
