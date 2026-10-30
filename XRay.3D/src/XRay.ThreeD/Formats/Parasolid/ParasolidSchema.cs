using System.Globalization;

namespace XRay.ThreeD.Formats.Parasolid;

/// <summary>One field of a node type: its name, its type letter (d int, p pointer, n short, u byte, w unicode unit, c char, l logical, f double, v vector, i interval, h point of an intersection chart, b box) and how often it occurs.</summary>
internal sealed class XtField
{
    public required string Name { get; init; }

    public required char Type { get; init; }

    /// <summary>1 for a scalar, n for a fixed array, 0 for the variable length last field, -1 for a run of logical characters of unknown length.</summary>
    public required int Count { get; init; }

    /// <summary>Where the values are kept in <see cref="XtNode.Ints"/> (integer-like types) or <see cref="XtNode.Doubles"/>; -1 for a variable length field.</summary>
    public required int Slot { get; init; }

    /// <summary>False for a variable length field an embedded schema declares as not transmitted (its elements are absent from the stream).</summary>
    public bool Transmitted { get; init; } = true;

    public bool IsDouble => Type is 'f' or 'v' or 'i' or 'b' or 'h';

    public int Width => Type switch { 'v' or 'h' => 3, 'i' => 2, 'b' => 6, _ => 1 };
}

/// <summary>A node type of the Parasolid transmit format and its fields, in the order a transmit file lists them.</summary>
internal sealed class XtNodeDef
{
    public required int Type { get; init; }

    public required string Name { get; init; }

    public required XtField[] Fields { get; init; }

    public required int IntCount { get; init; }

    public required int DoubleCount { get; init; }

    public XtField? Variable => Fields.Length > 0 && Fields[^1].Count == 0 ? Fields[^1] : null;

    /// <summary>Whether the node is visible at the modeller's interface, which is when a transmit file with user fields appends them.</summary>
    public bool Visible { get; init; }

    private Dictionary<string, XtField>? _byName;

    public XtField Field(string name)
    {
        _byName ??= Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        return _byName[name];
    }
}

/// <summary>
/// The node types and field lists of the transmit format, written from the public XT format reference (Siemens, April 2008: the struct listings of its
/// "Node Types" chapters), which describes the schema of Parasolid 12; files written by Parasolid 10 and 11 differ by a few fields.
/// Where the printed listing and real files disagree the real files win: the ellipse lists its sense before its centre, the transform ends with one
/// unset perspective vector, an edge's second chain is named next_on_curve, an intersection curve lists its two surfaces before its chart. The 10.0 layout was checked on a real SolidWorks 98Plus file.
/// </summary>
internal static class XtSchema
{
    private const string Spec = """
10 ASSEMBLY highest_node_id:d attributes_groups:p attribute_chains:p list:p surface:p curve:p point:p key:p res_size:f res_linear:f ref_instance:p next:p previous:p state:u owner:p type:u sub_instance:p
11 INSTANCE node_id:d attributes_groups:p type:u part:p transform:p assembly:p next_in_part:p prev_in_part:p next_of_part:p prev_of_part:p
12 BODY highest_node_id:d attributes_groups:p attribute_chains:p surface:p curve:p point:p key:p res_size:f res_linear:f ref_instance:p next:p previous:p state:u owner:p body_type:u nom_geom_state:u shell:p boundary_surface:p boundary_curve:p boundary_point:p region:p edge:p vertex:p
13 SHELL node_id:d attributes_groups:p body:p next:p face:p edge:p vertex:p region:p front_face:p
14 FACE node_id:d attributes_groups:p tolerance:f next:p previous:p loop:p shell:p surface:p sense:c next_on_surface:p previous_on_surface:p next_front:p previous_front:p front_shell:p
15 LOOP node_id:d attributes_groups:p fin:p face:p next:p
16 EDGE node_id:d attributes_groups:p tolerance:f fin:p previous:p next:p curve:p next_on_curve:p previous_on_curve:p owner:p
17 FIN attributes_groups:p loop:p forward:p backward:p vertex:p other:p edge:p curve:p next_at_vx:p sense:c
18 VERTEX node_id:d attributes_groups:p fin:p previous:p next:p point:p tolerance:f owner:p
19 REGION node_id:d attributes_groups:p body:p next:p previous:p shell:p type:c
29 POINT node_id:d attributes_groups:p owner:p next:p previous:p pvec:v
30 LINE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c pvec:v direction:v
31 CIRCLE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c centre:v normal:v x_axis:v radius:f
32 ELLIPSE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c centre:v normal:v x_axis:v major_radius:f minor_radius:f
38 INTERSECTION node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c surface:p[2] chart:p start:p end:p
40 CHART Base_parameter:f Base_scale:f Chart_count:d Chordal_error:f Angular_error:f parameter_error:f[2] hvec:h*
41 LIMIT type:c hvec:h*
45 BSPLINE_VERTICES vertices:f*
50 PLANE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c pvec:v normal:v x_axis:v
51 CYLINDER node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c pvec:v axis:v radius:f x_axis:v
52 CONE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c pvec:v axis:v radius:f sin_half_angle:f cos_half_angle:f x_axis:v
53 SPHERE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c centre:v radius:f axis:v x_axis:v
54 TORUS node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c centre:v axis:v major_radius:f minor_radius:f x_axis:v
56 BLENDED_EDGE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c blend_type:c surface:p[2] spine:p range:f[2] thumb_weight:f[2] boundary:p[2] start:p end:p
59 BLEND_BOUND node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c boundary:n blend:p
60 OFFSET_SURF node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c check:c true_offset:l surface:p offset:f scale:f
67 SWEPT_SURF node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c section:p sweep:v scale:f
68 SPUN_SURF node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c profile:p base:v axis:v start:v end:v start_param:f end_param:f x_axis:v scale:f
70 LIST node_id:d owner:p next:p previous:p list_type:d list_length:d block_length:d size_of_entry:d list_block:p finger_block:p finger_index:d notransmit:l
74 POINTER_LIS_BLOCK n_entries:d next_block:p entries:p*
79 ATT_DEF_ID String:c*
80 ATTRIB_DEF next:p identifier:p type_id:d actions:u[8] field_names:p legal_owners:l[*] fields:u*
81 ATTRIBUTE node_id:d definition:p owner:p next:p previous:p next_of_type:p previous_of_type:p fields:p*
82 INT_VALUES values:d*
83 REAL_VALUES values:f*
84 CHAR_VALUES values:c*
85 POINT_VALUES values:v*
86 VECTOR_VALUES values:v*
87 AXIS_VALUES values:v*
88 TAG_VALUES values:t*
89 DIRECTION_VALUES values:v*
90 GROUP node_id:d attributes_groups:p owner:p next:p previous:p type:u first_member:p
91 MEMBER_OF_GROUP dummy_node_id:d owning_group:p owner:p next:p previous:p next_member:p previous_member:p
98 UNICODE_VALUES values:w*
99 FIELD_NAMES names:w*
100 TRANSFORM node_id:d owner:p next:p previous:p rotation:f[9] translation:v scale:f flag:d perspective:v
101 WORLD assembly:p attribute:p body:p transform:p surface:p curve:p point:p alive:l attrib_def:p highest_id:d current_id:d
102 KEY string:c*
120 PE_SURF node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c type:c data:p tf:p
121 INT_PE_DATA geom_type:d real_array:p int_array:p
122 EXT_PE_DATA key:p real_array:p int_array:p
124 B_SURFACE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c nurbs:p data:p
125 SURFACE_DATA original_uint:i original_vint:i extended_uint:i extended_vint:i self_int:u original_u_start:c original_u_end:c original_v_start:c original_v_end:c extended_u_start:c extended_u_end:c extended_v_start:c extended_v_end:c analytic_form_type:c swept_form_type:c spun_form_type:c blend_form_type:c analytic_form:p swept_form:p spun_form:p blend_form:p
126 NURBS_SURF u_periodic:l v_periodic:l u_degree:n v_degree:n n_u_vertices:d n_v_vertices:d u_knot_type:u v_knot_type:u n_u_knots:d n_v_knots:d rational:l u_closed:l v_closed:l surface_form:u vertex_dim:n bspline_vertices:p u_knot_mult:p v_knot_mult:p u_knots:p v_knots:p
127 KNOT_MULT mult:n*
128 KNOT_SET knots:f*
130 PE_CURVE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c type:c data:p tf:p
133 TRIMMED_CURVE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c basis_curve:p point_1:v point_2:v parm_1:f parm_2:f
134 B_CURVE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c nurbs:p data:p
135 CURVE_DATA self_int:u analytic_form:p
136 NURBS_CURVE degree:n n_vertices:d vertex_dim:n n_knots:d knot_type:u periodic:l closed:l rational:l curve_form:u bspline_vertices:p knot_mult:p knots:p
137 SP_CURVE node_id:d attributes_groups:p owner:p next:p previous:p geometric_owner:p sense:c surface:p b_curve:p original:p tolerance_to_original:f
141 GEOMETRIC_OWNER owner:p next:p previous:p shared_geometry:p
163 HELIX_CU_FORM axis_pt:v axis_dir:v point:v hand:c turns:i pitch:f tol:f
184 HELIX_SU_FORM axis_pt:v axis_dir:v hand:c turns:i pitch:f gap:f tol:f
""";

    private static readonly int[] VisibleTypes = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 29, 30, 31, 32, 38, 50, 51, 52, 53, 54, 56, 60, 67, 68, 70, 80, 81, 90, 100, 120, 124, 130, 133, 134, 137];

    private static readonly Dictionary<int, XtNodeDef> Current = Build(legacy: false);

    private static readonly Dictionary<int, XtNodeDef> Legacy = Build(legacy: true);

    /// <summary>The base layouts: the 13006 schema (<paramref name="legacy"/> false) or the one of Parasolid 10 (a fin without an attribute pointer).</summary>
    public static IReadOnlyDictionary<int, XtNodeDef> Base(bool legacy) => legacy ? Legacy : Current;

    /// <summary>Whether a node type belongs to the 13006 base schema; an embedded schema declares the other types in full.</summary>
    public static bool InBase(int type) => Current.ContainsKey(type);

    /// <summary>Assembles a node definition from its fields (their slots are assigned here, in order: integer-like values and doubles are kept apart).</summary>
    public static XtNodeDef Create(int type, string name, IReadOnlyList<XtField> fields, bool? visible = null)
    {
        var list = new XtField[fields.Count];
        int ints = 0, doubles = 0;
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var slot = -1;
            if (f.Count > 0)
            {
                slot = f.IsDouble ? doubles : ints;
                if (f.IsDouble)
                {
                    doubles += f.Width * f.Count;
                }
                else
                {
                    ints += f.Count;
                }
            }

            list[i] = new XtField { Name = f.Name, Type = f.Type, Count = f.Count, Slot = slot, Transmitted = f.Transmitted };
        }

        return new XtNodeDef { Type = type, Name = name, Fields = list, IntCount = ints, DoubleCount = doubles, Visible = visible ?? Array.IndexOf(VisibleTypes, type) >= 0 };
    }

    /// <summary>Parses "name:t", "name:t*" (variable length), "name:t[n]" (fixed array) and "name:t[*]" (a run of logicals) into a field.</summary>
    public static XtField ParseField(string spec)
    {
        var colon = spec.IndexOf(':', StringComparison.Ordinal);
        var name = spec[..colon];
        var rest = spec[(colon + 1)..];
        var letter = rest[0] == 't' ? 'd' : rest[0];
        var count = 1;
        if (rest.Length > 1)
        {
            var suffix = rest[1..];
            count = suffix switch { "*" => 0, "[*]" => -1, _ => int.Parse(suffix.Trim('[', ']'), CultureInfo.InvariantCulture) };
        }

        return new XtField { Name = name, Type = letter, Count = count, Slot = -1 };
    }

    private static Dictionary<int, XtNodeDef> Build(bool legacy)
    {
        var result = new Dictionary<int, XtNodeDef>();
        foreach (var line in Spec.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var type = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var fields = new List<XtField>();
            foreach (var spec in parts.Skip(2))
            {
                var field = ParseField(spec);
                if (legacy && type == 17 && field.Name == "attributes_groups")
                {
                    continue;
                }

                fields.Add(field);
            }

            result[type] = Create(type, parts[1], fields);
        }

        return result;
    }
}

/// <summary>One node of a transmit file: its definition, index and field values (NaN is the unset double).</summary>
internal sealed class XtNode
{
    public required XtNodeDef Def { get; init; }

    public required int Index { get; init; }

    public long[] Ints { get; init; } = [];

    public double[] Doubles { get; init; } = [];

    /// <summary>The variable length field: long[] (integer-like), double[] (doubles, vectors and chart points flattened) or string (characters).</summary>
    public object? Variable { get; set; }

    public long Int(string name) => Ints[Def.Field(name).Slot];

    public int Ptr(string name) => (int)Ints[Def.Field(name).Slot];

    public char Char(string name) => (char)Ints[Def.Field(name).Slot];

    public double Dbl(string name) => Doubles[Def.Field(name).Slot];

    public bool Has(string name) => Def.Fields.Any(f => f.Name == name);

    public (double X, double Y, double Z) Vec(string name)
    {
        var s = Def.Field(name).Slot;
        return (Doubles[s], Doubles[s + 1], Doubles[s + 2]);
    }

    public double[] DoubleArray(string name)
    {
        var f = Def.Field(name);
        return f.Slot < 0 ? (Variable as double[] ?? []) : Doubles.AsSpan(f.Slot, f.Count * f.Width).ToArray();
    }

    public long[] LongArray => Variable as long[] ?? [];
}
