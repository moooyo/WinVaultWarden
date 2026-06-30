namespace Crypto.PasswordStrength;

public enum MatchType { Dictionary, Repeat, Sequence, Spatial, Date, Bruteforce }

public sealed record Match(
    int I, int J, string Token, MatchType Type,
    double Guesses = 0, int Rank = 0, bool Reversed = false, bool L33t = false);

public sealed record PasswordStrengthResult(int Score, double Guesses);
