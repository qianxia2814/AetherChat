namespace AetherChat.Core;

// Outside an instance, a cross-world party is held separately from the normal party list.
public sealed record PartyChatState(int RegularMembers, bool RegularAlliance, bool CrossRealmParty, bool CrossRealmAlliance, string CrossRealmRoster)
{
    public string? RejectionFor(ChatChannel channel) => channel switch
    {
        ChatChannel.Party when RegularMembers == 0 && !CrossRealmParty => "未检测到普通小队或跨服小队，回复已取消。",
        ChatChannel.Alliance when !RegularAlliance && !(CrossRealmParty && CrossRealmAlliance) => "未检测到联盟队伍，回复已取消。",
        _ => null,
    };

    public string SessionKey => $"{RegularMembers}:{RegularAlliance}:{CrossRealmParty}:{CrossRealmAlliance}:{CrossRealmRoster}";
}
