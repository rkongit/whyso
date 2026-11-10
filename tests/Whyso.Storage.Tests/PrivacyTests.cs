using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Storage.Tests;

public class RedactorTests
{
    [Theory]
    [InlineData("key AKIAIOSFODNN7EXAMPLE here", "aws-access-key")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "github-token")]
    [InlineData("export ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "api-key-sk")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig", "bearer-token")]
    [InlineData("postgres://admin:hunter2secret@db.local:5432/app", "url-credentials")]
    [InlineData("password = \"correcthorse\"", "assignment-secret")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----", "private-key")]
    public void Redacts_known_secret_shapes(string input, string rule)
    {
        var output = Redactor.RedactString(input);
        Assert.Contains($"[REDACTED:{rule}]", output);
    }

    [Fact]
    public void Url_credentials_keep_user_and_host()
    {
        var output = Redactor.RedactString("postgres://admin:hunter2secret@db.local/app");
        Assert.Equal("postgres://admin:[REDACTED:url-credentials]@db.local/app", output);
    }

    [Theory]
    [InlineData("fix the login bug in src/auth/AuthService.cs")]
    [InlineData("commit 3f2a9c1b7d4e5f60718293a4b5c6d7e8f9012345")] // SHA must survive
    [InlineData("sess_e0d2a1c5-c116-4832-bc77-7329a7759ddc")]
    [InlineData("tokens = 100")]
    public void Leaves_ordinary_text_alone(string input)
    {
        Assert.Equal(input, Redactor.RedactString(input));
    }

    [Fact]
    public void Redacts_nested_strings_but_not_keys_or_numbers()
    {
        var node = JsonNode.Parse(
            """{"tool_input":{"command":"curl -H 'Authorization: Bearer abcdefghijklmnop1234'"},"duration":12,"items":["AKIAIOSFODNN7EXAMPLE"]}""");
        var redacted = Redactor.RedactNode(node)!;
        var json = redacted.ToJsonString();
        Assert.DoesNotContain("abcdefghijklmnop1234", json);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", json);
        Assert.Equal(12, redacted["duration"]!.GetValue<int>());
        Assert.NotNull(redacted["tool_input"]!["command"]);
        // Source is untouched.
        Assert.Contains("AKIAIOSFODNN7EXAMPLE", node!.ToJsonString());
    }
}

public class EncryptionTests
{
    private const string FakeKey = "AKIAIOSFODNN7EXAMPLE";

    private static string NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-priv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var args in new[]
                 {
                     new[] { "init" },
                     new[] { "config", "user.email", "test@example.com" },
                     new[] { "config", "user.name", "Test" },
                 })
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            p.WaitForExit();
        }

        return dir;
    }

    private static AgentEvent Prompt(string sessionId, string text) => new()
    {
        Version = Protocol.Version,
        EventId = Protocol.NewEventId(),
        SessionId = sessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Agent = "cursor",
        EventType = EventType.PromptSubmitted,
        Payload = new JsonObject { ["prompt"] = text },
    };

    [Fact]
    public async Task Stored_payloads_are_redacted_and_encrypted_but_read_back_as_plaintext()
    {
        var dir = NewRepo();
        try
        {
            var (root, config) = await Store.InitProjectAsync(dir);
            await using (var store = await Store.OpenAsync(root))
            {
                await store.AppendEventAsync(Prompt("sess_a", $"deploy with {FakeKey} please"), config.ProjectId);

                var events = await store.ListEventsAsync("sess_a");
                var prompt = events.Single().Payload["prompt"]!.GetValue<string>();
                Assert.Contains("deploy with", prompt);
                Assert.Contains("[REDACTED:aws-access-key]", prompt);
            }

            // Nothing on disk contains the secret or the readable prompt.
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, ".whyso"), "*", SearchOption.AllDirectories))
            {
                var bytes = await File.ReadAllTextAsync(file);
                Assert.DoesNotContain(FakeKey, bytes);
                Assert.DoesNotContain("deploy with", bytes);
            }

            var jsonl = Directory.EnumerateFiles(Path.Combine(root, ".whyso", "events"), "*.jsonl", SearchOption.AllDirectories).Single();
            Assert.Contains(PayloadCrypto.Marker, await File.ReadAllTextAsync(jsonl));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Forgetting_a_session_makes_payloads_unreadable_without_touching_others()
    {
        var dir = NewRepo();
        try
        {
            var (root, config) = await Store.InitProjectAsync(dir);
            await using var store = await Store.OpenAsync(root);
            await store.AppendEventAsync(Prompt("sess_forget", "secret plan"), config.ProjectId);
            await store.AppendEventAsync(Prompt("sess_keep", "public plan"), config.ProjectId);

            Assert.True(store.ForgetSessionKey("sess_forget"));
            Assert.False(store.ForgetSessionKey("sess_forget"));

            var forgotten = (await store.ListEventsAsync("sess_forget")).Single();
            Assert.Null(forgotten.Payload["prompt"]);
            Assert.Equal("session key deleted", forgotten.Payload["unreadable"]!.GetValue<string>());

            var kept = (await store.ListEventsAsync("sess_keep")).Single();
            Assert.Equal("public plan", kept.Payload["prompt"]!.GetValue<string>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Ciphertext_is_bound_to_its_event()
    {
        var key = new byte[32];
        var sealedPayload = PayloadCrypto.Encrypt(key, "evt_1", "sess_1", new JsonObject { ["prompt"] = "x" });
        Assert.Equal("x", PayloadCrypto.Decrypt(key, "evt_1", "sess_1", sealedPayload)["prompt"]!.GetValue<string>());
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => PayloadCrypto.Decrypt(key, "evt_2", "sess_1", sealedPayload));
    }

    [Fact]
    public void Legacy_data_dir_is_migrated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".ai-worktree", "events"));
        File.WriteAllText(Path.Combine(dir, ".ai-worktree", "config.toml"), "x");
        try
        {
            Assert.Equal(Path.Combine(dir, ".ai-worktree"), WorktreePaths.WorktreeDir(dir));
            WorktreePaths.TryMigrateLegacyDir(dir);
            Assert.False(Directory.Exists(Path.Combine(dir, ".ai-worktree")));
            Assert.True(File.Exists(Path.Combine(dir, ".whyso", "config.toml")));
            Assert.Equal(Path.Combine(dir, ".whyso"), WorktreePaths.WorktreeDir(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
