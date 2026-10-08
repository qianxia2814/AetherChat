namespace AetherChat.Core;

public enum ChatChannel { Tell, Say, Party, Alliance, FreeCompany }
public enum ApiProtocol { ChatCompletions, OpenAiResponses, AnthropicMessages }

public sealed class ChannelRule
{
    public bool Enabled { get; set; }
    public bool OverrideKeywords { get; set; }
    public string Keywords { get; set; } = "";
}

public sealed class ConnectionProfile
{
    public string Name { get; set; } = "本机 / OpenAI 兼容";
    public ApiProtocol Protocol { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string Model { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
}

public class ChatSettings
{
    public List<ConnectionProfile> Profiles { get; set; } =
    [
        new(),
        new() { Name = "GPT / OpenAI", Protocol = ApiProtocol.OpenAiResponses, BaseUrl = "https://api.openai.com/v1" },
        new() { Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1" },
        new() { Name = "Claude / Anthropic", Protocol = ApiProtocol.AnthropicMessages, BaseUrl = "https://api.anthropic.com/v1" },
    ];
    public int ActiveProfile { get; set; }
    public Dictionary<ChatChannel, ChannelRule> Channels { get; set; } = Enum.GetValues<ChatChannel>().ToDictionary(c => c, _ => new ChannelRule());
    public string Keywords { get; set; } = "";
    public string SystemPrompt { get; set; } = "你在最终幻想14中参与聊天。用自然简短的中文回复当前发言者，一般一到两句话。只输出准备发送的聊天正文，不要输出分析过程、Markdown、游戏命令或聊天宏。";
    public string ReplyPrefix { get; set; } = "";
    public string IgnoredPlayers { get; set; } = "";
    public int CooldownSeconds { get; set; } = 10;
    public int RepliesPerMinute { get; set; } = 6;
    public int TimeoutSeconds { get; set; } = 45;
    public int ContextPairs { get; set; } = 6;
    public int MaxReplyBytes { get; set; } = 240;
    public int MaxOutputTokens { get; set; } = 1024;
    public bool PreviewOnly { get; set; }
    public bool PauseInCombat { get; set; }

    public void Normalize()
    {
        Profiles ??= [new()];
        if (Profiles.Count == 0) Profiles.Add(new());
        ActiveProfile = Math.Clamp(ActiveProfile, 0, Profiles.Count - 1);
        Channels ??= [];
        foreach (var c in Enum.GetValues<ChatChannel>())
            if (!Channels.ContainsKey(c) || Channels[c] is null) Channels[c] = new();
        CooldownSeconds = Math.Clamp(CooldownSeconds, 3, 120);
        RepliesPerMinute = Math.Clamp(RepliesPerMinute, 1, 20);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 120);
        ContextPairs = Math.Clamp(ContextPairs, 0, 20);
        MaxReplyBytes = Math.Clamp(MaxReplyBytes, 30, 400);
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, 64, 8192);
    }
}

public sealed record ChatInput(ChatChannel Channel, string SenderName, string SenderWorld, string Text, bool IsSelf = false)
{
    public string SenderId => $"{SenderName}@{SenderWorld}";
    public string ConversationKey => Channel == ChatChannel.Tell ? $"Tell:{SenderId}" : Channel.ToString();
}
public sealed record Turn(string Role, string Content);
public sealed record CompletionOptions(ApiProtocol Protocol, string BaseUrl, string Model, string ApiKey, string SystemPrompt, int MaxTokens, int TimeoutSeconds);
public interface ICompletionClient
{
    Task<string> CompleteAsync(CompletionOptions options, IReadOnlyList<Turn> messages, CancellationToken cancellationToken);
}
