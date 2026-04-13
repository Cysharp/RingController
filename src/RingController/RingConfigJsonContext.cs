using System.Text.Json.Serialization;

namespace RingController;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RingConfig))]
[JsonSerializable(typeof(RingModeProfile))]
[JsonSerializable(typeof(RingContextConfig))]
[JsonSerializable(typeof(RingDirectionMappingConfig))]
[JsonSerializable(typeof(RingMagnitudeActionRule))]
[JsonSerializable(typeof(RingActionConfig))]
[JsonSerializable(typeof(RingSequenceRuleConfig))]
[JsonSerializable(typeof(RingSequenceStepConfig))]
[JsonSerializable(typeof(List<RingSequenceRuleConfig>))]
[JsonSerializable(typeof(List<RingMagnitudeActionRule>))]
[JsonSerializable(typeof(List<RingSequenceStepConfig>))]
[JsonSerializable(typeof(Dictionary<string, RingConfig>))]
internal partial class RingConfigJsonContext : JsonSerializerContext
{
}
