using AetherChat.Core;
using Dalamud.Configuration;

namespace AetherChat;
[Serializable]
public sealed class Configuration : ChatSettings, IPluginConfiguration
{
    public int Version { get; set; } = 1;
}
