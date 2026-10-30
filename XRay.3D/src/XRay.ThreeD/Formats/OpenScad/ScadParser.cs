using System.Globalization;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.OpenScad;

internal abstract record Expr;

internal sealed record Lit(object? Value) : Expr;

internal sealed record Ident(string Name) : Expr;

internal sealed record VecExpr(Expr[] Items) : Expr;

internal sealed record RangeExpr(Expr Start, Expr? Step, Expr End) : Expr;

internal sealed record Unary(string Op, Expr Operand) : Expr;

internal sealed record Binary(string Op, Expr Left, Expr Right) : Expr;

internal sealed record Ternary(Expr Cond, Expr Then, Expr Else) : Expr;

internal sealed record Arg(string? Name, Expr Value);

internal sealed record CallExpr(Expr Callee, Arg[] Args) : Expr;

internal sealed record IndexExpr(Expr Target, Expr Index) : Expr;

internal sealed record MemberExpr(Expr Target, string Name) : Expr;

internal sealed record Param(string Name, Expr? Default);

internal sealed record FuncLit(Param[] Params, Expr Body) : Expr;

internal sealed record LetExpr(Arg[] Assigns, Expr Body) : Expr;

internal sealed record LcFor(Arg[] Vars, Expr Body) : Expr;

internal sealed record LcCFor(Arg[] Init, Expr Cond, Arg[] Step, Expr Body) : Expr;

internal sealed record LcIf(Expr Cond, Expr Then, Expr? Else) : Expr;

internal sealed record LcEach(Expr Items) : Expr;

internal abstract record Stmt;

internal sealed record AssignStmt(string Name, Expr Value) : Stmt;

internal sealed record ModuleDef(string Name, Param[] Params, Stmt Body) : Stmt;

internal sealed record FuncDef(string Name, Param[] Params, Expr Body) : Stmt;

internal sealed record Block(Stmt[] Items) : Stmt;

internal sealed record Instance(string Name, Arg[] Args, Stmt? Child, string Modifiers) : Stmt;

internal sealed record IfStmt(Expr Cond, Stmt Then, Stmt? Else) : Stmt;

internal sealed record ForStmt(Arg[] Vars, Stmt Body, bool Intersection) : Stmt;

internal sealed record CForStmt(Arg[] Init, Expr Cond, Arg[] Step, Stmt Body) : Stmt;

internal sealed record LetStmt(Arg[] Assigns, Stmt Body) : Stmt;

internal sealed record IncludeStmt(string Path, bool Use) : Stmt;

/// <summary>Recursive descent parser for the OpenSCAD language. Depth and size are bounded; errors are <see cref="ModelFormatException"/>.</summary>
internal sealed class ScadParser
{
    private enum T { Num, Str, Id, Op, Eof }

    private readonly record struct Tok(T Kind, string Text, double Number);

    private readonly List<Tok> _t = new();
    private int _p;
    private int _depth;
    private readonly CancellationToken _cancel;

    private ScadParser(string source, CancellationToken cancel)
    {
        _cancel = cancel;
        Lex(source);
    }

    public static Stmt[] Parse(string source, CancellationToken cancel)
    {
        var parser = new ScadParser(source, cancel);
        var list = new List<Stmt>();
        long tick = 0;
        while (parser.Peek.Kind != T.Eof)
        {
            Cancel.Every(cancel, tick++);
            if (parser.IsOp(";"))
            {
                parser._p++;
                continue;
            }

            list.Add(parser.ParseStmt());
        }

        return list.ToArray();
    }

    private void Lex(string s)
    {
        var i = 0;
        long tick = 0;
        while (i < s.Length)
        {
            Cancel.Every(_cancel, tick++);
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                var j = i;
                while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.'))
                {
                    j++;
                }

                if (j < s.Length && (s[j] == 'e' || s[j] == 'E'))
                {
                    var k = j + 1;
                    if (k < s.Length && (s[k] == '+' || s[k] == '-'))
                    {
                        k++;
                    }

                    if (k < s.Length && char.IsDigit(s[k]))
                    {
                        while (k < s.Length && char.IsDigit(s[k]))
                        {
                            k++;
                        }

                        j = k;
                    }
                }

                if (!double.TryParse(s.AsSpan(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    throw new ModelFormatException("Bad number in the OpenSCAD source.");
                }

                _t.Add(new Tok(T.Num, "", number));
                i = j;
            }
            else if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '$'))
                {
                    j++;
                }

                _t.Add(new Tok(T.Id, s.Substring(i, j - i), 0));
                i = j;
            }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                var j = i + 1;
                while (j < s.Length && s[j] != '"')
                {
                    if (s[j] == '\\' && j + 1 < s.Length)
                    {
                        j++;
                        sb.Append(s[j] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => s[j] });
                    }
                    else
                    {
                        sb.Append(s[j]);
                    }

                    j++;
                }

                if (j >= s.Length)
                {
                    throw new ModelFormatException("Unterminated string in the OpenSCAD source.");
                }

                _t.Add(new Tok(T.Str, sb.ToString(), 0));
                i = j + 1;
            }
            else if (c == '<' && IsPathStart(s, i))
            {
                var j = s.IndexOf('>', i);
                _t.Add(new Tok(T.Str, s.Substring(i + 1, j - i - 1), 0));
                i = j + 1;
            }
            else
            {
                string[] three = ["==", "!=", "<=", ">=", "&&", "||"];
                string op = c.ToString();
                if (i + 1 < s.Length)
                {
                    var two = s.Substring(i, 2);
                    if (Array.IndexOf(three, two) >= 0)
                    {
                        op = two;
                    }
                }

                _t.Add(new Tok(T.Op, op, 0));
                i += op.Length;
            }

            if (_t.Count > 5_000_000)
            {
                throw new ModelFormatException("The OpenSCAD source is too large.");
            }
        }

        _t.Add(new Tok(T.Eof, "", 0));
    }

    /// <summary>A "&lt;file.scad&gt;" path after include or use.</summary>
    private bool IsPathStart(string s, int i)
    {
        if (_t.Count == 0 || _t[^1].Kind != T.Id || (_t[^1].Text != "include" && _t[^1].Text != "use"))
        {
            return false;
        }

        return s.IndexOf('>', i) > i;
    }

    private Tok Peek => _t[_p];

    private bool IsOp(string op) => Peek.Kind == T.Op && Peek.Text == op;

    private bool IsId(string id) => Peek.Kind == T.Id && Peek.Text == id;

    private void Expect(string op)
    {
        if (!IsOp(op))
        {
            throw new ModelFormatException($"OpenSCAD: expected '{op}'.");
        }

        _p++;
    }

    private string ExpectId()
    {
        if (Peek.Kind != T.Id)
        {
            throw new ModelFormatException("OpenSCAD: expected a name.");
        }

        return _t[_p++].Text;
    }

    private void Enter()
    {
        if (++_depth > 200)
        {
            throw new ModelFormatException("The OpenSCAD source is nested too deeply.");
        }
    }

    private Stmt ParseStmt()
    {
        Enter();
        try
        {
            _cancel.ThrowIfCancellationRequested();
            if (IsOp("{"))
            {
                _p++;
                var items = new List<Stmt>();
                while (!IsOp("}"))
                {
                    if (Peek.Kind == T.Eof)
                    {
                        throw new ModelFormatException("OpenSCAD: missing '}'.");
                    }

                    if (IsOp(";"))
                    {
                        _p++;
                        continue;
                    }

                    items.Add(ParseStmt());
                }

                _p++;
                return new Block(items.ToArray());
            }

            if (IsOp(";"))
            {
                _p++;
                return new Block([]);
            }

            if (IsId("module"))
            {
                _p++;
                var name = ExpectId();
                var ps = ParseParams();
                return new ModuleDef(name, ps, ParseStmt());
            }

            if (IsId("function") && _t[_p + 1].Kind == T.Id)
            {
                _p++;
                var name = ExpectId();
                var ps = ParseParams();
                Expect("=");
                var body = ParseExpr();
                Expect(";");
                return new FuncDef(name, ps, body);
            }

            if (IsId("include") || IsId("use"))
            {
                var use = Peek.Text == "use";
                _p++;
                var path = Peek.Kind == T.Str ? _t[_p++].Text : throw new ModelFormatException("OpenSCAD: expected a file name.");
                if (IsOp(";"))
                {
                    _p++;
                }

                return new IncludeStmt(path, use);
            }

            if (Peek.Kind == T.Id && _t[_p + 1].Kind == T.Op && _t[_p + 1].Text == "=" && !(Peek.Text is "for" or "if" or "let"))
            {
                var name = _t[_p].Text;
                _p += 2;
                var value = ParseExpr();
                Expect(";");
                return new AssignStmt(name, value);
            }

            return ParseInstance();
        }
        finally
        {
            _depth--;
        }
    }

    private Stmt ParseInstance()
    {
        var modifiers = new StringBuilder();
        while (Peek.Kind == T.Op && Peek.Text is "*" or "!" or "#" or "%")
        {
            modifiers.Append(_t[_p++].Text);
        }

        var name = ExpectId();
        if (name is "for" or "intersection_for")
        {
            Expect("(");
            if (HasSemicolonBeforeClose())
            {
                var init = ParseAssignList(";");
                Expect(";");
                var cond = ParseExpr();
                Expect(";");
                var step = ParseAssignList(")");
                Expect(")");
                return new CForStmt(init, cond, step, ParseStmt());
            }

            var vars = ParseAssignList(")");
            Expect(")");
            return Wrap(new ForStmt(vars, ParseStmt(), name == "intersection_for"), modifiers);
        }

        if (name == "let")
        {
            Expect("(");
            var assigns = ParseAssignList(")");
            Expect(")");
            return new LetStmt(assigns, ParseStmt());
        }

        if (name == "if")
        {
            Expect("(");
            var cond = ParseExpr();
            Expect(")");
            var then = ParseStmt();
            Stmt? other = null;
            if (IsId("else"))
            {
                _p++;
                other = ParseStmt();
            }

            return Wrap(new IfStmt(cond, then, other), modifiers);
        }

        Expect("(");
        var args = ParseArgs(")");
        Expect(")");
        Stmt? child = null;
        if (IsOp(";"))
        {
            _p++;
        }
        else
        {
            child = ParseStmt();
        }

        return new Instance(name, args, child, modifiers.ToString());
    }

    private static Stmt Wrap(Stmt s, StringBuilder modifiers) => modifiers.Length == 0 ? s : new Instance("group", [], s, modifiers.ToString());

    private bool HasSemicolonBeforeClose()
    {
        var depth = 0;
        for (var i = _p; i < _t.Count; i++)
        {
            var t = _t[i];
            if (t.Kind == T.Op)
            {
                if (t.Text is "(" or "[" or "{")
                {
                    depth++;
                }
                else if (t.Text is ")" or "]" or "}")
                {
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                }
                else if (t.Text == ";" && depth == 0)
                {
                    return true;
                }
            }
            else if (t.Kind == T.Eof)
            {
                return false;
            }
        }

        return false;
    }

    private Arg[] ParseAssignList(string close)
    {
        var list = new List<Arg>();
        while (!IsOp(close))
        {
            var name = ExpectId();
            Expect("=");
            list.Add(new Arg(name, ParseExpr()));
            if (IsOp(","))
            {
                _p++;
            }
            else
            {
                break;
            }
        }

        return list.ToArray();
    }

    private Param[] ParseParams()
    {
        Expect("(");
        var list = new List<Param>();
        while (!IsOp(")"))
        {
            var name = ExpectId();
            Expr? def = null;
            if (IsOp("="))
            {
                _p++;
                def = ParseExpr();
            }

            list.Add(new Param(name, def));
            if (IsOp(","))
            {
                _p++;
            }
            else
            {
                break;
            }
        }

        Expect(")");
        return list.ToArray();
    }

    private Arg[] ParseArgs(string close)
    {
        var list = new List<Arg>();
        while (!IsOp(close))
        {
            if (Peek.Kind == T.Eof)
            {
                throw new ModelFormatException("OpenSCAD: unexpected end of file in an argument list.");
            }

            if (Peek.Kind == T.Id && _t[_p + 1].Kind == T.Op && _t[_p + 1].Text == "=")
            {
                var name = _t[_p].Text;
                _p += 2;
                list.Add(new Arg(name, ParseExpr()));
            }
            else
            {
                list.Add(new Arg(null, ParseExpr()));
            }

            if (IsOp(","))
            {
                _p++;
            }
            else
            {
                break;
            }
        }

        return list.ToArray();
    }

    private Expr ParseExpr()
    {
        Enter();
        try
        {
            if (IsId("function") && _t[_p + 1].Kind == T.Op && _t[_p + 1].Text == "(")
            {
                _p++;
                var ps = ParseParams();
                return new FuncLit(ps, ParseExpr());
            }

            if (IsId("let") && _t[_p + 1].Text == "(")
            {
                _p++;
                Expect("(");
                var assigns = ParseAssignList(")");
                Expect(")");
                return new LetExpr(assigns, ParseExpr());
            }

            var cond = ParseBinary(0);
            if (IsOp("?"))
            {
                _p++;
                var a = ParseExpr();
                Expect(":");
                var b = ParseExpr();
                return new Ternary(cond, a, b);
            }

            return cond;
        }
        finally
        {
            _depth--;
        }
    }

    private static readonly string[][] Levels =
    [
        ["||"], ["&&"], ["==", "!="], ["<", ">", "<=", ">="], ["+", "-"], ["*", "/", "%"],
    ];

    private Expr ParseBinary(int level)
    {
        if (level >= Levels.Length)
        {
            return ParseUnary();
        }

        var left = ParseBinary(level + 1);
        while (Peek.Kind == T.Op && Array.IndexOf(Levels[level], Peek.Text) >= 0)
        {
            var op = _t[_p++].Text;
            left = new Binary(op, left, ParseBinary(level + 1));
        }

        return left;
    }

    private Expr ParseUnary()
    {
        if (Peek.Kind == T.Op && Peek.Text is "-" or "+" or "!")
        {
            Enter();
            try
            {
                var op = _t[_p++].Text;
                return new Unary(op, ParseUnary());
            }
            finally
            {
                _depth--;
            }
        }

        return ParsePostfix(ParsePrimary());
    }

    private Expr ParsePostfix(Expr e)
    {
        while (true)
        {
            if (IsOp("("))
            {
                _p++;
                var args = ParseArgs(")");
                Expect(")");
                e = new CallExpr(e, args);
            }
            else if (IsOp("["))
            {
                _p++;
                var index = ParseExpr();
                Expect("]");
                e = new IndexExpr(e, index);
            }
            else if (IsOp(".") && _t[_p + 1].Kind == T.Id)
            {
                _p++;
                e = new MemberExpr(e, _t[_p++].Text);
            }
            else
            {
                return e;
            }
        }
    }

    private Expr ParsePrimary()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case T.Num:
                _p++;
                return new Lit(t.Number);
            case T.Str:
                _p++;
                return new Lit(t.Text);
            case T.Id:
                _p++;
                return t.Text switch { "true" => new Lit(true), "false" => new Lit(false), "undef" => new Lit(null), _ => new Ident(t.Text) };
        }

        if (IsOp("("))
        {
            _p++;
            var e = ParseExpr();
            Expect(")");
            return e;
        }

        if (IsOp("["))
        {
            _p++;
            return ParseBracket();
        }

        throw new ModelFormatException("OpenSCAD: unexpected token in an expression.");
    }

    private Expr ParseBracket()
    {
        if (IsOp("]"))
        {
            _p++;
            return new VecExpr([]);
        }

        // [start : end] or [start : step : end] or a list with comprehension elements
        var first = ParseListElement();
        if (IsOp(":") && first is not (LcFor or LcIf or LcEach))
        {
            _p++;
            var second = ParseExpr();
            Expr? third = null;
            if (IsOp(":"))
            {
                _p++;
                third = ParseExpr();
            }

            Expect("]");
            return third is null ? new RangeExpr(first, null, second) : new RangeExpr(first, second, third);
        }

        var items = new List<Expr> { first };
        while (IsOp(","))
        {
            _p++;
            if (IsOp("]"))
            {
                break;
            }

            items.Add(ParseListElement());
        }

        Expect("]");
        return new VecExpr(items.ToArray());
    }

    private Expr ParseListElement()
    {
        _cancel.ThrowIfCancellationRequested();
        if (IsId("for"))
        {
            _p++;
            Expect("(");
            if (HasSemicolonBeforeClose())
            {
                var init = ParseAssignList(";");
                Expect(";");
                var cond = ParseExpr();
                Expect(";");
                var step = ParseAssignList(")");
                Expect(")");
                return new LcCFor(init, cond, step, ParseListElement());
            }

            var vars = ParseAssignList(")");
            Expect(")");
            return new LcFor(vars, ParseListElement());
        }

        if (IsId("if"))
        {
            _p++;
            Expect("(");
            var cond = ParseExpr();
            Expect(")");
            var then = ParseListElement();
            Expr? other = null;
            if (IsId("else"))
            {
                _p++;
                other = ParseListElement();
            }

            return new LcIf(cond, then, other);
        }

        if (IsId("each"))
        {
            _p++;
            return new LcEach(ParseListElement());
        }

        if (IsId("let") && _t[_p + 1].Text == "(")
        {
            _p++;
            Expect("(");
            var assigns = ParseAssignList(")");
            Expect(")");
            return new LetExpr(assigns, ParseListElement());
        }

        return ParseExpr();
    }
}
