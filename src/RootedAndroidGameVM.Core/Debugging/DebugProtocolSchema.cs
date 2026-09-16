using System.Text.Json;

namespace RootedAndroidGameVM.Core.Debugging;

public static class DebugProtocolSchema
{
    public static readonly JsonElement Request = JsonSerializer.Deserialize<JsonElement>("""
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["command"],
         "properties":{"schemaVersion":{"const":1,"default":1},"command":{"type":"string","minLength":1,"maxLength":128},
          "requestId":{"type":"string","pattern":"^[a-zA-Z0-9_.:-]{1,128}$"},"arguments":{"type":"object"}},"additionalProperties":true}
        """);
    public static readonly JsonElement Response = JsonSerializer.Deserialize<JsonElement>("""
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["schemaVersion","ok"],
         "properties":{"schemaVersion":{"const":1},"ok":{"type":"boolean"},"requestId":{"type":"string"},
          "jobId":{"type":"string"},"stage":{"type":"string"},"session":{"type":"string"},"pid":{"type":"string"},
          "terminal":{"enum":["succeeded","failed","cancelled","timed_out","interrupted"]},
          "artifactDirectory":{"type":"string"},"result":{},"error":{"type":"object","required":["code","message"],
           "properties":{"code":{"type":"string"},"message":{"type":"string"},"stage":{"type":"string"},
            "evidencePath":{"type":"string"},"toolEvidencePath":{"type":"string"}}}},"additionalProperties":true}
        """);
    public static readonly string[] JobStates = ["queued", "running", "cancelling", "succeeded", "failed", "cancelled", "timed_out", "interrupted"];
}
