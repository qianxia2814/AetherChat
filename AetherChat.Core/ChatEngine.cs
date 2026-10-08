using System.Collections.Concurrent;

namespace AetherChat.Core;

// All public state is accessed on the game framework thread. Workers only enqueue immutable completions.
public sealed class ChatEngine : IDisposable
{
    private sealed record Completed(long Epoch, ChatInput Input, string UserText, string? Reply, string? Error, DateTimeOffset Started);
    private readonly ICompletionClient client;
    private readonly Func<DateTimeOffset> now;
    private readonly ConcurrentQueue<Completed> completed = new();
    private readonly Dictionary<string, List<Turn>> history = new();
    private readonly Queue<DateTimeOffset> attempts = new();
    private CancellationTokenSource? active;
    private DateTimeOffset nextAllowed;
    private long epoch;
    private bool disposed;
    public bool Enabled { get; private set; }
    public bool Busy { get; private set; }
    public int Skipped { get; private set; }
    public int Submitted { get; private set; }
    public string Status { get; private set; } = "已关闭";
    public string LastReply { get; private set; } = "";
    public string LastSender { get; private set; } = "";
    public ChatSettings Settings { get; }
    public ChatEngine(ChatSettings settings, ICompletionClient client, Func<DateTimeOffset>? clock = null) { Settings = settings; this.client = client; now = clock ?? (() => DateTimeOffset.UtcNow); }

    public void SetEnabled(bool enabled)
    {
        if (disposed) return;
        Reset("已取消旧请求并清空上下文");
        Enabled = enabled;
        Status = enabled ? "等待已开启频道的消息" : "已关闭";
    }

    public void Reset(string reason)
    {
        ++epoch;
        active?.Cancel();
        active?.Dispose();
        active = null;
        Busy = false;
        history.Clear();
        while (completed.TryDequeue(out _)) { }
        LastReply = "";
        LastSender = "";
        Status = reason;
    }

    public bool Receive(ChatInput input, Func<CompletionOptions> optionsFactory, bool manual = false)
    {
        if (disposed || !Enabled || !ChatRules.Accept(Settings, input, manual)) return false;
        var time = now();
        while (attempts.TryPeek(out var stamp) && time - stamp >= TimeSpan.FromMinutes(1)) attempts.Dequeue();
        if (Busy || time < nextAllowed || attempts.Count >= Settings.RepliesPerMinute) { ++Skipped; return false; }
        CompletionOptions options;
        try { options = optionsFactory(); _ = AiClient.Endpoint(options); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Status = ex.Message; return false; }
        var key = input.ConversationKey;
        var turns = history.TryGetValue(key, out var past) ? past.TakeLast(Settings.ContextPairs * 2).ToList() : [];
        var user = $"{input.SenderName}（{input.Channel}）: {ChatRules.TruncateUtf8(input.Text, 4000)}";
        turns.Add(new("user", user));
        active = new CancellationTokenSource();
        var token = active.Token;
        var requestEpoch = epoch;
        Busy = true;
        Status = "正在生成回复…";
        LastSender = input.SenderId;
        attempts.Enqueue(time);
        nextAllowed = time.AddSeconds(Settings.CooldownSeconds);
        // No game objects or mutable configuration are read in this worker.
        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await client.CompleteAsync(options, turns, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested) completed.Enqueue(new(requestEpoch, input, user, reply, null, time));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Never include raw provider bodies or credentials in the game log.
                var error = ex is InvalidOperationException ? ex.Message : "模型请求失败，请检查连接配置。";
                if (!token.IsCancellationRequested) completed.Enqueue(new(requestEpoch, input, user, null, error, time));
            }
        });
        return true;
    }

    public void Tick(Func<ChatInput, string, bool> send)
    {
        while (completed.TryDequeue(out var item))
        {
            if (disposed || item.Epoch != epoch || !Enabled) continue;
            Busy = false;
            active?.Dispose();
            active = null;
            if (item.Error is not null) { Status = item.Error; continue; }
            if (!Settings.Channels.TryGetValue(item.Input.Channel, out var rule) || !rule.Enabled) { Status = "频道已关闭，回复已丢弃"; continue; }
            if (now() - item.Started > TimeSpan.FromSeconds(Settings.TimeoutSeconds + 5)) { Status = "回复已过期，未发送"; continue; }
            var text = ChatRules.CleanReply(item.Reply ?? "", Settings.ReplyPrefix, Settings.MaxReplyBytes);
            if (text.Length == 0) { Status = "模型没有返回可发送的正文"; continue; }
            LastReply = text;
            if (Settings.PreviewOnly) { Status = "预览已生成（未发送）"; continue; }
            try
            {
                if (!send(item.Input, text)) { Status = "当前游戏状态不允许回复，已丢弃"; continue; }
                ++Submitted;
                Status = $"已提交到 {item.Input.Channel}（以游戏实际显示为准）";
                nextAllowed = now().AddSeconds(Settings.CooldownSeconds);
                if (!history.TryGetValue(item.Input.ConversationKey, out var turns))
                {
                    if (history.Count >= 64) history.Remove(history.Keys.First());
                    history[item.Input.ConversationKey] = turns = [];
                }
                turns.Add(new("user", item.UserText));
                turns.Add(new("assistant", text));
                var keep = Settings.ContextPairs * 2;
                if (turns.Count > keep) turns.RemoveRange(0, turns.Count - keep);
            }
            catch (Exception ex) { Status = ex is InvalidOperationException ? ex.Message : "提交游戏聊天失败，已停止本次回复。"; }
        }
    }

    public void Dispose() { if (disposed) return; Enabled = false; Reset("已卸载"); disposed = true; }
}
