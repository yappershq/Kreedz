/*
 * yappershq/Kreedz (KZ) — KZ Mapping API (1:1 cs2kz src/kz/mappingapi + src/kz/trigger)
 *
 * Modern kz_ maps describe their timer via the cs2kz Mapping API: `trigger_multiple` entities carry a
 * `timer_trigger_type` keyvalue (+ per-type keyvalues), and `info_target_server_only` entities declare
 * course descriptors. Kreedz's legacy path matched targetnames only, so real cs2kz maps registered no
 * zones — this is the typed model + parser that fixes it.
 *
 * This file is the FAITHFUL DATA MODEL + PARSER (headless, engine-independent): given an entity's
 * classname + its keyvalues (as a string dict), it produces a typed KzTrigger / KzCourseDescriptor and
 * registers it. The one engine-coupled piece — the SOURCE of the spawn keyvalues — is behind
 * IMappingKeyValueSource: ModSharp has no managed spawn-keyvalue read, so that source is either a native
 * CEntityKeyValues spawn detour (1:1 with cs2kz) or an offline entity-lump extract. Everything below is
 * shared by both and validated by the self-check in MappingApiTests.
 *
 * Constants + enum + keyvalue names transcribed verbatim from cs2kz kz_mappingapi.{h,cpp} (KZ_MAPAPI_VERSION 2).
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kreedz.Modules.MappingApi;

/// <summary>cs2kz KzTriggerType — verbatim order (values are the on-disk map contract, do not reorder).</summary>
internal enum KzTriggerType
{
    Disabled = 0,
    Modifier,
    ResetCheckpoints,
    SingleBhopReset,
    AntiBhop,
    ZoneStart,
    ZoneEnd,
    ZoneSplit,
    ZoneCheckpoint,
    ZoneStage,
    Teleport,
    MultiBhop,
    SingleBhop,
    SequentialBhop,
    Push,
    Count,
}

internal static class KzTrigger
{
    public static bool IsTimerZone(KzTriggerType t) => t is >= KzTriggerType.ZoneStart and <= KzTriggerType.ZoneStage;
    public static bool IsBhop(KzTriggerType t)      => t is KzTriggerType.MultiBhop or KzTriggerType.SingleBhop or KzTriggerType.SequentialBhop;
    public static bool IsTeleport(KzTriggerType t)  => t is KzTriggerType.Teleport;
    public static bool IsPush(KzTriggerType t)      => t is KzTriggerType.Push;
}

/// <summary>A course, declared by an info_target_server_only descriptor entity (cs2kz KZCourseDescriptor).</summary>
internal sealed class KzCourseDescriptor
{
    public const string DefaultDescriptor = "Default"; // KZ_NO_MAPAPI_COURSE_DESCRIPTOR
    public const string DefaultName       = "Main";    // KZ_NO_MAPAPI_COURSE_NAME

    public required int    Number             { get; init; } // timer_course_number (>0)
    public required string Name               { get; init; } // timer_course_name
    public required string TargetName         { get; init; } // targetname — links zones to this course
    public          bool   DisableCheckpoints { get; init; } // timer_course_disable_checkpoint
    public          int    HammerId           { get; init; } = -1;
}

/// <summary>A parsed KZ zone trigger (cs2kz KzMapZone) — links to a course + carries its number.</summary>
internal sealed class KzZoneTrigger
{
    public required KzTriggerType Type             { get; init; } // ZoneStart/End/Split/Checkpoint/Stage
    public required string        CourseDescriptor { get; init; } // timer_zone_course_descriptor
    public          int           Number           { get; init; } // split/checkpoint/stage number
    public          int           HammerId         { get; init; } = -1;
    public          int           EntityHandle     { get; init; } = -1;
}

/// <summary>Where the mapping API gets an entity's spawn keyvalues. ModSharp exposes no managed
/// spawn-keyvalue read, so the concrete source is engine-coupled (native detour or offline extract).</summary>
internal interface IMappingKeyValueSource
{
    /// <summary>The mapping-API version the map declares (0 = legacy/no mapping API → fall back to targetnames).</summary>
    int MapApiVersion { get; }
}

/// <summary>The mapping-API registry + parser. Feed it (classname, keyvalues, handle) per spawned entity;
/// it builds the course + zone-trigger tables the timer/zone systems consume.</summary>
internal sealed class MappingApiRegistry
{
    // cs2kz keyvalue names (kz_mappingapi.cpp) — verbatim.
    private const string KeyTriggerType       = "timer_trigger_type";
    private const string KeyHammerId          = "hammerUniqueId";
    private const string KeyZoneCourse        = "timer_zone_course_descriptor";
    private const string KeyZoneSplitNumber   = "timer_zone_split_number";
    private const string KeyZoneCheckpointNum = "timer_zone_checkpoint_number";
    private const string KeyZoneStageNumber   = "timer_zone_stage_number";
    private const string KeyCourseNumber      = "timer_course_number";
    private const string KeyCourseName        = "timer_course_name";
    private const string KeyCourseDisableCp   = "timer_course_disable_checkpoint";
    private const string KeyTargetName        = "targetname";

    public const int InvalidNumber = 0; // INVALID_{SPLIT,CHECKPOINT,STAGE,COURSE}_NUMBER

    private readonly Dictionary<string, KzCourseDescriptor> _courses  = new(StringComparer.Ordinal);
    private readonly List<KzZoneTrigger>                    _zones    = [];
    private readonly List<string>                           _errors   = [];

    public IReadOnlyDictionary<string, KzCourseDescriptor> Courses => _courses;
    public IReadOnlyList<KzZoneTrigger>                    Zones   => _zones;
    public IReadOnlyList<string>                           Errors  => _errors;

    public void Clear()
    {
        _courses.Clear();
        _zones.Clear();
        _errors.Clear();
    }

    /// <summary>Parse one course descriptor entity (info_target_server_only). cs2kz Mapi_OnInfoTargetSpawn.</summary>
    public bool TryAddCourse(IReadOnlyDictionary<string, string> kv, int hammerId = -1)
    {
        var number     = GetInt(kv, KeyCourseNumber, InvalidNumber);
        var name       = GetStr(kv, KeyCourseName);
        var targetName = GetStr(kv, KeyTargetName);

        if (number <= InvalidNumber) { _errors.Add($"course number must be > {InvalidNumber} (hammerId {hammerId})"); return false; }
        if (string.IsNullOrEmpty(name))       { _errors.Add($"course name empty (number {number}, hammerId {hammerId})"); return false; }
        if (string.IsNullOrEmpty(targetName)) { _errors.Add($"course targetname empty (name '{name}', hammerId {hammerId})"); return false; }

        if (!_courses.TryAdd(targetName, new KzCourseDescriptor
            {
                Number             = number,
                Name               = name,
                TargetName         = targetName,
                DisableCheckpoints = GetBool(kv, KeyCourseDisableCp),
                HammerId           = hammerId,
            }))
        {
            _errors.Add($"course descriptor '{targetName}' already registered (hammerId {hammerId})");
            return false;
        }

        return true;
    }

    /// <summary>Parse one trigger_multiple entity. Returns the typed trigger type (Disabled if not a KZ trigger).
    /// Timer-zone triggers are recorded; the caller wires them into the zone system. cs2kz Mapi_OnTriggerMultipleSpawn.</summary>
    public KzTriggerType TryAddTrigger(IReadOnlyDictionary<string, string> kv, int entityHandle = -1)
    {
        var hammerId = GetInt(kv, KeyHammerId, -1);
        var type     = (KzTriggerType) GetInt(kv, KeyTriggerType, (int) KzTriggerType.Disabled);

        if (type <= KzTriggerType.Disabled || type >= KzTriggerType.Count)
        {
            if (type != KzTriggerType.Disabled)
                _errors.Add($"trigger type {(int) type} out of range (hammerId {hammerId})");
            return KzTriggerType.Disabled;
        }

        if (KzTrigger.IsTimerZone(type))
        {
            var course = GetStr(kv, KeyZoneCourse);
            if (string.IsNullOrEmpty(course))
            {
                _errors.Add($"{type} zone course descriptor empty (hammerId {hammerId})");
                return KzTriggerType.Disabled;
            }

            var number = type switch
            {
                KzTriggerType.ZoneSplit      => GetInt(kv, KeyZoneSplitNumber,   InvalidNumber),
                KzTriggerType.ZoneCheckpoint => GetInt(kv, KeyZoneCheckpointNum, InvalidNumber),
                KzTriggerType.ZoneStage      => GetInt(kv, KeyZoneStageNumber,   InvalidNumber),
                _                            => InvalidNumber, // start/end carry no number
            };

            if (type is KzTriggerType.ZoneSplit or KzTriggerType.ZoneCheckpoint or KzTriggerType.ZoneStage && number <= InvalidNumber)
            {
                _errors.Add($"{type} number '{number}' invalid (hammerId {hammerId})");
                return KzTriggerType.Disabled;
            }

            _zones.Add(new KzZoneTrigger
            {
                Type             = type,
                CourseDescriptor = course,
                Number           = number,
                HammerId         = hammerId,
                EntityHandle     = entityHandle,
            });
        }

        // Non-zone triggers (modifier/antibhop/teleport/push/bhop) are parsed by their own handlers — the
        // type is returned so the trigger service can attach the right per-type behavior. (Follow-up.)
        return type;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> kv, string key, int fallback)
        => kv.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> kv, string key)
        => kv.TryGetValue(key, out var v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string GetStr(IReadOnlyDictionary<string, string> kv, string key)
        => kv.TryGetValue(key, out var v) ? v : string.Empty;
}
