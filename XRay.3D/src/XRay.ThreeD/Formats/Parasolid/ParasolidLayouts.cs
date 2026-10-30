using System.Globalization;

namespace XRay.ThreeD.Formats.Parasolid;

/// <summary>A schema key, SCH_&lt;modeller&gt;_&lt;schema&gt;[_&lt;base schema&gt;]: the schema number names the layout of the node stream; a third number says the file embeds its schema as edits to that base.</summary>
internal readonly record struct XtKey(string Raw, int Schema, int Base)
{
    public bool Embedded => Base > 0;

    public static XtKey? Parse(string? key)
    {
        var parts = key?.Split('_') ?? [];
        if (parts.Length is < 3 or > 4 || parts[0] != "SCH" || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var schema))
        {
            return null;
        }

        var baseSchema = 0;
        if (parts.Length == 4 && !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out baseSchema))
        {
            return null;
        }

        return new XtKey(key!, schema, baseSchema);
    }
}

/// <summary>How the nodes of a file are laid out: the effective layout of every node type of a plain schema, or the base layouts an embedded schema is applied to.</summary>
internal sealed class XtLayout
{
    public required string Name { get; init; }

    public required IReadOnlyDictionary<int, XtNodeDef> Types { get; init; }

    public bool Embedded { get; init; }
}

/// <summary>
/// The layouts of the schemas. Parasolid 10 and the schemas 12006 to 13006 are the lists of the public XT format reference (<see cref="XtSchema"/>). A file
/// of a later schema either embeds its changes to the 13006 base (a three number key, read from the file) or names a plain schema, whose layouts are the
/// base with the edits the files of that schema revision carry in their embedded schemas: the changes accumulate from one revision to the next, so the
/// edits of the nearest known revision at or below the file's are applied, and a revision between two known ones is read by each candidate in turn.
/// The edit tables of the older revisions are the per-revision edit sequences recorded by the MIT-licensed parasolid-kit (see THIRD_PARTY_NOTICES.md); the
/// one of 35102 is the edit sequence of the real file of that schema in the local samples.
/// </summary>
internal static class XtLayouts
{
    private const string Body20000 = "K23 +index_origin:d +index_values:p +node_id_values:p +schema_values:p";

    private const string Body28002 = "K13 D +container_ref:p K9 +boundary_mesh:p +index_origin:d +index_values:p +node_id_values:p +schema_values:p +child_body:p +min_local_id:d";

    private const string Body28101 = "K6 +construction_mesh:p +construction_polyline:p K7 D +container_ref:p K6 +boundary_mesh:p +boundary_polyline:p K3 +index_origin:d +index_values:p +node_id_values:p +schema_values:p +child_body:p +min_local_id:d";

    private const string Body32001 = Body28101 + " +mesh_offset_ref:p";

    private const string Body33103 = "K3 +construction_lattice:p K3 +construction_mesh:p +construction_polyline:p K7 D +container_ref:p K6 +boundary_mesh:p +boundary_polyline:p K3 +index_origin:d +index_values:p +node_id_values:p +schema_values:p +child_body:p +min_local_id:d +mesh_offset_ref:p";

    private const string Body35102 = "K3 +construction_lattice:p K3 +construction_mesh:p +construction_polyline:p K7 D +container_ref:p K3 +boundary_lattice:p K3 +boundary_mesh:p +boundary_polyline:p K3 +index_origin:d +index_values:p +node_id_values:p +schema_values:p +child_body:p +min_local_id:d +mesh_offset_ref:p";

    private const string Region = "K7 +owner_ref:p";

    private const string Region35102 = "K6 +frame:p K1 +owner_ref:p";

    private const string Intersection = "K11 +intersection_data:p";

    private const string Limit = "K1 +limit_state:c K1";

    // The last three base fields are dropped where the embedded sequence ends.
    private const string List = "K1 +entry_kind:u +transmission_flag:l K3 D K2 D +cursor_index:d +cursor_block:p K1 D D D";

    private const string PointerBlock = "K1 +index_origin:d K2";

    /// <summary>The first schema number of each revision and its edits (type, script), cumulative in the sense that each row restates all of them.</summary>
    private static readonly (int From, (int Type, string Script)[] Edits)[] Generations =
    [
        (13007, []),
        (15003, [(70, List)]),
        (19008, [(12, Body20000), (70, List), (74, PointerBlock)]),
        (26105, [(12, Body28002), (19, Region), (70, List), (74, PointerBlock)]),
        (28002, [(12, Body28002), (19, Region), (41, Limit), (70, List), (74, PointerBlock)]),
        (28101, [(12, Body28101), (19, Region), (41, Limit), (70, List), (74, PointerBlock)]),
        (30000, [(12, Body28101), (19, Region), (38, Intersection), (41, Limit), (70, List), (74, PointerBlock)]),
        (32001, [(12, Body32001), (19, Region), (38, Intersection), (41, Limit), (70, List), (74, PointerBlock)]),
        (33103, [(12, Body33103), (19, Region), (38, Intersection), (41, Limit), (70, List), (74, PointerBlock)]),
        (35102, [(12, Body35102), (19, Region35102), (38, Intersection), (41, Limit), (70, List), (74, PointerBlock)]),
    ];

    private static readonly Dictionary<int, XtLayout> Cache = [];

    /// <summary>The layouts to try for a schema key, most likely first; empty (with the reason) when the key is not understood.</summary>
    public static List<XtLayout> Candidates(XtKey? key, out string reason)
    {
        reason = "";
        var result = new List<XtLayout>();
        if (key is not { } k)
        {
            reason = "the schema key is missing or malformed";
            return result;
        }

        if (k.Embedded)
        {
            // The file declares its own differences to its base: 13006 (Parasolid 13) is the base of every file seen; Parasolid 10 had no embedding.
            result.Add(new XtLayout { Name = "embedded on " + k.Base.ToString(CultureInfo.InvariantCulture), Types = XtSchema.Base(legacy: k.Base < 11000), Embedded = true });
            return result;
        }

        var schema = k.Schema;
        if (schema < 11000)
        {
            result.Add(Plain(0, legacy: true));
        }
        else if (schema <= 13006)
        {
            result.Add(Plain(0, legacy: false));
            if (schema < 12000)
            {
                // Parasolid 11 is between the two documented layouts: both are tried.
                result.Add(Plain(0, legacy: true));
            }
        }
        else
        {
            // The nearest revision at or below the file's, then the older ones in case the file's revision lies between two.
            for (var g = Generations.Length - 1; g >= 0; g--)
            {
                if (Generations[g].From <= schema)
                {
                    result.Add(Plain(g + 1, legacy: false));
                }
            }
        }

        return result;
    }

    private static XtLayout Plain(int generation, bool legacy)
    {
        var id = legacy ? -1 : generation;
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var baseTypes = XtSchema.Base(legacy);
            var types = new Dictionary<int, XtNodeDef>(baseTypes);
            if (generation > 0)
            {
                foreach (var (type, script) in Generations[generation - 1].Edits)
                {
                    types[type] = Apply(baseTypes[type], script);
                }

                if (Generations[generation - 1].From >= 26105)
                {
                    // The block of a file of several parts: its fixed fields are not named by the sources, the parts are the variable list at its end.
                    types[176] = XtSchema.Create(176, "PART_BLOCK", [XtSchema.ParseField("count:d"), XtSchema.ParseField("a:d"), XtSchema.ParseField("b:d"), XtSchema.ParseField("c:d"), XtSchema.ParseField("d:d"), XtSchema.ParseField("parts:p*")], visible: false);
                }

                if (Generations[generation - 1].From >= 30000)
                {
                    // Intersection data (surface parameters of an intersection curve), declared in full by the files of these revisions.
                    types[204] = XtSchema.Create(204, "INTERSECTION_DATA", [XtSchema.ParseField("uv_kind:u"), XtSchema.ParseField("values:f*")], visible: false);
                }
            }

            var layout = new XtLayout { Name = legacy ? "Parasolid 10" : generation == 0 ? "13006" : "revision " + Generations[generation - 1].From.ToString(CultureInfo.InvariantCulture), Types = types };
            Cache[id] = layout;
            return layout;
        }
    }

    /// <summary>Applies an edit script to a base layout: Kn keeps the next n base fields, D drops one, +name:t inserts a field.</summary>
    public static XtNodeDef Apply(XtNodeDef baseDef, string script)
    {
        var fields = new List<XtField>();
        var next = 0;
        foreach (var token in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token[0] == 'K')
            {
                var n = int.Parse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
                for (var i = 0; i < n; i++)
                {
                    fields.Add(baseDef.Fields[next++]);
                }
            }
            else if (token == "D")
            {
                next++;
            }
            else
            {
                fields.Add(XtSchema.ParseField(token[1..]));
            }
        }

        if (next != baseDef.Fields.Length)
        {
            throw new InvalidOperationException("A Parasolid layout edit does not consume its base layout: " + baseDef.Name);
        }

        return XtSchema.Create(baseDef.Type, baseDef.Name, fields, baseDef.Visible);
    }
}
