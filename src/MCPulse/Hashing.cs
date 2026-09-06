using System.Security.Cryptography;
using System.Text;

namespace MCPulse;

/// <summary>Fingerprinting a call's arguments.</summary>
public static class Hashing
{
    /// <summary>What an argument set hashes to when it cannot be serialised at all.</summary>
    public const string Unhashable = "000000000000";

    /// <summary>
    /// A short, one-way fingerprint of a call's arguments.
    /// </summary>
    /// <remarks>
    /// This is the only thing MCPulse ever learns about what was passed to a tool, and it is
    /// deliberately not enough to learn anything: 12 hex characters of a SHA-256 over the RFC 8785
    /// canonical form, with no way back. All the product asks of it is "were these two calls made
    /// with the same arguments or different ones" — which is what separates a model retrying a
    /// reworded request from a client paging through results.
    /// </remarks>
    public static string ArgsHash(object? args)
    {
        // A tool that takes no arguments is called with arguments absent. That is an ordinary
        // call, not a failure, and it hashes as the empty object it is — otherwise every
        // no-argument tool shares one hash with every call whose arguments blew up.
        var value = args ?? new Dictionary<string, object?>();

        try
        {
            var canonical = Canonical.Canonicalize(value);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexStringLower(digest)[..12];
        }
        catch
        {
            // Arguments JSON cannot represent. The call still happened and still deserves a row;
            // it simply cannot be compared to another, so give it a constant that says exactly
            // that.
            return Unhashable;
        }
    }

    /// <summary>
    /// Identifies one run of the customer's server, so calls can be grouped and a cost-per-session
    /// worked out.
    /// </summary>
    /// <remarks>
    /// Random rather than derived — there is nothing about the process worth encoding here, and
    /// anything derived from the machine would be an identifier we did not intend to collect.
    /// </remarks>
    public static string NewSessionId() =>
        "s_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
}
