﻿using Robust.Shared.Prototypes;

namespace Content.Server.WhiteDream.BloodCult.Constructs.SoulShard;

[RegisterComponent]
public sealed partial class SoulShardComponent : Component
{
    [DataField]
    public bool IsBlessed;

    [DataField]
    public Color LightColor = Color.DarkRed;

    [DataField]
    public Color BlessedLightColor = Color.LightCyan;

    [DataField]
    public List<EntProtoId> Constructs = new()
    {
        "ConstructJuggernaut",
        "ConstructArtificer",
        "ConstructWraith"
    };

    [DataField]
    public List<EntProtoId> BlessedConstructs = new()
    {
        "ConstructJuggernautHoly",
        "ConstructArtificerHoly",
        "ConstructWraithHoly"
    };
}
