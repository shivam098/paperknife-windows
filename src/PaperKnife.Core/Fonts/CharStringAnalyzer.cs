using System.Buffers.Binary;

namespace PaperKnife.Core.Fonts;

/// <summary>
/// Follows a Type 1 glyph program (Adobe Type 1 Font Format, chapter 6) far enough to tell what it
/// depends on: the subroutines it calls, directly or through other subroutines, and the glyphs an
/// accented glyph is built from (seac). Type 1 programs have no branches, so running them with the
/// operand values tracked finds exactly the subroutines a renderer runs. Hint replacement
/// ("subr# 1 3 callothersubr pop callsubr") is followed; a subroutine number that can't be known
/// fails the analysis.
/// </summary>
internal static class CharStringAnalyzer
{
    private const int MaxDepth = 16;
    private const int MaxStack = 24;

    /// <summary>A limit on the instructions run, shared by the analyses of one font, so a crafted font can't take long.</summary>
    internal sealed class Budget(int steps)
    {
        public int Steps { get; set; } = steps;
    }

    /// <summary>
    /// The subroutines <paramref name="program"/> calls and the StandardEncoding codes of the
    /// glyphs it is built from, or null when the program can't be followed with certainty.
    /// </summary>
    public static (HashSet<int> Subrs, List<int> Components)? Analyze(byte[] program, IReadOnlyDictionary<int, byte[]> subrs, Budget budget)
    {
        var run = new Run(subrs, budget);
        return run.Execute(program, 0) == Outcome.Failed ? null : (run.Called, run.Components);
    }

    private enum Outcome
    {
        Return,
        End,
        Failed,
    }

    private sealed class Run(IReadOnlyDictionary<int, byte[]> subrs, Budget budget)
    {
        // Unknown values are null.
        private readonly List<double?> _stack = [];
        private int _knownResults;
        private List<double?>? _unknownResults;
        private int _nextUnknown;

        public HashSet<int> Called { get; } = [];

        public List<int> Components { get; } = [];

        public Outcome Execute(byte[] program, int depth)
        {
            if (depth > MaxDepth)
            {
                return Outcome.Failed;
            }

            var i = 0;
            while (i < program.Length)
            {
                if (--budget.Steps < 0)
                {
                    return Outcome.Failed;
                }

                int v = program[i++];
                if (v >= 32)
                {
                    if (ReadNumber(program, v, ref i) is not { } number || _stack.Count >= MaxStack)
                    {
                        return Outcome.Failed;
                    }

                    _stack.Add(number);
                    continue;
                }

                switch (v)
                {
                    case 10: // callsubr
                        if (!TryPop(out var index) || index is not { } n || !IsIndex(n) || !subrs.TryGetValue((int)n, out var subr))
                        {
                            return Outcome.Failed;
                        }

                        Called.Add((int)n);
                        var outcome = Execute(subr, depth + 1);
                        if (outcome != Outcome.Return)
                        {
                            return outcome;
                        }

                        break;
                    case 11: // return
                        return Outcome.Return;
                    case 14: // endchar
                        return Outcome.End;
                    case 12:
                        if (i >= program.Length)
                        {
                            return Outcome.Failed;
                        }

                        var escaped = program[i++];
                        switch (escaped)
                        {
                            case 6: // seac: asb adx ady bchar achar
                                if (_stack.Count < 5 || _stack[^2] is not { } baseCode || _stack[^1] is not { } accentCode
                                    || !IsCode(baseCode) || !IsCode(accentCode))
                                {
                                    return Outcome.Failed;
                                }

                                Components.Add((int)baseCode);
                                Components.Add((int)accentCode);
                                return Outcome.End;
                            case 12: // div
                                if (!TryPop(out var divisor) || !TryPop(out var dividend))
                                {
                                    return Outcome.Failed;
                                }

                                _stack.Add(divisor is { } d && dividend is { } x && d != 0 ? x / d : null);
                                break;
                            case 16: // callothersubr: arg1 ... argn n othersubr#
                                if (!TryPop(out var other) || !TryPop(out var count) || other is not { } o || count is not { } c
                                    || !IsIndex(c) || c > _stack.Count || !CallOtherSubr(o, (int)c))
                                {
                                    return Outcome.Failed;
                                }

                                break;
                            case 17: // pop: takes a result of the last callothersubr
                                if (_knownResults > 0)
                                {
                                    // Already on the stack, as FreeType puts it there.
                                    _knownResults--;
                                }
                                else if (_unknownResults is { } results && _nextUnknown < results.Count && _stack.Count < MaxStack)
                                {
                                    _stack.Add(results[_nextUnknown++]);
                                }
                                else
                                {
                                    return Outcome.Failed;
                                }

                                break;
                            case 0 or 1 or 2 or 7 or 33: // dotsection, vstem3, hstem3, sbw, setcurrentpoint
                                _stack.Clear();
                                break;
                            default:
                                return Outcome.Failed;
                        }

                        break;
                    case 1 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 13 or 21 or 22 or 30 or 31:
                        // hstem, vstem, vmoveto, rlineto, hlineto, vlineto, rrcurveto, closepath, hsbw,
                        // rmoveto, hmoveto, vhcurveto, hvcurveto: they use the operands and clear the stack.
                        _stack.Clear();
                        break;
                    default:
                        return Outcome.Failed;
                }
            }

            return Outcome.Return;
        }

        // As FreeType's t1decode.c: the results of the othersubrs it knows are put back on the
        // operand stack at once (the end point of a flex, the subroutine number for hint
        // replacement), and "pop" only consumes them; the arguments of others come back one per "pop".
        private bool CallOtherSubr(double number, int count)
        {
            var arguments = _stack.GetRange(_stack.Count - count, count);
            _stack.RemoveRange(_stack.Count - count, count);
            _knownResults = 0;
            _unknownResults = null;
            _nextUnknown = 0;
            switch (number)
            {
                case 0: // end of flex
                    if (count != 3)
                    {
                        return false;
                    }

                    _stack.Add(null);
                    _stack.Add(null);
                    _knownResults = 2;
                    return true;
                case 1 or 2: // start of flex, a flex point
                    return count == 0;
                case 3: // hint replacement
                    if (count != 1)
                    {
                        return false;
                    }

                    _stack.Add(arguments[0]);
                    _knownResults = 1;
                    return true;
                default:
                    _unknownResults = arguments;
                    return true;
            }
        }

        private bool TryPop(out double? value)
        {
            if (_stack.Count == 0)
            {
                value = null;
                return false;
            }

            value = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return true;
        }

        private static bool IsIndex(double value) => value >= 0 && value <= 65535 && value == Math.Floor(value);

        private static bool IsCode(double value) => value is >= 0 and <= 255 && value == Math.Floor(value);
    }

    /// <summary>A number in a Type 1 glyph program (section 6.2), or null when the program ends inside it.</summary>
    public static double? ReadNumber(byte[] program, int v, ref int i)
    {
        switch (v)
        {
            case <= 246:
                return v - 139;
            case <= 250 when i < program.Length:
                return ((v - 247) * 256) + program[i++] + 108;
            case <= 254 when i < program.Length:
                return -((v - 251) * 256) - program[i++] - 108;
            case 255 when i + 4 <= program.Length:
                var value = BinaryPrimitives.ReadInt32BigEndian(program.AsSpan(i));
                i += 4;
                return value;
            default:
                return null;
        }
    }
}
