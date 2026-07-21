# KZ Extensibility — 3rd-party modes & styles

cs2kz ships **modes** and **styles as separate plugins** (e.g. `cs2kz-mode-vanilla`, `cs2kz-mode-classic`
are their own repos, registered against the core and checksum-validated). This fork should mirror that:
the core owns the timer/zones/records/HUD spine, and modes/styles are **drop-in plugins** that register
against a public KZ API. This doc is the split map + the API contract to build.

## Module inventory — what splits out

| Module | Home | Why |
|---|---|---|
| timer, zones, records, DB, replays, HUD, checkpoint | **Core** (this repo) | The gamemode spine; everything else plugs into it. |
| **CKZ movement** (`CkzMovementModule`) | **Externalizable — prime candidate** | Big, self-contained physics port. cs2kz keeps Classic as its own repo. Registers convars + owns its movement hooks. |
| VNL mode | Core built-in (or external) | Trivial (stock movement + convars); fine to keep built-in as the default. |
| **Styles** (ABH, LGJ, future Low-Grav/Ice/WS-only/AD-only) | **Externalizable** | cs2kz designs styles as optional drop-in modifiers. Each style = a few convars or a movement tweak. |
| jumpstats | Core, or external | Consumes movement events; could live outside, but it's cheap to keep in. |
| anticheat, ban, goto, fov, measure, pistol, tip | Core | Server-owned; not player-selectable content. |

## The public API contract (to add to `Timer.Shared`)

Two core-published registries, following Timer's existing `IModSharpModuleInterface` pattern (same as
`ICommandManager`/`IRequestManager`/`IReplayProvider`, which are already looked up cross-plugin):

```csharp
// Timer.Shared — ORM-free, no internal types (single-ALC rule).
public interface IKzModeRegistry
{
    static readonly string Identity = typeof(IKzModeRegistry).FullName!;

    /// Register a mode's metadata + convar layer. The plugin owns its own movement hooks,
    /// gated on GetPlayerMode(slot) == id.
    void RegisterMode(string id, string name, string shortName, IReadOnlyDictionary<string, string> convars);

    string GetPlayerMode(PlayerSlot slot);            // "vnl" default
    event Action<PlayerSlot, string>? PlayerModeChanged;
}

public interface IKzStyleRegistry
{
    static readonly string Identity = typeof(IKzStyleRegistry).FullName!;

    void RegisterStyle(string id, string name, string shortName, IReadOnlyDictionary<string, string> convars);
    bool HasStyle(PlayerSlot slot, string id);
    bool HasAnyStyle(PlayerSlot slot);                // → run is unranked
    event Action<PlayerSlot>? PlayerStylesChanged;
}
```

## Lifecycle (the wiring, once we do the pass)

**Core publishes** (in `ModeModule`/`KzStyleModule`, at the plugin's `PostInit`):
```csharp
_shared.GetSharpModuleManager()
       .RegisterSharpModuleInterface<IKzModeRegistry>(owner, IKzModeRegistry.Identity, this);
```

**External mode/style plugin consumes** (in its `OnAllSharpModulesLoaded`):
```csharp
var modes = shared.GetSharpModuleManager()
                  .GetOptionalSharpModuleInterface<IKzModeRegistry>(IKzModeRegistry.Identity)?.Instance;
modes?.RegisterMode("ckz", "Classic", "CKZ", CkzConvars);
// then install PlayerProcessMovePre / PlayerGetMaxSpeed hooks gated on modes.GetPlayerMode(slot) == "ckz"
```
This is exactly Prophunt's publish-in-PostInit / consume-in-OnAllSharpModulesLoaded pattern, so ordering
is guaranteed (all PostInits finish before any OAM).

## Status: implemented

The split is **live**. Package layout:

- `Kreedz.Shared` — publishes the `IKzModeRegistry` + `IKzStyleRegistry` contracts (ORM-free).
- `Kreedz.Core` — data-driven registries; `ModeModule`/`KzStyleModule` `RegisterSharpModuleInterface` in
  `OnPostInit`. No mode is hard-coded; built-in ABH/LGJ styles self-register.
- `Kreedz.Mode.VNL` — satellite plugin; registers the Vanilla convar layer.
- `Kreedz.Mode.CKZ` — satellite plugin; registers Classic **and owns the faithful cs2kz prestrafe/perf
  movement**, gated on `GetPlayerMode == "ckz"`; consumes the registry in `OnAllModulesLoaded`.

Adding a mode or style is now a new satellite plugin project against `Kreedz.Shared` — no Core changes.

**Remaining validation:** the cross-plugin publish/consume follows the proven lifecycle (publish in
PostInit, consume in OnAllModulesLoaded), but the cross-ALC load + hook interaction can only be certified
on a live server. Styles still ship their ABH/LGJ built-ins in Core; extracting those to their own plugins
is now a trivial repeat of the mode pattern.
