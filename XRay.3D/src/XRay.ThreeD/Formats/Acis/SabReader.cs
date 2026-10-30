using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Acis;

/// <summary>
/// ACIS SAB (binary save files, "ACIS BinaryFile" and the same layout with the "ASM BinaryFile4" signature): a header of four integers,
/// three counted strings and three doubles, then records made of tagged tokens. The tokens are turned into the text form's tokens and the
/// SAT reader builds the model from them. Booleans stand for forward/reversed senses; the transform is stored as a text literal.
/// </summary>
internal static class SabReader
{
    private static readonly byte[] AcisSignature = "ACIS BinaryFile"u8.ToArray();
    private static readonly byte[] AsmSignature = "ASM BinaryFile4"u8.ToArray();

    public static bool Sniff(ReadOnlySpan<byte> head) => head.StartsWith(AcisSignature) || head.StartsWith(AsmSignature);

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        var p = 15;
        if (span.Length < 40)
        {
            throw new ModelFormatException("The ACIS binary file is too short.");
        }

        int Int()
        {
            if (p + 4 > data.Length)
            {
                throw new ModelFormatException("The ACIS binary file is truncated.");
            }

            var v = BinaryPrimitives.ReadInt32LittleEndian(data.Span.Slice(p, 4));
            p += 4;
            return v;
        }

        var version = Int();
        Int();
        Int();
        Int();
        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Acis;
        meta.DocumentKind = "model";
        meta.UpAxis = UpAxis.Z;
        meta.FormatVersion = "ACIS save version " + version.ToString(CultureInfo.InvariantCulture) + " (SAB)";

        string Str()
        {
            if (p + 2 > data.Length || data.Span[p] != 0x07)
            {
                throw new ModelFormatException("An ACIS binary header string is missing.");
            }

            var n = data.Span[p + 1];
            if (p + 2 + n > data.Length)
            {
                throw new ModelFormatException("The ACIS binary file is truncated.");
            }

            var s = Encoding.Latin1.GetString(data.Span.Slice(p + 2, n));
            p += 2 + n;
            return s;
        }

        var producer = Str();
        var product = Str();
        var date = Str();
        meta.Application = producer + " " + product;
        meta.Properties.Add(new ModelProperty("Saved", date));
        var numbers = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (p + 9 > span.Length || span[p] != 0x06)
            {
                throw new ModelFormatException("The ACIS binary header units are missing.");
            }

            numbers[i] = BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(p + 1, 8));
            p += 9;
        }

        meta.Units = numbers[0] switch
        {
            1 => LengthUnit.Millimeter,
            10 => LengthUnit.Centimeter,
            1000 => LengthUnit.Meter,
            25.4 => LengthUnit.Inch,
            304.8 => LengthUnit.Foot,
            _ => null,
        };
        meta.Properties.Add(new ModelProperty("Millimetres per model unit", numbers[0].ToString("R", CultureInfo.InvariantCulture)));

        var records = new List<SatRecord>();
        var tokens = new List<string>();
        var name = new StringBuilder();
        long tick = 0;
        while (p < span.Length)
        {
            Cancel.Every(options.Cancellation, tick++);
            var tag = span[p++];
            switch (tag)
            {
                case 0x0D:
                case 0x0E:
                {
                    if (p >= span.Length || p + 1 + span[p] > span.Length)
                    {
                        throw new ModelFormatException("An ACIS binary identifier is truncated.");
                    }

                    var n = span[p];
                    var part = Encoding.Latin1.GetString(span.Slice(p + 1, n));
                    p += 1 + n;
                    if (tokens.Count == 0)
                    {
                        // The entity name: parts joined with '-' until the last one (tag 0x0D).
                        if (name.Length > 0)
                        {
                            name.Append('-');
                        }

                        name.Append(part);
                        if (tag == 0x0D)
                        {
                            tokens.Add(name.ToString());
                            name.Clear();
                        }
                    }
                    else
                    {
                        tokens.Add(part);
                    }

                    break;
                }

                case 0x11:
                {
                    if (tokens.Count > 0)
                    {
                        var type = tokens[0];
                        if (type.StartsWith("End-of-", StringComparison.Ordinal) || type.StartsWith("Begin-of-", StringComparison.Ordinal))
                        {
                            p = span.Length;
                        }
                        else
                        {
                            records.Add(new SatRecord { Index = records.Count, Type = type, Tokens = tokens.Skip(1).ToArray() });
                        }
                    }

                    tokens.Clear();
                    name.Clear();
                    break;
                }

                case 0x04:
                    tokens.Add(Int().ToString(CultureInfo.InvariantCulture));
                    break;
                case 0x0C:
                    tokens.Add("$" + Int().ToString(CultureInfo.InvariantCulture));
                    break;
                case 0x06:
                    tokens.Add(ReadDouble(span, ref p));
                    break;
                case 0x13:
                case 0x14:
                    tokens.Add(ReadDouble(span, ref p));
                    tokens.Add(ReadDouble(span, ref p));
                    tokens.Add(ReadDouble(span, ref p));
                    break;
                case 0x0A:
                    tokens.Add("reversed");
                    break;
                case 0x0B:
                    tokens.Add("forward");
                    break;
                case 0x0F:
                    tokens.Add("{");
                    break;
                case 0x10:
                    tokens.Add("}");
                    break;
                case 0x07:
                case 0x08:
                case 0x09:
                case 0x12:
                {
                    var width = tag == 0x07 ? 1 : tag == 0x08 ? 2 : 4;
                    if (p + width > span.Length)
                    {
                        throw new ModelFormatException("An ACIS binary string is truncated.");
                    }

                    long n = width == 1 ? span[p] : width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(p, 2)) : BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4));
                    p += width;
                    if (n > span.Length - p)
                    {
                        throw new ModelFormatException("An ACIS binary string is truncated.");
                    }

                    var text = Encoding.Latin1.GetString(span.Slice(p, (int)n));
                    p += (int)n;
                    if (tag == 0x12)
                    {
                        tokens.AddRange(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    }
                    else
                    {
                        tokens.Add("@" + text);
                    }

                    break;
                }

                case 0x05:
                    if (p + 4 > span.Length)
                    {
                        throw new ModelFormatException("The ACIS binary file is truncated.");
                    }

                    tokens.Add(BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p, 4)).ToString("R", CultureInfo.InvariantCulture));
                    p += 4;
                    break;
                case 0x01:
                case 0x02:
                    tokens.Add(span[p++].ToString(CultureInfo.InvariantCulture));
                    break;
                case 0x03:
                    if (p + 2 > span.Length)
                    {
                        throw new ModelFormatException("The ACIS binary file is truncated.");
                    }

                    tokens.Add(BinaryPrimitives.ReadInt16LittleEndian(span.Slice(p, 2)).ToString(CultureInfo.InvariantCulture));
                    p += 2;
                    break;
                default:
                    throw new ModelFormatException($"Unknown token tag 0x{tag:x2} in the ACIS binary file.");
            }

            if (tokens.Count > 1_000_000 || records.Count > 20_000_000)
            {
                throw new ModelFormatException("The ACIS binary file has an impossibly large record.");
            }
        }

        return SatReader.Build(model, records, version, options);
    }

    private static string ReadDouble(ReadOnlySpan<byte> span, ref int p)
    {
        if (p + 8 > span.Length)
        {
            throw new ModelFormatException("The ACIS binary file is truncated.");
        }

        var v = BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(p, 8));
        p += 8;
        return v.ToString("R", CultureInfo.InvariantCulture);
    }
}
