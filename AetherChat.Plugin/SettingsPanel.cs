using System.Numerics;
using AetherChat.Core;
using Dalamud.Bindings.ImGui;

namespace AetherChat;

internal sealed class SettingsPanel(Plugin plugin)
{
    public bool IsOpen;
    private string keyDraft = "";
    private string keyStatus = "";
    private string manualText = "";
    private int manualChannel;
    private static readonly string[] ChannelLabels = ["悄悄话", "说话", "小队", "团队（联盟）", "部队"];
    private static readonly string[] ProtocolLabels = ["OpenAI 兼容 / Chat Completions", "OpenAI / Responses", "Claude / Anthropic Messages"];

    public void Draw()
    {
        if (!IsOpen) return;
        ImGui.SetNextWindowSize(new Vector2(720, 620), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("灵语 · AetherChat 0.1.1###AetherChat", ref IsOpen)) { ImGui.End(); return; }
        try
        {
            var enabled = plugin.Engine.Enabled;
            if (ImGui.Checkbox("AI 聊天总开关", ref enabled)) plugin.SetEnabled(enabled);
            ImGui.SameLine();
            if (ImGui.Button("清空上下文 / 取消当前回复")) plugin.Engine.Reset("上下文已清空");
            ImGui.TextWrapped("状态：" + plugin.Engine.Status);
            ImGui.TextDisabled($"已提交 {plugin.Engine.Submitted} 条 · 忙碌/冷却时跳过 {plugin.Engine.Skipped} 条");
            ImGui.Separator();
            var changed = false;
            if (ImGui.BeginTabBar("settings"))
            {
                if (ImGui.BeginTabItem("频道与关键词")) { changed |= Channels(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("模型连接")) { changed |= Connection(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("自定义")) { changed |= Customize(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("测试与状态")) { Diagnostics(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
            if (changed) plugin.Save();
        }
        finally { ImGui.End(); }
    }

    private bool Channels()
    {
        var c = plugin.Config;
        var changed = false;
        ImGui.TextWrapped("接收已开启频道的他人消息，由你的当前角色回复原频道；悄悄话回复原发件人。");
        ImGui.TextWrapped("关键词为空 = 自动回复所有他人消息。多个关键词用逗号、分号或换行分隔，命中任意一个即触发（忽略英文大小写）。");
        var words = c.Keywords;
        if (ImGui.InputTextMultiline("全局关键词", ref words, 2048, new Vector2(-180, 70))) { c.Keywords = words; changed = true; }
        foreach (var channel in Enum.GetValues<ChatChannel>())
        {
            ImGui.PushID((int)channel);
            var rule = c.Channels[channel];
            var on = rule.Enabled;
            if (ImGui.Checkbox(ChannelLabels[(int)channel], ref on)) { rule.Enabled = on; changed = true; }
            ImGui.SameLine(200);
            var own = rule.OverrideKeywords;
            if (ImGui.Checkbox("使用独立关键词", ref own)) { rule.OverrideKeywords = own; changed = true; }
            if (own)
            {
                var custom = rule.Keywords;
                if (ImGui.InputText("关键词（空白=全部）", ref custom, 2048)) { rule.Keywords = custom; changed = true; }
            }
            ImGui.PopID();
        }
        ImGui.Separator();
        ImGui.TextWrapped("首次使用请先选择模型、填写地址和模型 ID，再打开所需频道及总开关。重新加载插件或退出角色后总开关保持关闭。");
        return changed;
    }

    private bool Connection()
    {
        var c = plugin.Config;
        var changed = false;
        var selected = c.ActiveProfile;
        if (ImGui.Combo("连接配置", ref selected, c.Profiles.Select(p => p.Name).ToArray(), c.Profiles.Count))
        { c.ActiveProfile = selected; keyDraft = ""; keyStatus = ""; changed = true; }
        var p = c.Profiles[c.ActiveProfile];
        var protocol = (int)p.Protocol;
        if (ImGui.Combo("接口协议", ref protocol, ProtocolLabels, ProtocolLabels.Length)) { p.Protocol = (ApiProtocol)protocol; changed = true; }
        var url = p.BaseUrl;
        if (ImGui.InputText("基础地址", ref url, 1024)) { p.BaseUrl = url; changed = true; }
        ImGui.TextDisabled("例如 http://127.0.0.1:1234/v1；不要加 /chat/completions");
        var model = p.Model;
        if (ImGui.InputText("模型 ID", ref model, 256)) { p.Model = model; changed = true; }
        ImGui.TextWrapped("模型 ID 填写该服务实际提供的名称；本机与云端使用各自的配置。GPT、Claude 通过对应 API 连接，本机运行本地服务提供的模型。");
        ImGui.InputText("新的 API Key", ref keyDraft, 4096, ImGuiInputTextFlags.Password);
        ImGui.TextDisabled(p.ProtectedApiKey.Length == 0 ? "未保存密钥（本机可留空）" : "已加密保存密钥");
        if (ImGui.Button("保存密钥"))
        {
            try { p.ProtectedApiKey = SecretProtection.Protect(keyDraft.Trim()); keyDraft = ""; keyStatus = "密钥已加密保存"; changed = true; }
            catch { keyStatus = "加密失败，密钥未保存"; }
        }
        ImGui.SameLine();
        if (ImGui.Button("清除密钥")) { p.ProtectedApiKey = ""; keyDraft = ""; keyStatus = "密钥已清除"; changed = true; }
        if (keyStatus.Length > 0) ImGui.TextWrapped(keyStatus);
        if (ImGui.Button(plugin.Testing ? "重新测试连接" : "测试连接（只在面板显示）")) plugin.TestConnection();
        ImGui.TextWrapped(plugin.TestStatus);
        ImGui.Separator();
        ImGui.TextWrapped("云端模式会将触发的聊天消息与该会话上下文发送给所选 API。上下文只留在内存中；密钥使用当前 Windows 账户加密保存。");
        return changed;
    }

    private bool Customize()
    {
        var c = plugin.Config;
        var changed = false;
        var prompt = c.SystemPrompt;
        if (ImGui.InputTextMultiline("人设 / 系统提示词", ref prompt, 16000, new Vector2(-180, 160))) { c.SystemPrompt = prompt; changed = true; }
        var prefix = c.ReplyPrefix;
        if (ImGui.InputText("回复前缀（可空）", ref prefix, 256)) { c.ReplyPrefix = prefix; changed = true; }
        var ignored = c.IgnoredPlayers;
        if (ImGui.InputTextMultiline("忽略玩家", ref ignored, 4096, new Vector2(-180, 65))) { c.IgnoredPlayers = ignored; changed = true; }
        ImGui.TextDisabled("精确角色名或 角色名@服务器，逗号分隔。可用于忽略其他机器人。");
        var cooldown = c.CooldownSeconds;
        if (ImGui.SliderInt("回复间隔（秒）", ref cooldown, 3, 120)) { c.CooldownSeconds = cooldown; changed = true; }
        var limit = c.RepliesPerMinute;
        if (ImGui.SliderInt("每分钟请求上限", ref limit, 1, 20)) { c.RepliesPerMinute = limit; changed = true; }
        var timeout = c.TimeoutSeconds;
        if (ImGui.SliderInt("请求超时（秒）", ref timeout, 5, 120)) { c.TimeoutSeconds = timeout; changed = true; }
        var context = c.ContextPairs;
        if (ImGui.SliderInt("每会话上下文轮数", ref context, 0, 20)) { c.ContextPairs = context; changed = true; }
        var bytes = c.MaxReplyBytes;
        if (ImGui.SliderInt("回复长度（UTF-8 字节）", ref bytes, 30, 400)) { c.MaxReplyBytes = bytes; changed = true; }
        ImGui.TextDisabled("普通汉字通常 3 字节；超长回复截短为一条，不连续刷屏。");
        var tokens = c.MaxOutputTokens;
        if (ImGui.SliderInt("模型输出 token 上限", ref tokens, 64, 8192)) { c.MaxOutputTokens = tokens; changed = true; }
        var combat = c.PauseInCombat;
        if (ImGui.Checkbox("战斗中暂停", ref combat)) { c.PauseInCombat = combat; changed = true; }
        var preview = c.PreviewOnly;
        if (ImGui.Checkbox("仅预览回复，不发到游戏", ref preview)) { c.PreviewOnly = preview; changed = true; }
        return changed;
    }

    private void Diagnostics()
    {
        ImGui.TextWrapped("最近触发者：" + plugin.Engine.LastSender);
        ImGui.TextWrapped("最近回复：" + plugin.Engine.LastReply);
        ImGui.Separator();
        string[] channels = ["说话", "小队", "团队（联盟）", "部队"];
        ImGui.Combo("手动提问目标", ref manualChannel, channels, channels.Length);
        ImGui.InputTextMultiline("提问内容", ref manualText, 2048, new Vector2(-160, 80));
        if (ImGui.Button("让角色在所选频道回复") && !string.IsNullOrWhiteSpace(manualText)) plugin.ManualAsk((ChatChannel)(manualChannel + 1), manualText);
        ImGui.TextWrapped(plugin.TestStatus);
        ImGui.Separator();
        ImGui.TextWrapped("/aichat 打开面板\n/aichat on 或 off 开关\n/aichat toggle 切换\n/aichat clear 清空上下文并取消请求\n/aichat status 查看状态\n/aichat ask party 问题 手动提问");
        ImGui.TextWrapped("同时只处理一条请求；忙碌、冷却或超限的消息直接跳过，不排队。上下文按频道隔离，悄悄话按发件人隔离。切图、换队、改设置或关闭后，旧回复会被取消。");
    }
}
