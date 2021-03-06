using System;
using Orleans.Providers;

namespace Orleans.Indexing.Tests
{
    [Serializable]
    public class Player3GrainStateNonFaultTolerant : Player3PropertiesNonFaultTolerant, IPlayerState
    {
        public string Email { get; set; }
    }

    /// <summary>
    /// A simple grain that represent a player in a game
    /// </summary>
    [StorageProvider(ProviderName = "MemoryStore")]
    public class Player3GrainNonFaultTolerant : PlayerGrainNonFaultTolerant<Player3GrainStateNonFaultTolerant, Player3PropertiesNonFaultTolerant>, IPlayer3GrainNonFaultTolerant
    {
    }
}
