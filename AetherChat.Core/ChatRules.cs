using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AetherChat.Core;

public static partial class ChatRules
{
    public static string[] SplitKeywords(string? text) => (text ?? "").Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static bool MatchesKeywords(string text, string? keywords)
    {
        var words = SplitKeywords(keywords);
        return words.Length == 0 || words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Accept(ChatSettings settings, ChatInput input, bool manual = false)
    {
        if (input.IsSelf || string.IsNullOrWhiteSpace(input.Text) || !settings.Channels.TryGetValue(input.Channel, out var rule) || !rule.Enabled) return false;
        if (SplitKeywords(settings.IgnoredPlayers).Any(p => p.Equals(input.SenderName, StringComparison.OrdinalIgnoreCase) || p.Equals(input.SenderId, StringComparison.OrdinalIgnoreCase))) return false;
        return manual || MatchesKeywords(input.Text, rule.OverrideKeywords ? rule.Keywords : settings.Keywords);
    }

    // Strip common reasoning wrappers, controls, FFXIV auto-translate / macro syntax and line breaks.
    public static string CleanReply(string text, string prefix, int maxBytes)
    {
        text = ThinkBlock().Replace(text, "");
        var unclosed = text.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (unclosed >= 0) text = text[..unclosed];
        text = text.Replace("```", "").Replace('<', '＜').Replace('>', '＞');
        var sb = new StringBuilder();
        foreach (var rune in (prefix + text).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune)) sb.Append(' ');
            else if (category is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate)) sb.Append(rune);
        }
        var flat = Whitespace().Replace(sb.ToString(), " ").Trim();
        if (string.IsNullOrWhiteSpace(text)) return "";
        return TruncateUtf8(flat, maxBytes);
    }

    public static string TruncateUtf8(string text, int bytes)
    {
        if (bytes <= 0) return "";
        if (Encoding.UTF8.GetByteCount(text) <= bytes) return text;
        var sb = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > bytes - 3) break;
            sb.Append(rune);
            used += rune.Utf8SequenceLength;
        }
        return bytes >= 3 ? sb + "…" : "";
    }

    public static string BuildCommand(ChatInput input, string reply)
    {
        string prefix = input.Channel switch
        {
            ChatChannel.Say => "/s ",
            ChatChannel.Party => "/p ",
            ChatChannel.Alliance => "/a ",
            ChatChannel.FreeCompany => "/fc ",
            ChatChannel.Tell when ValidName(input.SenderName, true) && ValidName(input.SenderWorld, false) => $"/tell {input.SenderName}@{input.SenderWorld} ",
            _ => throw new InvalidOperationException("缺少有效的悄悄话发件人或服务器；已取消回复。"),
        };
        // Limit the entire UTF-8 command, including recipient; never split into a burst.
        var clean = CleanReply(reply, "", 500 - Encoding.UTF8.GetByteCount(prefix));
        if (clean.Length == 0) throw new InvalidOperationException("模型没有返回可发送的正文。" );
        return prefix + clean;
    }

    private static bool ValidName(string value, bool allowSpace) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && value.EnumerateRunes().All(r => Rune.IsLetter(r) || r.Value is '\'' or '-' || (allowSpace && r.Value == ' '));
    [GeneratedRegex(@"<think(?:ing)?\b[^>]*>.*?</think(?:ing)?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex ThinkBlock();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
