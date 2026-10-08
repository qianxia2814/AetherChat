using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AetherChat;
using AetherChat.Core;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected InvalidOperationException"); }
ChatSettings Settings() { var s = new ChatSettings(); foreach (var c in s.Channels.Values) c.Enabled = true; return s; }
CompletionOptions Options(ApiProtocol p = ApiProtocol.ChatCompletions) => new(p, "http://127.0.0.1:1234/v1", "test-model", "unit-test-key", "persona", 100, 5);
ChatInput Input(ChatChannel c = ChatChannel.Party, string sender = "浅夏", string world = "紫水栈桥", string text = "你好") => new(c, sender, world, text);
async Task Finish(ChatEngine engine, Func<ChatInput, string, bool>? sink = null)
{
    for (var i = 0; i < 500 && engine.Busy; i++) { await Task.Delay(2); engine.Tick(sink ?? ((_, _) => true)); }
    Check(!engine.Busy, "Completion was not drained");
}

Test("Blank / whitespace / empty keyword list matches all", () => { foreach (var words in new[] { "", "  \n ", ",，;；" }) Check(ChatRules.MatchesKeywords("hello", words)); });
Test("Keywords are OR substrings, case insensitive, Chinese separators", () => { Check(ChatRules.MatchesKeywords("Hi AI", "小龙，ai；你好")); Check(!ChatRules.MatchesKeywords("路过", "小龙,AI")); });
Test("Disabled channel, self, empty and ignored players are rejected", () =>
{
    var s = Settings(); s.Channels[ChatChannel.Say].Enabled = false;
    Check(!ChatRules.Accept(s, Input(ChatChannel.Say))); Check(!ChatRules.Accept(s, Input() with { IsSelf = true }));
    Check(!ChatRules.Accept(s, Input(text: " "))); s.IgnoredPlayers = "浅夏@紫水栈桥";
    Check(!ChatRules.Accept(s, Input())); Check(ChatRules.Accept(s, Input(world: "神意之地")));
});
Test("Per-channel empty override means all, even with global keywords", () =>
{
    var s = Settings(); s.Keywords = "龙"; Check(!ChatRules.Accept(s, Input()));
    s.Channels[ChatChannel.Party].OverrideKeywords = true; Check(ChatRules.Accept(s, Input()));
    s.Channels[ChatChannel.Party].Keywords = "猫"; Check(!ChatRules.Accept(s, Input()));
});
Test("Regression: cross-world party outside duty can reply with empty normal party list", () =>
{
    var crossWorld = new PartyChatState(0, false, true, false, "0|0:111,222");
    Check(crossWorld.RejectionFor(ChatChannel.Party) is null);
    Check(crossWorld.RejectionFor(ChatChannel.Alliance) is not null);
});
Test("Ordinary and cross-world alliances retain channel membership checks", () =>
{
    var solo = new PartyChatState(0, false, false, false, "");
    Check(solo.RejectionFor(ChatChannel.Party) is not null);
    Check(solo.RejectionFor(ChatChannel.Alliance) is not null);
    Check(solo.RejectionFor(ChatChannel.Say) is null);
    Check(new PartyChatState(2, false, false, false, "").RejectionFor(ChatChannel.Party) is null);
    Check(new PartyChatState(8, true, false, false, "").RejectionFor(ChatChannel.Alliance) is null);
    Check(new PartyChatState(0, false, true, true, "0|0:111,222|1:333,444").RejectionFor(ChatChannel.Alliance) is null);
});
Test("All five destinations have fixed chat commands", () =>
{
    var expected = new[] { "/tell 浅夏@紫水栈桥 ", "/s ", "/p ", "/a ", "/fc " };
    foreach (var c in Enum.GetValues<ChatChannel>()) Check(ChatRules.BuildCommand(Input(c), "回复") == expected[(int)c] + "回复");
});
Test("Tell recipient cannot be a command or unknown world", () =>
{
    Throws(() => ChatRules.BuildCommand(Input(ChatChannel.Tell, "A\n/logout"), "回复"));
    Throws(() => ChatRules.BuildCommand(Input(ChatChannel.Tell, world: ""), "回复"));
    Check(ChatRules.BuildCommand(Input(ChatChannel.Tell, "First Last", "Omega"), "Hi").StartsWith("/tell First Last@Omega "));
});
Test("UTF-8 clipping preserves Chinese / emoji and total command limit", () =>
{
    var original = string.Concat(Enumerable.Repeat("你好🐉", 100));
    for (var bytes = 3; bytes < 250; bytes++)
    {
        var clipped = ChatRules.TruncateUtf8(original, bytes);
        Check(Encoding.UTF8.GetByteCount(clipped) <= bytes); Check(!clipped.Contains('�'));
    }
    Check(Encoding.UTF8.GetByteCount(ChatRules.BuildCommand(Input(ChatChannel.Tell), original)) <= 500);
});
Test("Replies cannot inject new chat lines or game text macros", () =>
{
    var result = ChatRules.BuildCommand(Input(), "/logout\n/s hi <se.1>\u0000\uE000");
    Check(result.StartsWith("/p /logout ")); Check(!result.Contains('\n') && !result.Contains('<') && !result.Contains('\0') && !result.Contains('\uE000'));
});
Test("Thinking blocks and unfinished thinking are not sent", () =>
{
    Check(ChatRules.CleanReply("<think>secret</think>你好", "", 240) == "你好");
    Check(ChatRules.CleanReply("<think>secret", "[AI]", 240) == "");
});
Test("Endpoint validation rejects remote HTTP, URL credentials and full endpoint", () =>
{
    Throws(() => AiClient.Endpoint(Options() with { BaseUrl = "http://example.com/v1" }));
    Throws(() => AiClient.Endpoint(Options() with { BaseUrl = "https://user:pass@example.com/v1" }));
    Throws(() => AiClient.Endpoint(Options() with { BaseUrl = "http://localhost:1234/v1/chat/completions" }));
    Check(AiClient.Endpoint(Options()).AbsolutePath == "/v1/chat/completions");
});
Test("Windows DPAPI round trip; persisted value is not the key", () =>
{
    var cipher = SecretProtection.Protect("sample-key-for-test-only");
    Check(!cipher.Contains("sample-key")); Check(SecretProtection.Unprotect(cipher) == "sample-key-for-test-only");
    Check(SecretProtection.Unprotect("") == ""); Throws(() => SecretProtection.Unprotect("broken"));
});

foreach (var protocol in Enum.GetValues<ApiProtocol>())
{
    AsyncTest($"{protocol}: request headers/body and response parsing", async () =>
    {
        var handler = new Handler(async (request, token) =>
        {
            Check(request.Method == HttpMethod.Post);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            Check(root.GetProperty("model").GetString() == "test-model");
            Check(!root.GetProperty("stream").GetBoolean());
            string body;
            if (protocol == ApiProtocol.AnthropicMessages)
            {
                Check(request.RequestUri!.AbsolutePath == "/v1/messages");
                Check(request.Headers.GetValues("x-api-key").Single() == "unit-test-key");
                Check(request.Headers.GetValues("anthropic-version").Single() == "2023-06-01");
                Check(root.GetProperty("system").GetString() == "persona");
                Check(root.GetProperty("messages")[0].GetProperty("role").GetString() == "user");
                body = """{"content":[{"type":"thinking","thinking":"private"},{"type":"text","text":"你好"}]}""";
            }
            else if (protocol == ApiProtocol.OpenAiResponses)
            {
                Check(request.RequestUri!.AbsolutePath == "/v1/responses"); Check(!root.GetProperty("store").GetBoolean());
                Check(root.GetProperty("instructions").GetString() == "persona");
                Check(root.GetProperty("max_output_tokens").GetInt32() == 100);
                body = """{"output":[{"type":"reasoning","summary":[]},{"type":"message","content":[{"type":"output_text","text":"你好"}]}]}""";
            }
            else
            {
                Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions");
                Check(root.GetProperty("messages")[0].GetProperty("role").GetString() == "system");
                Check(root.GetProperty("max_tokens").GetInt32() == 100);
                body = """{"choices":[{"message":{"content":"你好","reasoning_content":"private"}}]}""";
            }
            if (protocol != ApiProtocol.AnthropicMessages) Check(request.Headers.Authorization?.Parameter == "unit-test-key");
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        using var client = new AiClient(handler);
        Check(await client.CompleteAsync(Options(protocol), [new("user", "hello")], default) == "你好");
    });
}
AsyncTest("HTTP errors do not expose response bodies or credentials", async () =>
{
    using var client = new AiClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-token") })));
    try { await client.CompleteAsync(Options(), [new("user", "x")], default); throw new Exception("Expected error"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("401") && !ex.Message.Contains("secret-token")); }
});
AsyncTest("Timeout cancels a hung provider", async () =>
{
    using var client = new AiClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
    try { await client.CompleteAsync(Options() with { TimeoutSeconds = 1 }, [new("user", "x")], default); throw new Exception("Expected timeout"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("超时")); }
});
AsyncTest("HTTP response size is bounded", async () =>
{
    using var client = new AiClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2_000_001)) })));
    try { await client.CompleteAsync(Options(), [], default); throw new Exception("Expected rejection"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("过大")); }
});

AsyncTest("Tell keeps original recipient; busy messages do not replace it", async () =>
{
    var fake = new ControlledClient(); using var engine = new ChatEngine(Settings(), fake); engine.SetEnabled(true);
    Check(engine.Receive(Input(ChatChannel.Tell, "甲"), () => Options()));
    var request = await fake.Next();
    Check(!engine.Receive(Input(ChatChannel.Tell, "乙"), () => Options()));
    request.Reply.SetResult("你好"); string? command = null;
    await Finish(engine, (input, text) => { command = ChatRules.BuildCommand(input, text); return true; });
    Check(command == "/tell 甲@紫水栈桥 你好" && engine.Skipped == 1);
});
AsyncTest("Disable cancels in-flight request and rejects late provider result", async () =>
{
    var fake = new ControlledClient(); using var engine = new ChatEngine(Settings(), fake); engine.SetEnabled(true);
    engine.Receive(Input(), () => Options()); var request = await fake.Next(); engine.SetEnabled(false);
    Check(request.Token.IsCancellationRequested); request.Reply.SetResult("late"); await Task.Delay(20);
    var sends = 0; engine.Tick((_, _) => { sends++; return true; }); Check(sends == 0 && !engine.Busy);
});
AsyncTest("Session change cancels old result even after enabling again", async () =>
{
    var fake = new ControlledClient(); var time = DateTimeOffset.UtcNow;
    using var engine = new ChatEngine(Settings(), fake, () => time); engine.SetEnabled(true);
    engine.Receive(Input(), () => Options()); var old = await fake.Next(); engine.Reset("new session"); time += TimeSpan.FromSeconds(20);
    engine.Receive(Input(), () => Options()); var current = await fake.Next(); old.Reply.SetResult("old"); current.Reply.SetResult("new");
    var results = new List<string>(); await Finish(engine, (_, text) => { results.Add(text); return true; }); Check(results.SequenceEqual(["new"]));
});
AsyncTest("Channel disabled before completion prevents sending", async () =>
{
    var fake = new ControlledClient(); var settings = Settings(); using var engine = new ChatEngine(settings, fake); engine.SetEnabled(true);
    engine.Receive(Input(), () => Options()); var request = await fake.Next(); settings.Channels[ChatChannel.Party].Enabled = false;
    request.Reply.SetResult("answer"); var sends = 0; await Finish(engine, (_, _) => { sends++; return true; }); Check(sends == 0);
});
AsyncTest("Cross-world party change invalidates pending reply even if normal list stays empty", async () =>
{
    var original = new PartyChatState(0, false, true, false, "0|0:111,222");
    var changed = new PartyChatState(0, false, true, false, "0|0:111,333");
    var left = new PartyChatState(0, false, false, false, "");
    Check(original.SessionKey != changed.SessionKey && original.SessionKey != left.SessionKey);
    var fake = new ControlledClient(); using var engine = new ChatEngine(Settings(), fake); engine.SetEnabled(true);
    engine.Receive(Input(), () => Options()); var request = await fake.Next();
    if (original.SessionKey != changed.SessionKey) engine.Reset("cross-world party changed");
    Check(request.Token.IsCancellationRequested); request.Reply.SetResult("late reply"); await Task.Delay(20);
    var sends = 0; engine.Tick((_, _) => { sends++; return true; }); Check(sends == 0);
});
AsyncTest("Specific send rejection is visible instead of generic game-state failure", async () =>
{
    var fake = new ControlledClient(); using var engine = new ChatEngine(Settings(), fake); engine.SetEnabled(true);
    engine.Receive(Input(), () => Options()); (await fake.Next()).Reply.SetResult("reply");
    await Finish(engine, (input, _) => throw new InvalidOperationException(new PartyChatState(0, false, false, false, "").RejectionFor(input.Channel)));
    Check(engine.Status.Contains("普通小队或跨服小队") && engine.Submitted == 0);
});
AsyncTest("Cooldown and per-minute limits cannot be bypassed by toggling", async () =>
{
    var fake = new ControlledClient(); var settings = Settings(); settings.RepliesPerMinute = 1; var time = DateTimeOffset.UtcNow;
    using var engine = new ChatEngine(settings, fake, () => time); engine.SetEnabled(true);
    engine.Receive(Input(), () => Options()); (await fake.Next()).Reply.SetResult("reply"); await Finish(engine);
    Check(!engine.Receive(Input(), () => Options())); engine.SetEnabled(false); engine.SetEnabled(true); time += TimeSpan.FromSeconds(20);
    Check(!engine.Receive(Input(), () => Options())); time += TimeSpan.FromSeconds(41); Check(engine.Receive(Input(), () => Options()));
    (await fake.Next()).Reply.SetResult("reply"); await Finish(engine);
});
AsyncTest("History is isolated by channel and tell sender+world", async () =>
{
    var fake = new ControlledClient(); var time = DateTimeOffset.UtcNow; var settings = Settings(); settings.RepliesPerMinute = 20;
    using var engine = new ChatEngine(settings, fake, () => time); engine.SetEnabled(true);
    async Task<int> Round(ChatInput input)
    {
        time += TimeSpan.FromSeconds(20); Check(engine.Receive(input, () => Options())); var request = await fake.Next();
        request.Reply.SetResult("reply"); await Finish(engine); return request.Turns.Count;
    }
    Check(await Round(Input(ChatChannel.Tell, "甲")) == 1);
    Check(await Round(Input(ChatChannel.Tell, "乙")) == 1);
    Check(await Round(Input(ChatChannel.Tell, "甲", "神意之地")) == 1);
    Check(await Round(Input(ChatChannel.Tell, "甲")) == 3);
    Check(await Round(Input(ChatChannel.Party)) == 1);
    Check(await Round(Input(ChatChannel.FreeCompany)) == 1);
});
AsyncTest("Preview mode never calls the game sender", async () =>
{
    var fake = new ControlledClient(); var s = Settings(); s.PreviewOnly = true;
    using var engine = new ChatEngine(s, fake); engine.SetEnabled(true); engine.Receive(Input(), () => Options());
    (await fake.Next()).Reply.SetResult("preview"); var sends = 0; await Finish(engine, (_, _) => { sends++; return true; });
    Check(sends == 0 && engine.LastReply == "preview");
});
AsyncTest("Actual loopback HTTP request round trip (synthetic data only)", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var socket = await listener.AcceptTcpClientAsync(); await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        Check((await reader.ReadLineAsync()) == "POST /v1/chat/completions HTTP/1.1");
        int length = 0; string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
        var chars = new char[length]; var read = 0;
        // This test request serializes non-ASCII using JSON escapes, so char and byte lengths agree.
        while (read < length) { var n = await reader.ReadAsync(chars.AsMemory(read)); if (n == 0) break; read += n; }
        using var body = JsonDocument.Parse(new string(chars)); Check(body.RootElement.GetProperty("model").GetString() == "test-model");
        var json = """{"choices":[{"message":{"content":"wire-ok"}}]}""";
        var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\nConnection: close\r\n\r\n{json}");
        await stream.WriteAsync(response);
    });
    using var client = new AiClient();
    var reply = await client.CompleteAsync(Options() with { BaseUrl = $"http://127.0.0.1:{port}/v1" }, [new("user", "local-test")], default);
    Check(reply == "wire-ok"); await server.WaitAsync(TimeSpan.FromSeconds(5));
});

var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " :: " + ex.Message); }
}
Console.WriteLine($"RESULT: {tests.Count - failed}/{tests.Count} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken); }
sealed class ControlledClient : ICompletionClient
{
    public sealed record Request(IReadOnlyList<Turn> Turns, CancellationToken Token, TaskCompletionSource<string> Reply);
    private readonly Channel<Request> queue = Channel.CreateUnbounded<Request>();
    public Task<string> CompleteAsync(CompletionOptions options, IReadOnlyList<Turn> messages, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Writer.TryWrite(new(messages, cancellationToken, reply)); return reply.Task;
    }
    public async Task<Request> Next() => await queue.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
}
