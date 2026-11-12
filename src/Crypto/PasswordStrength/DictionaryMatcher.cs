namespace Crypto.PasswordStrength;

public sealed class DictionaryMatcher
{
    private static readonly Dictionary<char, char[]> L33t = new()
    {
        ['@'] = ['a'], ['4'] = ['a'], ['8'] = ['b'], ['('] = ['c'],
        ['3'] = ['e'], ['6'] = ['g'], ['1'] = ['l', 'i'], ['0'] = ['o'],
        ['$'] = ['s'], ['5'] = ['s'], ['7'] = ['t'], ['!'] = ['i'], ['|'] = ['l', 'i'],
    };

    private readonly TrieNode _root = new();
    private readonly int _maximumWordLength;

    public DictionaryMatcher(FrequencyDictionaries dictionaries)
    {
        ArgumentNullException.ThrowIfNull(dictionaries);
        foreach (var entry in dictionaries.Passwords.Concat(dictionaries.English))
        {
            if (entry.Key.Length < 2)
                continue;

            _maximumWordLength = Math.Max(_maximumWordLength, entry.Key.Length);
            var node = _root;
            foreach (var character in entry.Key)
            {
                if (!node.Children.TryGetValue(character, out var next))
                {
                    next = new TrieNode();
                    node.Children.Add(character, next);
                }
                node = next;
            }

            // Password-frequency ranks take precedence over English-word ranks,
            // matching the original two-dictionary lookup order.
            if (node.Rank == 0)
                node.Rank = entry.Value;
        }
    }

    public IReadOnlyList<Match> Match(string password)
    {
        PasswordStrengthBudget.ValidateMatcherInput(password);
        var result = new List<Match>();
        var lower = password.ToLowerInvariant();
        ScanDirect(lower, lower, reversed: false, result);

        var reversed = lower.ToCharArray();
        Array.Reverse(reversed);
        ScanDirect(new string(reversed), lower, reversed: true, result);
        ScanL33t(lower, result);
        return result;
    }

    private void ScanDirect(string source, string original, bool reversed, List<Match> result)
    {
        for (var start = 0; start + 1 < source.Length; start++)
        {
            var node = _root;
            var limit = Math.Min(source.Length, start + _maximumWordLength);
            for (var end = start; end < limit; end++)
            {
                if (!node.Children.TryGetValue(source[end], out var next))
                    break;
                node = next;
                if (node.Rank == 0)
                    continue;

                var length = end - start + 1;
                var originalStart = reversed ? original.Length - end - 1 : start;
                result.Add(new Match(
                    originalStart,
                    originalStart + length - 1,
                    original.Substring(originalStart, length),
                    StrengthMatchType.Dictionary,
                    Rank: node.Rank,
                    Reversed: reversed));
            }
        }
    }

    private void ScanL33t(string source, List<Match> result)
    {
        if (!source.Any(L33t.ContainsKey))
            return;

        var current = new List<TrieState>();
        var next = new List<TrieState>();
        for (var start = 0; start + 1 < source.Length; start++)
        {
            current.Clear();
            current.Add(new TrieState(_root, false));
            var limit = Math.Min(source.Length, start + _maximumWordLength);
            for (var end = start; end < limit && current.Count > 0; end++)
            {
                next.Clear();
                var character = source[end];
                foreach (var state in current)
                {
                    AddCandidate(state, character, state.HasSubstitution, start, end, source, next, result);
                    if (L33t.TryGetValue(character, out var replacements))
                    {
                        foreach (var replacement in replacements)
                            AddCandidate(state, replacement, true, start, end, source, next, result);
                    }
                }
                (current, next) = (next, current);
            }
        }
    }

    private static void AddCandidate(
        TrieState state,
        char candidate,
        bool hasSubstitution,
        int start,
        int end,
        string source,
        List<TrieState> next,
        List<Match> result)
    {
        if (!state.Node.Children.TryGetValue(candidate, out var node))
            return;
        next.Add(new TrieState(node, hasSubstitution));
        if (hasSubstitution && node.Rank > 0)
        {
            result.Add(new Match(
                start,
                end,
                source.Substring(start, end - start + 1),
                StrengthMatchType.Dictionary,
                Rank: node.Rank,
                L33t: true));
        }
    }

    private sealed class TrieNode
    {
        internal Dictionary<char, TrieNode> Children { get; } = new();
        internal int Rank { get; set; }
    }

    private readonly record struct TrieState(TrieNode Node, bool HasSubstitution);
}
