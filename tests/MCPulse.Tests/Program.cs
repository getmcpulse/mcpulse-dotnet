using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCPulse.Tests;

/// <summary>
/// The cross-language contract, plus the parts of the SDK that only this language can get wrong.
/// </summary>
/// <remarks>
/// <para>
/// <c>canonical.json</c> is the shared conformance suite, copied from
/// <c>packages/schemas/fixtures</c> in the mcpulse monorepo. The
/// TypeScript, Python, Go and Java SDKs run the same file. If it passes in all of them, their
/// hashes are interchangeable and a customer running more than one sees one set of numbers rather
/// than several.
/// </para>
/// <para>
/// Never edit a fixture to make a failure go away — these hashes are in the product's history, and
/// rewriting one rewrites what every stored row means.
/// </para>
/// <para>
/// A plain <c>Main</c> rather than xUnit, so the suite runs with nothing but the SDK.
/// </para>
/// </remarks>
public static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        Fixtures();
        Numbers();
        Strings();
        KeyOrder();
        ArgsHash();
        EmptinessChecks();
        await Recording();

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed > 0 ? 1 : 0;
    }

    private static void Fixtures()
    {
        // Next to the assembly, not the working directory: `dotnet run` and `dotnet test`
        // launch from different places.
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "canonical.json"));
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        Check("algorithm is pinned", "sha256/rfc8785/hex12", root.GetProperty("algorithm").GetString());
        Check("wire version is pinned", 1, root.GetProperty("wire_version").GetInt32());

        var count = 0;
        foreach (var fixture in root.GetProperty("fixtures").EnumerateArray())
        {
            count++;
            var name = fixture.GetProperty("name").GetString()!;
            var input = fixture.GetProperty("input").Clone();

            Check($"canonical: {name}", fixture.GetProperty("canonical").GetString(),
                Canonical.Canonicalize(input));
            Check($"hash: {name}", fixture.GetProperty("args_hash").GetString(),
                Hashing.ArgsHash(input));

            // Each fixture's hash must match its own canonical form, so a corrupted file is
            // caught rather than silently agreed with.
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(fixture.GetProperty("canonical").GetString()!));
            Check($"self-consistent: {name}", fixture.GetProperty("args_hash").GetString(),
                Convert.ToHexStringLower(digest)[..12]);
        }

        Check("every fixture ran", true, count >= 23);
    }

    /// <summary>ECMAScript Number::toString, which is where .NET's formatting differs.</summary>
    private static void Numbers()
    {
        Check("1.0 loses the decimal", "1", Canonical.Canonicalize(1.0));
        Check("negative zero", "0", Canonical.Canonicalize(-0.0));
        Check("2.5", "2.5", Canonical.Canonicalize(2.5));
        Check("1e21", "1e+21", Canonical.Canonicalize(1e21));      // .NET writes 1E+21
        Check("1e-7", "1e-7", Canonical.Canonicalize(1e-7));       // .NET writes 1E-07
        Check("1e-6", "0.000001", Canonical.Canonicalize(1e-6));
        Check("0.1", "0.1", Canonical.Canonicalize(0.1));
        Check("min subnormal", "5e-324", Canonical.Canonicalize(double.Epsilon));
        Check("max double", "1.7976931348623157e+308", Canonical.Canonicalize(double.MaxValue));
        Check("-1.5e-9", "-1.5e-9", Canonical.Canonicalize(-1.5e-9));
        Check("2^53-1", "9007199254740991", Canonical.Canonicalize(9007199254740991L));
        Check("a million stays plain", "1000000", Canonical.Canonicalize(1_000_000));
        Check("plain integers", "42", Canonical.Canonicalize(42));

        CheckThrows("NaN is refused", () => Canonical.Canonicalize(double.NaN));
        CheckThrows("Infinity is refused", () => Canonical.Canonicalize(double.PositiveInfinity));
    }

    private static void Strings()
    {
        Check("non-ascii is literal", "\"café\"", Canonical.Canonicalize("café"));
        Check("emoji is literal", "\"\U0001F680\"", Canonical.Canonicalize("\U0001F680"));

        // System.Text.Json escapes all three of these by default, and that alone would put every
        // .NET server's hashes in a different bucket from every other SDK's.
        Check("html is not escaped", "\"a<b>c&d\"", Canonical.Canonicalize("a<b>c&d"));

        Check("short escapes", "\"\\b\\t\\n\\f\\r\\\"\\\\\"", Canonical.Canonicalize("\b\t\n\f\r\"\\"));
        Check("other control chars", "\"\\u0000\\u0001\\u001f\"", Canonical.Canonicalize("\u0000\u0001\u001f"));
    }

    private static void KeyOrder()
    {
        var nested = new Dictionary<string, object?> { ["z"] = 1, ["a"] = 2 };
        Check("sorts at every depth", "{\"o\":{\"a\":2,\"z\":1}}",
            Canonical.Canonicalize(new Dictionary<string, object?> { ["o"] = nested }));

        // U+1F680 is the surrogate pair D83D DE80, so it sorts before U+FFFD. StringComparer
        // .Ordinal gets this right for free; Python and Go both need a workaround.
        var mixed = new Dictionary<string, object?>
        {
            ["�"] = 1,
            ["\U0001F680"] = 2,
            ["é"] = 3,
            ["a"] = 4,
        };
        Check("utf-16 key order", "{\"a\":4,\"é\":3,\"\U0001F680\":2,\"�\":1}",
            Canonical.Canonicalize(mixed));

        Check("array order is left alone", "[2,1]", Canonical.Canonicalize(new[] { 2, 1 }));
    }

    private static void ArgsHash()
    {
        // A no-argument tool is an ordinary call. Sharing the failure sentinel would make every
        // such tool look broken.
        Check("absent args are {}", Hashing.ArgsHash(new Dictionary<string, object?>()),
            Hashing.ArgsHash(null));
        CheckNot("absent args are not the sentinel", Hashing.Unhashable, Hashing.ArgsHash(null));

        var circular = new Dictionary<string, object?>();
        circular["self"] = circular;
        Check("circular gets the sentinel", Hashing.Unhashable, Hashing.ArgsHash(circular));

        Check("NaN gets the sentinel", Hashing.Unhashable,
            Hashing.ArgsHash(new Dictionary<string, object?> { ["n"] = double.NaN }));

        var hash = Hashing.ArgsHash(new Dictionary<string, object?> { ["q"] = "anything" });
        Check("twelve characters", 12, hash.Length);
        Check("lowercase hex", hash, hash.ToLowerInvariant());

        var id = Hashing.NewSessionId();
        Check("session id shape", true, id.StartsWith("s_") && id.Length == 14);
        CheckNot("session ids differ", id, Hashing.NewSessionId());
    }

    private static void EmptinessChecks()
    {
        static object Text(string value) => new Dictionary<string, object?>
        {
            ["content"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = value },
            },
        };

        Check("null is empty", true, Emptiness.IsEmptyResult(null));
        Check("no parts is empty", true, Emptiness.IsEmptyResult(
            new Dictionary<string, object?> { ["content"] = new List<object?>() }));
        Check("a serialised empty list is empty", true, Emptiness.IsEmptyResult(Text("[]")));
        Check("blank text is empty", true, Emptiness.IsEmptyResult(Text("   ")));
        Check("prose is not empty", false, Emptiness.IsEmptyResult(Text("no rows found")));

        // 0 and false are results, not absences. Counting them as empty would report working
        // tools as broken.
        Check("zero is an answer", false, Emptiness.IsEmptyResult(Text("0")));
        Check("false is an answer", false, Emptiness.IsEmptyResult(Text("false")));

        // The {"result": …} envelope some SDKs add must not hide an empty answer.
        Check("the result envelope is opened", true, Emptiness.IsEmptyResult(
            new Dictionary<string, object?>
            {
                ["structuredContent"] = new Dictionary<string, object?> { ["result"] = "[]" },
            }));
    }

    // ─── Recording ───────────────────────────────────────────────────────────

    private sealed record ToolResult(List<object?> Content, bool IsError);

    private static async Task Recording()
    {
        var recorded = new List<Dictionary<string, object?>>();
        MCPulse.Stream.Sink = recorded.Add;

        McPulse.Configure(new McPulseOptions { Key = "mp_test_key", Endpoint = "http://127.0.0.1:1" });

        static ToolResult Ok(string text) => new(
            [new Dictionary<string, object?> { ["type"] = "text", ["text"] = text }], false);

        // A successful call.
        var result = await McPulse.RecordAsync("echo",
            new Dictionary<string, object?> { ["text"] = "sensitive-argument-value" },
            "test-client",
            () => Task.FromResult(Ok("hello")));

        Check("one call recorded", 1, recorded.Count);
        Check("tool name", "echo", recorded[0]["tool_name"]);
        Check("outcome", "ok", recorded[0]["outcome"]);
        Check("wire version", 1, recorded[0]["v"]);
        Check("client name", "test-client", recorded[0]["client_name"]);
        Check("result passes through", false, result.IsError);
        Check("is_empty", false, recorded[0]["is_empty"]);

        // Nothing about the arguments may reach the wire.
        var wire = JsonSerializer.Serialize(recorded);
        Check("no argument value on the wire", false, wire.Contains("sensitive-argument-value"));
        Check("args_hash is twelve characters", 12, ((string)recorded[0]["args_hash"]!).Length);

        // Argument order must not change the hash.
        recorded.Clear();
        await McPulse.RecordAsync("two",
            new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 }, null,
            () => Task.FromResult(Ok("x")));
        await McPulse.RecordAsync("two",
            new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 }, null,
            () => Task.FromResult(Ok("x")));
        Check("reordered arguments hash the same", recorded[0]["args_hash"], recorded[1]["args_hash"]);

        // A throwing handler is crashed, and the exception still reaches the server.
        recorded.Clear();
        var rethrown = false;
        try
        {
            await McPulse.RecordAsync<ToolResult>("explode", null, null,
                () => throw new InvalidOperationException("boom"));
        }
        catch (InvalidOperationException)
        {
            rethrown = true;
        }

        Check("the exception still reaches the server", true, rethrown);
        Check("outcome is crashed", "crashed", recorded[0]["outcome"]);

        // An isError result is a tool error, not a crash.
        recorded.Clear();
        await McPulse.RecordAsync("failing", null, null,
            () => Task.FromResult(new ToolResult([], true)));
        Check("outcome is tool_error", "tool_error", recorded[0]["outcome"]);

        // An empty answer is flagged.
        recorded.Clear();
        await McPulse.RecordAsync("nothing", null, null, () => Task.FromResult(Ok("[]")));
        Check("an empty result is flagged", true, recorded[0]["is_empty"]);

        // Startup, once.
        recorded.Clear();
        McPulse.RecordStartup([new Dictionary<string, object?> { ["name"] = "echo" }], "test-client");
        McPulse.RecordStartup([new Dictionary<string, object?> { ["name"] = "echo" }], "test-client");
        Check("startup is sent once", 1, recorded.Count);
        Check("startup type", "startup", recorded[0]["type"]);

        // Two configurations for the same destination are one session, not two.
        //
        // The bug this guards against is invisible in a stdio server and fatal in an HTTP one: a
        // server configured per request would open a session per request, so a retry could never
        // be detected and first-call success would report a perfect score however badly the server
        // was doing.
        McPulse.Configure(new McPulseOptions { Key = "mp_test_key", Endpoint = "http://127.0.0.1:1" });
        var firstSession = McPulse.SessionId;
        McPulse.Configure(new McPulseOptions { Key = "mp_test_key", Endpoint = "http://127.0.0.1:1" });
        Check("the same destination is one session", firstSession, McPulse.SessionId);

        // Two keys are two customers. Merging them would file one customer's calls under another's.
        McPulse.Configure(new McPulseOptions { Key = "mp_other_key", Endpoint = "http://127.0.0.1:1" });
        CheckNot("different keys are different sessions", firstSession, McPulse.SessionId);

        // Configured off means nothing is recorded at all.
        recorded.Clear();
        McPulse.Configure(new McPulseOptions { Key = "" });
        await McPulse.RecordAsync("echo", null, null, () => Task.FromResult(Ok("hi")));
        Check("an empty key records nothing", 0, recorded.Count);

        MCPulse.Stream.Sink = null;
    }

    // ─── Harness ─────────────────────────────────────────────────────────────

    private static void Check(string what, object? expected, object? actual)
    {
        if (Equals(expected, actual))
        {
            _passed++;
        }
        else
        {
            _failed++;
            Console.WriteLine($"FAIL {what}\n  want {expected}\n  got  {actual}");
        }
    }

    private static void CheckNot(string what, object? forbidden, object? actual)
    {
        if (!Equals(forbidden, actual))
        {
            _passed++;
        }
        else
        {
            _failed++;
            Console.WriteLine($"FAIL {what}: got the forbidden value {forbidden}");
        }
    }

    private static void CheckThrows(string what, Action body)
    {
        try
        {
            body();
            _failed++;
            Console.WriteLine($"FAIL {what}: nothing was thrown");
        }
        catch (Exception)
        {
            _passed++;
        }
    }
}
