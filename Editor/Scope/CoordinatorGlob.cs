using System;
using System.Linq;

namespace Coordinator.Editor
{
    public static class CoordinatorGlob
    {
        private const string AnyDepth = "**";

        public static string Problem(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "The pattern is empty.";
            if (pattern.StartsWith("/") || pattern.EndsWith("/"))
                return "Patterns are relative to the repository root and do not start or end with '/'.";
            if (pattern.IndexOfAny(new[] { '\\', '\'', '[', ']', '{', '}', ':' }) >= 0)
                return "Patterns use '/', '*', '**' and '?' only; backslashes, quotes, brackets, braces and ':' are not supported.";
            var segments = pattern.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
                return "Patterns contain no empty, '.' or '..' segments.";
            if (segments.Any(segment => segment.Contains(AnyDepth) && segment != AnyDepth))
                return "'**' must be a whole segment, as in 'Assets/**' or '**/*.unity'.";
            return string.Empty;
        }

        public static bool Match(string pattern, string path) => MatchSegments(pattern.Split('/'), 0, path.Split('/'), 0);

        public static bool Overlaps(string left, string right) => IntersectSegments(left.Split('/'), 0, right.Split('/'), 0);

        public static bool Covers(string outer, string inner) => Match(outer, inner);

        private static bool MatchSegments(string[] pattern, int p, string[] path, int s)
        {
            if (p == pattern.Length)
                return s == path.Length;
            if (pattern[p] == AnyDepth)
            {
                for (var skip = s; skip <= path.Length; skip++)
                    if (MatchSegments(pattern, p + 1, path, skip))
                        return true;
                return false;
            }
            return s < path.Length && MatchSegment(pattern[p], 0, path[s], 0) && MatchSegments(pattern, p + 1, path, s + 1);
        }

        private static bool MatchSegment(string pattern, int p, string text, int t)
        {
            while (p < pattern.Length)
            {
                var symbol = pattern[p];
                if (symbol == '*')
                {
                    for (var skip = t; skip <= text.Length; skip++)
                        if (MatchSegment(pattern, p + 1, text, skip))
                            return true;
                    return false;
                }
                if (t == text.Length || symbol != '?' && symbol != text[t])
                    return false;
                p++;
                t++;
            }
            return t == text.Length;
        }

        private static bool IntersectSegments(string[] left, int l, string[] right, int r)
        {
            if (l == left.Length && r == right.Length)
                return true;
            if (l < left.Length && left[l] == AnyDepth)
                return IntersectSegments(left, l + 1, right, r) || r < right.Length && IntersectSegments(left, l, right, r + 1);
            if (r < right.Length && right[r] == AnyDepth)
                return IntersectSegments(left, l, right, r + 1) || l < left.Length && IntersectSegments(left, l + 1, right, r);
            return l < left.Length && r < right.Length && SegmentsIntersect(left[l], right[r])
                   && IntersectSegments(left, l + 1, right, r + 1);
        }

        private static bool SegmentsIntersect(string left, string right)
        {
            var leftWild = left.IndexOfAny(new[] { '*', '?' }) >= 0;
            var rightWild = right.IndexOfAny(new[] { '*', '?' }) >= 0;
            if (!leftWild)
                return MatchSegment(right, 0, left, 0);
            if (!rightWild)
                return MatchSegment(left, 0, right, 0);
            var leftPrefix = Prefix(left);
            var rightPrefix = Prefix(right);
            var leftSuffix = Suffix(left);
            var rightSuffix = Suffix(right);
            return (leftPrefix.StartsWith(rightPrefix, StringComparison.Ordinal) || rightPrefix.StartsWith(leftPrefix, StringComparison.Ordinal))
                   && (leftSuffix.EndsWith(rightSuffix, StringComparison.Ordinal) || rightSuffix.EndsWith(leftSuffix, StringComparison.Ordinal));
        }

        private static string Prefix(string segment) => segment.Substring(0, segment.IndexOfAny(new[] { '*', '?' }));

        private static string Suffix(string segment) => segment.Substring(segment.LastIndexOfAny(new[] { '*', '?' }) + 1);
    }
}
