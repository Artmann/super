namespace Super.Cook;

/// <summary>Matches rule patterns against paths. <c>*</c> stays inside one segment, <c>**</c> as a whole segment crosses any number of them.</summary>
internal static class Glob
{
    public static bool Matches(string pattern, string relative)
    {
        return MatchSegments(pattern.Split('/'), 0, relative.Split('/'), 0);
    }

    private static bool MatchSegments(string[] pattern, int p, string[] path, int s)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == "**")
            {
                for (int skip = s; skip <= path.Length; skip++)
                {
                    if (MatchSegments(pattern, p + 1, path, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (s >= path.Length || !MatchSegment(pattern[p], 0, path[s], 0))
            {
                return false;
            }

            p++;
            s++;
        }

        return s == path.Length;
    }

    private static bool MatchSegment(string pattern, int p, string text, int t)
    {
        while (p < pattern.Length)
        {
            char c = pattern[p];
            if (c == '*')
            {
                while (p < pattern.Length && pattern[p] == '*')
                {
                    p++;
                }

                for (int skip = t; skip <= text.Length; skip++)
                {
                    if (MatchSegment(pattern, p, text, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (t >= text.Length || (c != '?' && c != text[t]))
            {
                return false;
            }

            p++;
            t++;
        }

        return t == text.Length;
    }
}
