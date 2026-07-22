/*
 * yappershq/Kreedz (KZ) — Vanilla (VNL) mode plugin
 *
 * A standalone ModSharp module that registers the Vanilla mode against Kreedz.Core's IKzModeRegistry.
 * VNL is stock CS2 movement physics (convar layer only), but it is NOT hook-free (cs2kz kz_mode_vnl.cpp):
 * it gates timer-zone touches to full ticks (CanTouchTimerZone — subtick-time touches can't shave run
 * time). The path-swept TriggerFix that cs2kz's VNL also carries lives in Core's TriggerModifierModule
 * (it applies to every mode there).
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Mode.Vnl;

public sealed class KreedzModeVnl : IModSharpModule, IKzMovementMode
{
    /// <summary>cs2kz VNL CanTouchTimerZone — timer zones only interact on full ticks.</summary>
    public bool CanTouchTimerZone(PlayerSlot slot)
    {
        var tick = _shared.GetModSharp().GetGlobals().CurTime * 64.0;
        return Math.Abs(Math.Round(tick) - tick) < 0.001;
    }

    private readonly ISharedSystem            _shared;
    private readonly ILogger<KreedzModeVnl>   _logger;

    public string DisplayName   => "[Kreedz] Mode - Vanilla";
    public string DisplayAuthor => "yappershq";

    public KreedzModeVnl(ISharedSystem shared,
                         string?        dllPath,
                         string?        sharpPath,
                         Version?       version,
                         IConfiguration? coreConfiguration,
                         bool           hotReload)
    {
        _shared = shared;
        _logger = shared.GetLoggerFactory().CreateLogger<KreedzModeVnl>();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        var registry = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<IKzModeRegistry>(IKzModeRegistry.Identity)?.Instance;

        if (registry is null)
        {
            _logger.LogError("[Kreedz.Mode.VNL] Kreedz.Core mode registry not found — is the core loaded?");
            return;
        }

        registry.RegisterMode("vnl", "Vanilla", "VNL", Convars);
        registry.RegisterMovementMode("vnl", this); // no custom physics — only the timer-zone tick gate

        // cs2kz VNL Init — switch the player to knife on entering the mode (slot3).
        registry.PlayerModeChanged += (slot, mode) =>
        {
            if (mode == "vnl" && _shared.GetClientManager().GetGameClient(slot) is { IsFakeClient: false } client)
                client.ExecuteStringCommand("slot3");
        };

        _logger.LogInformation("[Kreedz.Mode.VNL] registered.");
    }

    public void Shutdown() { }

    // Faithful CS2 stock movement convar defaults.
    private static readonly IReadOnlyDictionary<string, string> Convars = new Dictionary<string, string>
    {
        // cs2kz kz_mode_vnl.h modeCvarValues — the full 33-cvar mode layer, verbatim.
        ["sv_accelerate"]                   = "5.5",
        ["sv_accelerate_use_weapon_speed"]  = "true",
        ["sv_airaccelerate"]                = "12",
        ["sv_air_max_wishspeed"]            = "30",
        ["sv_autobunnyhopping"]             = "false",
        ["sv_bounce"]                       = "0",
        ["sv_enablebunnyhopping"]           = "false",
        ["sv_friction"]                     = "5.2",
        ["sv_gravity"]                      = "800",
        ["sv_jump_impulse"]                 = "301.993377411",
        ["sv_jump_precision_enable"]        = "true",
        ["sv_jump_spam_penalty_time"]       = "0.015625",
        ["sv_ladder_angle"]                 = "-0.707",
        ["sv_ladder_dampen"]                = "0.2",
        ["sv_ladder_scale_speed"]           = "0.78",
        ["sv_maxspeed"]                     = "250", // 250 not 320 — prevents no-weapon abuses (cs2kz)
        ["sv_maxvelocity"]                  = "3500",
        ["sv_staminajumpcost"]              = "0.08",
        ["sv_staminalandcost"]              = "0.05",
        ["sv_staminamax"]                   = "80",
        ["sv_staminarecoveryrate"]          = "60",
        ["sv_standable_normal"]             = "0.7",
        ["sv_step_move_vel_min"]            = "64",
        ["sv_timebetweenducks"]             = "0.4",
        ["sv_walkable_normal"]              = "0.7",
        ["sv_wateraccelerate"]              = "10",
        ["sv_waterfriction"]                = "1",
        ["sv_water_slow_amount"]            = "0.9",
        ["mp_solid_teammates"]              = "0",
        ["mp_solid_enemies"]                = "0",
        ["sv_subtick_movement_view_angles"] = "true",
        ["sv_legacy_jump"]                  = "false",
        ["sv_bhop_time_window"]             = "0.0078125",
    };
}
