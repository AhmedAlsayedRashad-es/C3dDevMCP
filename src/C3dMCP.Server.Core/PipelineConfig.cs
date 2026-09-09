using System.Text.Json;
using System.Text.Json.Serialization;

namespace C3dMCP.Server.Core;

/// <summary>pipeline.json: the agent bank, the ordered steps per rung, and the ladder rule.</summary>
public sealed class PipelineConfig
{
    public string Schema { get; set; } = "c3dmcp.pipeline/1";
    public Dictionary<string, AgentSpec> Bank { get; set; } = new();
    public List<Rung> Rungs { get; set; } = new();
    public LadderRule Ladder { get; set; } = new();

    public sealed class AgentSpec
    {
        public string Agent { get; set; } = "";
        public string? Model { get; set; }
        public string? Effort { get; set; }
    }

    public sealed class Step
    {
        public string Use { get; set; } = "";
        public bool Optional { get; set; }
        public bool Active { get; set; } = true;
        public string? Model { get; set; }
        public string? Effort { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class Rung
    {
        [JsonPropertyName("rung")] public int Number { get; set; }
        public List<Step> Steps { get; set; } = new();
        public bool AskHuman { get; set; }
    }

    public sealed class LadderRule
    {
        public int StartRung { get; set; } = 1;
        public int ClimbAfterFails { get; set; } = 1;
        public int EscalateAfterFails { get; set; } = 2;
        public bool ResetOnPass { get; set; } = true;
        public int RedispatchCeiling { get; set; } = 2;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PipelineConfig Load(ProjectContext ctx)
    {
        if (!File.Exists(ctx.PipelineFile)) return Default();
        return JsonSerializer.Deserialize<PipelineConfig>(File.ReadAllText(ctx.PipelineFile), Json) ?? Default();
    }

    public static void Save(ProjectContext ctx, PipelineConfig p)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ctx.PipelineFile)!);
        File.WriteAllText(ctx.PipelineFile, JsonSerializer.Serialize(p, Json));
    }

    public Rung? GetRung(int n) => Rungs.FirstOrDefault(r => r.Number == n);
    public int MaxRung => Rungs.Count == 0 ? 1 : Rungs.Max(r => r.Number);

    /// <summary>Resolved (agent, model, effort) for a step: step overrides bank.</summary>
    public (string agent, string? model, string? effort) Resolve(Step s)
    {
        Bank.TryGetValue(s.Use, out var spec);
        return (spec?.Agent ?? s.Use, s.Model ?? spec?.Model, s.Effort ?? spec?.Effort);
    }

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Rungs.Count == 0) errors.Add("no rungs");
        foreach (var r in Rungs)
        {
            if (r.Steps.Count == 0) errors.Add("rung " + r.Number + " has no steps");
            foreach (var s in r.Steps) if (!Bank.ContainsKey(s.Use)) errors.Add("rung " + r.Number + " step '" + s.Use + "' is not in the bank");
            if (!r.Steps.Any(s => s.Active && !s.Optional && Resolve(s).agent == "c3d-analyzer")) errors.Add("rung " + r.Number + " has no required analyzer step");
        }
        if (GetRung(Ladder.StartRung) == null) errors.Add("startRung " + Ladder.StartRung + " does not exist");
        if (Ladder.EscalateAfterFails < 1) errors.Add("escalateAfterFails must be >= 1");
        return errors;
    }

    public static PipelineConfig Default() => JsonSerializer.Deserialize<PipelineConfig>(DefaultJson, Json)!;

    public const string DefaultJson = """
        {
          "schema": "c3dmcp.pipeline/1",
          "bank": {
            "diagnostician": { "agent": "c3d-diagnostician",  "model": "opus",   "effort": "high" },
            "planner":       { "agent": "c3d-planner",        "model": "opus",   "effort": "high" },
            "planner-x":     { "agent": "c3d-planner-xhigh",  "model": "fable",  "effort": "xhigh" },
            "planner-max":   { "agent": "c3d-planner-max",    "model": "fable",  "effort": "max" },
            "implementer":   { "agent": "c3d-implementer",    "model": "sonnet", "effort": "medium" },
            "analyzer":      { "agent": "c3d-analyzer",       "model": "opus",   "effort": "high" },
            "plan-critic":   { "agent": "c3d-plan-critic",    "model": "fable",  "effort": "xhigh" },
            "change-critic": { "agent": "c3d-change-critic",  "model": "fable",  "effort": "high" }
          },
          "rungs": [
            { "rung": 1, "steps": [
              { "use": "diagnostician", "optional": true,  "active": false },
              { "use": "planner",       "optional": false, "active": true },
              { "use": "implementer",   "optional": false, "active": true },
              { "use": "analyzer",      "optional": false, "active": true } ] },
            { "rung": 2, "steps": [
              { "use": "diagnostician", "optional": false, "active": true },
              { "use": "planner-x",     "optional": false, "active": true },
              { "use": "plan-critic",   "optional": true,  "active": false },
              { "use": "implementer",   "optional": false, "active": true },
              { "use": "analyzer",      "optional": false, "active": true } ] },
            { "rung": 3, "askHuman": true, "steps": [
              { "use": "diagnostician", "optional": false, "active": true },
              { "use": "planner-x",     "optional": false, "active": true },
              { "use": "plan-critic",   "optional": false, "active": true },
              { "use": "implementer",   "optional": false, "active": true },
              { "use": "analyzer",      "optional": false, "active": true },
              { "use": "change-critic", "optional": true,  "active": true } ] },
            { "rung": 4, "askHuman": true, "steps": [
              { "use": "diagnostician", "optional": false, "active": true },
              { "use": "planner-max",   "optional": false, "active": true },
              { "use": "plan-critic",   "optional": false, "active": true },
              { "use": "implementer",   "optional": false, "active": true },
              { "use": "analyzer",      "optional": false, "active": true },
              { "use": "change-critic", "optional": false, "active": true } ] }
          ],
          "ladder": { "startRung": 1, "climbAfterFails": 1, "escalateAfterFails": 2, "resetOnPass": true, "redispatchCeiling": 2 }
        }
        """;
}
