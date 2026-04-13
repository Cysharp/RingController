using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingController;

public static class RingConfigStore
{
    const string ConfigFileName = "ring_config.json";

    static readonly object syncRoot = new();
    static DateTime lastWriteUtc;
    static RingConfig? cached;

    public static RingConfig LoadOrCreate(Android.Content.Context context)
    {
        var filePath = GetConfigPath(context);
        lock (syncRoot)
        {
            if (!File.Exists(filePath))
            {
                cached = RingConfig.CreateDefault();
                SaveInternal(context, filePath, cached);
                lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                return cached;
            }

            var writeUtc = File.GetLastWriteTimeUtc(filePath);
            if (cached == null || writeUtc != lastWriteUtc)
            {
                var json = File.ReadAllText(filePath);
                cached = Deserialize(json);
                lastWriteUtc = writeUtc;
            }

            return cached;
        }
    }

    public static void Save(Android.Content.Context context, RingConfig config)
    {
        var filePath = GetConfigPath(context);
        lock (syncRoot)
        {
            SaveInternal(context, filePath, config);
            cached = config;
            lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
        }
    }

    /// <summary> Deep copy via JSON (same options as file persistence). </summary>
    public static RingConfig Clone(RingConfig config)
    {
        var json = Serialize(config);
        return Deserialize(json) ?? RingConfig.CreateDefault();
    }

    /// <summary> Clone and clear nested per-app maps so stored overrides do not recurse. </summary>
    public static RingConfig CloneForPerAppEntry(RingConfig config)
    {
        var c = Clone(config);
        c.PerAppOverrides.Clear();
        return c;
    }

    public static string LoadConfigJson(Android.Content.Context context)
    {
        var cfg = LoadOrCreate(context);
        return Serialize(cfg);
    }

    /// <summary> Replace stored config with deserialized JSON (e.g. after user picks a file). </summary>
    public static bool TryImportFromJson(Android.Content.Context context, string json, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var cfg = Deserialize(json);
            Save(context, cfg);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    static string GetConfigPath(Android.Content.Context context)
    {
        // FilesDir is rarely null on Android; fallback silences nullable analysis.
        var dir = context.FilesDir?.AbsolutePath;
        if (string.IsNullOrEmpty(dir))
            dir = context.ApplicationContext.FilesDir.AbsolutePath;
        return Path.Combine(dir, ConfigFileName);
    }

    static void SaveInternal(Android.Content.Context context, string filePath, RingConfig config)
    {
        var json = Serialize(config);
        File.WriteAllText(filePath, json);
    }

    static RingConfig Deserialize(string json)
    {
        var options = CreateJsonOptions();
        var config = JsonSerializer.Deserialize<RingConfig>(json, options) ?? RingConfig.CreateDefault();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("eachMode", out _))
            {
                RingContextConfig? normal = null;
                if (root.TryGetProperty("normal", out var normalEl))
                {
                    normal = JsonSerializer.Deserialize<RingContextConfig>(normalEl.GetRawText(), options)
                        ?? new RingContextConfig();
                }

                var minAbs = 1;
                var accum = 100L;
                var cool = 0L;
                var seqBuf = 1400L;
                if (root.TryGetProperty("minAbsToTrigger", out var eMin) && eMin.TryGetInt32(out var ma))
                    minAbs = ma;
                if (root.TryGetProperty("accumulationTimeoutMs", out var eAcc) && eAcc.TryGetInt64(out var ac))
                    accum = ac;
                else if (root.TryGetProperty("accumulatedResetAfterIdleMs", out var e0) && e0.TryGetInt64(out var resetAfterIdle))
                    accum = resetAfterIdle;
                else
                {
                    long idle = 0;
                    long inter = 0;
                    var hasIdle = root.TryGetProperty("accumulatedIdleAfterReactionMs", out var e1) && e1.TryGetInt64(out idle);
                    var hasInter = root.TryGetProperty("accumulatedInterEventResetMs", out var e2) && e2.TryGetInt64(out inter);
                    if (hasIdle && hasInter)
                        accum = Math.Max(idle, inter);
                    else if (hasIdle)
                        accum = idle;
                    else if (hasInter)
                        accum = inter;
                }

                if (root.TryGetProperty("actionCooldownMs", out var eCool) && eCool.TryGetInt64(out var c))
                    cool = c;
                if (root.TryGetProperty("sequenceBufferWindowMs", out var eSeq) && eSeq.TryGetInt64(out var sb))
                    seqBuf = sb;

                var migrated = new RingModeProfile
                {
                    MinAbsToTrigger = minAbs,
                    AccumulationTimeoutMs = accum,
                    ActionCooldownMs = cool,
                    SequenceBufferWindowMs = seqBuf,
                    Normal = normal ?? RingModeProfile.CreateDefault().Normal,
                };
                config.EachMode = migrated;
                config.ThresholdMode = JsonSerializer.Deserialize<RingModeProfile>(
                    JsonSerializer.Serialize(migrated, options), options) ?? RingModeProfile.CreateDefault();
                config.AccumulateMode = JsonSerializer.Deserialize<RingModeProfile>(
                    JsonSerializer.Serialize(migrated, options), options) ?? RingModeProfile.CreateDefault();
                config.GestureMode = JsonSerializer.Deserialize<RingModeProfile>(
                    JsonSerializer.Serialize(migrated, options), options) ?? RingModeProfile.CreateDefault();
            }
        }
        catch
        {
            // ignore migration parse errors; keep deserialized config
        }

        EnsureGestureMode(config, options);
        NormalizeGestureSequenceTimeouts(config);
        config.PerAppOverrides ??= new Dictionary<string, RingConfig>();

        return config;
    }

    static void NormalizeGestureSequenceTimeouts(RingConfig config)
    {
        NormalizeGestureSequenceTimeout(config.EachMode);
        NormalizeGestureSequenceTimeout(config.ThresholdMode);
        NormalizeGestureSequenceTimeout(config.AccumulateMode);
        NormalizeGestureSequenceTimeout(config.GestureMode);
    }

    static void NormalizeGestureSequenceTimeout(RingModeProfile profile)
    {
        if (profile.GestureSequenceTimeoutMs > 0) return;
        if (profile.Normal.Sequences.Count > 0)
        {
            var first = profile.Normal.Sequences[0];
            var t = Math.Max(first.MaxGapMs, first.MaxTotalMs);
            profile.GestureSequenceTimeoutMs = t > 0 ? t : first.MaxGapMs;
        }
        if (profile.GestureSequenceTimeoutMs <= 0)
            profile.GestureSequenceTimeoutMs = 1000;
    }

    static void EnsureGestureMode(RingConfig config, JsonSerializerOptions options)
    {
        config.GestureMode ??= RingConfig.CreateDefaultGestureMode();
        if (config.GestureMode.Normal.Sequences.Count > 0) return;
        var pick = config.ThresholdMode.Normal.Sequences.Count > 0
            ? config.ThresholdMode.Normal.Sequences
            : config.EachMode.Normal.Sequences.Count > 0
                ? config.EachMode.Normal.Sequences
                : config.AccumulateMode.Normal.Sequences;
        if (pick.Count == 0)
        {
            config.GestureMode.Normal.Sequences = [RingConfig.CreateDefaultGestureSequenceRule()];
            return;
        }
        config.GestureMode.Normal.Sequences = JsonSerializer.Deserialize<List<RingSequenceRuleConfig>>(
            JsonSerializer.Serialize(pick, options), options) ?? [];
    }

    static string Serialize(RingConfig config)
    {
        var options = CreateJsonOptions();
        return JsonSerializer.Serialize(config, options);
    }

    static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}

