using System;
using System.Collections.Generic;

namespace ExCombo.Helpers;

// Boolean expression over numbered inputs for the Logic node. Inputs are 1-based integers;
// operators (case-insensitive): AND/&&/&, OR/||/|, XOR/^, NOT/!, parentheses.
// Precedence: OR < XOR < AND < NOT < (). Parse once and reuse — Cached() memoizes by string.
public static class LogicExpr {
    public sealed class LogicAst {
        private readonly Node _root;
        public  int MaxInput { get; }
        internal LogicAst(Node root, int maxInput) { _root = root; MaxInput = maxInput; }
        public bool Eval(Func<int, bool> input) => _root.Eval(input);
    }

    internal abstract class Node {
        public abstract bool Eval(Func<int, bool> input);
    }
    private sealed class InputNode(int idx) : Node {
        public override bool Eval(Func<int, bool> input) => input(idx);
    }
    private sealed class NotNode(Node inner) : Node {
        public override bool Eval(Func<int, bool> input) => !inner.Eval(input);
    }
    private sealed class BinNode(char op, Node l, Node r) : Node {
        public override bool Eval(Func<int, bool> input) => op switch {
            '&' => l.Eval(input) && r.Eval(input),
            '|' => l.Eval(input) || r.Eval(input),
            _   => l.Eval(input) ^  r.Eval(input),
        };
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────
    // Token kinds: '&' '|' '^' '!' '(' ')' for operators, 'N' for an input number, 'E' end.
    private readonly record struct Tok(char Kind, int Value, int Pos);

    private static List<Tok>? Tokenize(string s, out string error) {
        error = "";
        var toks = new List<Tok>();
        var i = 0;
        while (i < s.Length) {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c)) {
                var start = i;
                var v = 0;
                while (i < s.Length && char.IsDigit(s[i])) { v = v * 10 + (s[i] - '0'); i++; }
                if (v < 1) { error = $"input numbers start at 1 (position {start + 1})"; return null; }
                toks.Add(new Tok('N', v, start));
                continue;
            }
            if (char.IsLetter(c)) {
                var start = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                var word = s[start..i].ToUpperInvariant();
                var kind = word switch {
                    "AND" => '&', "OR" => '|', "XOR" => '^', "NOT" => '!',
                    _     => '\0',
                };
                if (kind == '\0') { error = $"unknown word \"{s[start..i]}\" (position {start + 1})"; return null; }
                toks.Add(new Tok(kind, 0, start));
                continue;
            }
            switch (c) {
                case '&': toks.Add(new Tok('&', 0, i)); i += i + 1 < s.Length && s[i + 1] == '&' ? 2 : 1; continue;
                case '|': toks.Add(new Tok('|', 0, i)); i += i + 1 < s.Length && s[i + 1] == '|' ? 2 : 1; continue;
                case '^': toks.Add(new Tok('^', 0, i)); i++; continue;
                case '!': toks.Add(new Tok('!', 0, i)); i++; continue;
                case '(': toks.Add(new Tok('(', 0, i)); i++; continue;
                case ')': toks.Add(new Tok(')', 0, i)); i++; continue;
                default:  error = $"unexpected character '{c}' (position {i + 1})"; return null;
            }
        }
        toks.Add(new Tok('E', 0, s.Length));
        return toks;
    }

    // ── Recursive-descent parser ──────────────────────────────────────────
    private sealed class Parser(List<Tok> toks) {
        private int _i;
        public  int MaxInput;
        public  string Error = "";

        private Tok Cur => toks[_i];

        public Node? ParseOr() {
            var l = ParseXor();
            if (l == null) return null;
            while (Cur.Kind == '|') {
                _i++;
                var r = ParseXor();
                if (r == null) return null;
                l = new BinNode('|', l, r);
            }
            return l;
        }

        private Node? ParseXor() {
            var l = ParseAnd();
            if (l == null) return null;
            while (Cur.Kind == '^') {
                _i++;
                var r = ParseAnd();
                if (r == null) return null;
                l = new BinNode('^', l, r);
            }
            return l;
        }

        private Node? ParseAnd() {
            var l = ParseUnary();
            if (l == null) return null;
            while (Cur.Kind == '&') {
                _i++;
                var r = ParseUnary();
                if (r == null) return null;
                l = new BinNode('&', l, r);
            }
            return l;
        }

        private Node? ParseUnary() {
            if (Cur.Kind == '!') {
                _i++;
                var inner = ParseUnary();
                return inner == null ? null : new NotNode(inner);
            }
            return ParsePrimary();
        }

        private Node? ParsePrimary() {
            var t = Cur;
            switch (t.Kind) {
                case 'N':
                    _i++;
                    if (t.Value > MaxInput) MaxInput = t.Value;
                    return new InputNode(t.Value);
                case '(': {
                    _i++;
                    var inner = ParseOr();
                    if (inner == null) return null;
                    if (Cur.Kind != ')') { Error = $"missing ')' (position {Cur.Pos + 1})"; return null; }
                    _i++;
                    return inner;
                }
                case 'E':
                    Error = "expression ends too early";
                    return null;
                default:
                    Error = $"unexpected '{TokLabel(t.Kind)}' (position {t.Pos + 1})";
                    return null;
            }
        }

        public bool AtEnd => Cur.Kind == 'E';
        public Tok Current => Cur;
    }

    private static string TokLabel(char k) => k switch {
        '&' => "AND", '|' => "OR", '^' => "XOR", '!' => "NOT", 'N' => "number", _ => k.ToString(),
    };

    public static LogicAst? Parse(string expr, out string error) {
        if (string.IsNullOrWhiteSpace(expr)) { error = "expression is empty"; return null; }
        var toks = Tokenize(expr, out error);
        if (toks == null) return null;
        var p    = new Parser(toks);
        var root = p.ParseOr();
        if (root == null) { error = p.Error; return null; }
        if (!p.AtEnd) { error = $"unexpected '{TokLabel(p.Current.Kind)}' (position {p.Current.Pos + 1})"; return null; }
        return new LogicAst(root, p.MaxInput);
    }

    // Memoized parse; null = invalid expression. Expressions are short user strings, so an
    // unbounded per-string cache is fine (invalid ones cache as null to avoid re-parsing per frame).
    private static readonly Dictionary<string, LogicAst?> _cache = new();

    public static LogicAst? Cached(string expr) {
        if (_cache.TryGetValue(expr, out var ast)) return ast;
        ast = Parse(expr, out _);
        _cache[expr] = ast;
        return ast;
    }
}
