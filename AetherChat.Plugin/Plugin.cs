using System.Collections.Concurrent;
using AetherChat.Core;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace AetherChat;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly IFramework framework;
    private readonly IClientState state;
    private readonly IObjectTable objects;
    private readonly IPlayerState playerState;
    private readonly IPartyList party;
    private readonly ICondition condition;
    private readonly AiClient client = new();
    private readonly ConcurrentQueue<Action> uiCallbacks = new();
    private readonly SettingsPanel panel;
    private CancellationTokenSource? testCancellation;
    private string session = "";
    private bool disposed;
    internal Configuration Config { get; }
    internal ChatEngine Engine { get; }
    internal string TestStatus { get; private set; } = "尚未测试";
    internal bool Testing { get; private set; }

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IChatGui chatGui,
        IFramework gameFramework, IClientState clientState, IObjectTable objectTable, IPlayerState localPlayerState,
        IPartyList partyList, ICondition gameCondition)
    {
        pi = pluginInterface; commands = commandManager; chat = chatGui; framework = gameFramework;
        state = clientState; objects = objectTable; playerState = localPlayerState; party = partyList; condition = gameCondition;
        Config = pi.GetPluginConfig() as Configuration ?? new();
        Config.Normalize();
        Engine = new(Config, client);
        panel = new(this);
        commands.AddHandler("/aichat", new CommandInfo(OnCommand) { HelpMessage = "打开灵语面板。/aichat on | off | toggle | clear | status | ask <say|party|alliance|fc> <内容>" });
        chat.ChatMessage += OnChat;
        framework.Update += Update;
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenConfigUi += Open;
        pi.UiBuilder.OpenMainUi += Open;
    }

    private void Open() => panel.IsOpen = true;
    private void Draw() => panel.Draw();
    internal void SetEnabled(bool enabled) { Engine.SetEnabled(enabled); if (!enabled) CancelTest(); }
    internal void Save()
    {
        Config.Normalize();
        Engine.Reset(Engine.Enabled ? "设置已保存，等待新消息" : "设置已保存，AI 已关闭");
        CancelTest();
        TestStatus = "配置已修改，请重新测试连接";
        pi.SavePluginConfig(Config);
    }
    internal CompletionOptions Options()
    {
        var p = Config.Profiles[Config.ActiveProfile];
        return new(p.Protocol, p.BaseUrl, p.Model, SecretProtection.Unprotect(p.ProtectedApiKey), Config.SystemPrompt, Config.MaxOutputTokens, Config.TimeoutSeconds);
    }

    private string? NotReadyReason()
    {
        if (!state.IsLoggedIn || objects.LocalPlayer is null) return "角色未登录或尚未加载，回复已取消。";
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]) return "正在切换地图，回复已取消。";
        if (condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] || condition[ConditionFlag.OccupiedInCutSceneEvent]) return "正在播放过场，回复已取消。";
        if (Config.PauseInCombat && condition[ConditionFlag.InCombat]) return "已开启战斗中暂停，回复已取消。";
        return null;
    }
    private bool Ready() => NotReadyReason() is null;

    private unsafe PartyChatState ReadPartyState()
    {
        if (!state.IsLoggedIn) return new(0, false, false, false, "");
        var proxy = InfoProxyCrossRealm.Instance();
        var isCrossRealm = proxy != null && proxy->IsInCrossRealmParty;
        var inAlliance = isCrossRealm && proxy->IsInAllianceRaid;
        var roster = "";
        if (isCrossRealm)
        {
            // Include the local group and all member identities so leaving or changing a
            // cross-world party invalidates any pending reply, just like a normal party.
            var groups = proxy->CrossRealmGroups;
            var parts = new List<string> { proxy->LocalPlayerGroupIndex.ToString() };
            var groupCount = Math.Min((int)proxy->GroupCount, groups.Length);
            for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                var members = groups[groupIndex].GroupMembers;
                var count = Math.Min((int)groups[groupIndex].GroupMemberCount, members.Length);
                var ids = new ulong[count];
                for (var i = 0; i < count; i++) ids[i] = members[i].ContentId;
                Array.Sort(ids);
                parts.Add($"{groupIndex}:{string.Join(',', ids)}");
            }
            roster = string.Join('|', parts);
        }
        return new(party.Length, party.IsAlliance, isCrossRealm, inAlliance, roster);
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        while (uiCallbacks.TryDequeue(out var action)) action();
        var ready = Ready();
        var groupState = ReadPartyState();
        // Cancel on character, world, zone or group change, including temporary inability to chat.
        var current = $"{playerState.ContentId}:{objects.LocalPlayer?.CurrentWorld.RowId}:{state.TerritoryType}:{party.PartyId}:{groupState.SessionKey}:{ready}:" +
                      string.Join(',', party.Select(p => p.ContentId));
        if (current != session)
        {
            var hadSession = session.Length != 0;
            session = current;
            Engine.Reset(Engine.Enabled ? "场景或队伍变化，等待新消息" : "已关闭");
            if (hadSession && !state.IsLoggedIn) SetEnabled(false);
        }
        Engine.Tick(Send);
    }

    private void OnChat(IHandleableChatMessage message)
    {
        if (disposed || !Engine.Enabled || !Ready()) return;
        ChatChannel? channel = message.LogKind switch
        {
            XivChatType.TellIncoming => ChatChannel.Tell,
            XivChatType.Say => ChatChannel.Say,
            XivChatType.Party or XivChatType.CrossParty => ChatChannel.Party,
            XivChatType.Alliance => ChatChannel.Alliance,
            XivChatType.FreeCompany => ChatChannel.FreeCompany,
            _ => null,
        };
        if (channel is null || message.SourceKind == XivChatRelationKind.LocalPlayer) return;
        var payload = message.Sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        // Do not guess tell recipients from decorative text or use /reply (which can target a new sender).
        if (payload is null) return;
        var world = payload.World.ValueNullable?.Name.ToString() ?? "";
        var self = objects.LocalPlayer;
        var isSelf = self is not null && payload.PlayerName == self.Name.TextValue && payload.World.RowId == self.HomeWorld.RowId;
        Engine.Receive(new(channel.Value, payload.PlayerName, world, message.Message.TextValue, isSelf), Options);
    }

    private unsafe bool Send(ChatInput input, string reply)
    {
        if (NotReadyReason() is { } unavailable) throw new InvalidOperationException(unavailable);
        if (ReadPartyState().RejectionFor(input.Channel) is { } rejection) throw new InvalidOperationException(rejection);
        var command = ChatRules.BuildCommand(input, pi.Sanitizer.Sanitize(reply));
        var module = UIModule.Instance();
        if (module == null) throw new InvalidOperationException("游戏聊天模块尚未就绪，回复已取消。");
        var value = Utf8String.FromString(command);
        if (value == null) throw new InvalidOperationException("无法创建游戏聊天文字，回复已取消。");
        try { module->ProcessChatBoxEntry(value); }
        finally { value->Dtor(true); }
        return true;
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var action = parts.Length == 0 ? "" : parts[0].ToLowerInvariant();
        switch (action)
        {
            case "on": SetEnabled(true); chat.Print("灵语：AI 已开启。", "AetherChat"); break;
            case "off": SetEnabled(false); chat.Print("灵语：AI 已关闭，待处理回复已取消。", "AetherChat"); break;
            case "toggle": SetEnabled(!Engine.Enabled); chat.Print(Engine.Enabled ? "AI 已开启" : "AI 已关闭", "AetherChat"); break;
            case "clear": Engine.Reset("上下文已清空，旧请求已取消"); chat.Print(Engine.Status, "AetherChat"); break;
            case "status": chat.Print($"{(Engine.Enabled ? "开启" : "关闭")} / {Engine.Status}", "AetherChat"); break;
            case "ask":
                var ask = parts.Length > 1 ? parts[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) : [];
                if (ask.Length == 2 && TryChannel(ask[0], out var target)) ManualAsk(target, ask[1]);
                else chat.Print("用法：/aichat ask party 今天打什么？（say / party / alliance / fc）", "AetherChat");
                break;
            default: Open(); break;
        }
    }
    private static bool TryChannel(string text, out ChatChannel channel)
    {
        channel = text.ToLowerInvariant() switch { "say" or "s" => ChatChannel.Say, "party" or "p" => ChatChannel.Party, "alliance" or "a" => ChatChannel.Alliance, "fc" => ChatChannel.FreeCompany, _ => ChatChannel.Tell };
        return channel != ChatChannel.Tell;
    }
    internal void ManualAsk(ChatChannel channel, string text)
    {
        if (channel == ChatChannel.Tell) { TestStatus = "悄悄话由收到的消息触发，不使用不明确的手动收件人。"; return; }
        if (!Ready() || !Engine.Enabled || !Config.Channels[channel].Enabled) { TestStatus = "请登录游戏、开启 AI 总开关及目标频道。"; return; }
        var name = objects.LocalPlayer?.Name.TextValue ?? "我";
        if (!Engine.Receive(new(channel, name, "", text), Options, true)) TestStatus = "未提交：请查看运行状态、冷却时间和每分钟上限。";
        else TestStatus = "已提交手动提问，回复将进入所选频道。";
    }

    internal void TestConnection()
    {
        CancelTest();
        CompletionOptions options;
        try { options = Options(); _ = AiClient.Endpoint(options); }
        catch (Exception ex) { TestStatus = ex is InvalidOperationException ? ex.Message : "连接配置无效"; return; }
        var cancellation = testCancellation = new();
        var token = cancellation.Token;
        Testing = true;
        TestStatus = "正在测试；测试文字不会发到游戏频道…";
        _ = Task.Run(async () =>
        {
            string result;
            try
            {
                var response = await client.CompleteAsync(options with { SystemPrompt = "请只回复：连接成功。" }, [new("user", "连接测试")], token);
                result = "连接成功：" + ChatRules.CleanReply(response, "", 300);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { result = ex is InvalidOperationException ? ex.Message : "连接失败，请检查配置。"; }
            if (!token.IsCancellationRequested) uiCallbacks.Enqueue(() =>
            {
                if (disposed || testCancellation != cancellation) return;
                TestStatus = result; Testing = false; cancellation.Dispose(); testCancellation = null;
            });
        });
    }
    private void CancelTest()
    {
        if (Testing) TestStatus = "测试已取消";
        testCancellation?.Cancel(); testCancellation?.Dispose(); testCancellation = null; Testing = false;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        chat.ChatMessage -= OnChat; framework.Update -= Update;
        pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenConfigUi -= Open; pi.UiBuilder.OpenMainUi -= Open;
        commands.RemoveHandler("/aichat");
        CancelTest(); Engine.Dispose(); client.Dispose();
        while (uiCallbacks.TryDequeue(out _)) { }
    }
}
