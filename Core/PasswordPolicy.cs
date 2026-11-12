namespace FMSC_SMS.Core;

/// <summary>One password-creation policy shared by every account screen.</summary>
public static class PasswordPolicy
{
    // Account owners choose their password length; reject only empty or whitespace-only input.
    public static string Requirement => "Enter a password (any length; spaces alone are not allowed).";

    public static bool IsValid(string? password) =>
        !string.IsNullOrWhiteSpace(password);
}
