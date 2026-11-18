using System.Text;

namespace PaperKnife.Core.Editing;

/// <summary>
/// The smallest change to a run's text objects that gives it new text. The objects
/// <see cref="First"/> to <see cref="Last"/> (positions in the run) are replaced by one object
/// drawing <see cref="NewText"/>, which may be empty; objects before them keep their place, and
/// objects after them keep their characters and move along the baseline as a block.
/// </summary>
internal readonly record struct TextRunEdit(int First, int Last, string NewText)
{
    /// <param name="pieces">The text of each object in the run, in order.</param>
    /// <param name="separators">The text between consecutive objects: a space PDFium found in the gap, or nothing.</param>
    /// <param name="newText">The run's new text, without trailing whitespace.</param>
    public static TextRunEdit Plan(IReadOnlyList<string> pieces, IReadOnlyList<string> separators, string newText)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pieces.Count);
        var starts = new int[pieces.Count];
        var ends = new int[pieces.Count];
        var builder = new StringBuilder();
        for (var i = 0; i < pieces.Count; i++)
        {
            starts[i] = builder.Length;
            builder.Append(pieces[i]);
            ends[i] = builder.Length;
            if (i < separators.Count)
            {
                builder.Append(separators[i]);
            }
        }

        var old = builder.ToString();

        // Trailing whitespace isn't part of the editable text; it stays as it is.
        var target = string.Concat(newText, old.AsSpan(old.TrimEnd().Length));

        var prefix = 0;
        var limit = Math.Min(old.Length, target.Length);
        while (prefix < limit && old[prefix] == target[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < limit - prefix && old[^(suffix + 1)] == target[^(suffix + 1)])
        {
            suffix++;
        }

        // Never split a surrogate pair between the kept and the changed text.
        if (prefix > 0 && char.IsHighSurrogate(old[prefix - 1]))
        {
            prefix--;
        }

        if (suffix > 0 && char.IsLowSurrogate(old[^suffix]))
        {
            suffix--;
        }

        int first, last;
        var changedEnd = old.Length - suffix;
        if (changedEnd > prefix)
        {
            first = PieceAtOrBefore(prefix);
            last = PieceAtOrAfter(changedEnd - 1);
        }
        else
        {
            // Pure insertion: it joins the object it follows, or the next object after a gap.
            first = last = prefix == 0 ? 0 : PieceAtOrAfter(prefix - 1);
        }

        var newEnd = ends[last] + (target.Length - old.Length);
        return new TextRunEdit(first, last, target[starts[first]..newEnd]);

        // The object containing a position, or the one before the gap containing it.
        int PieceAtOrBefore(int position)
        {
            var piece = 0;
            while (piece + 1 < starts.Length && starts[piece + 1] <= position)
            {
                piece++;
            }

            return piece;
        }

        // The object containing a position, or the one after the gap containing it.
        int PieceAtOrAfter(int position)
        {
            var piece = 0;
            while (piece + 1 < ends.Length && ends[piece] <= position)
            {
                piece++;
            }

            return piece;
        }
    }
}
