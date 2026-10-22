namespace ArtasTools.MonitorAgent.Core.Analysis;

/// <summary>
/// JSON-схема ответа LLM — единый источник для обеих реализаций (custom и meai).
/// </summary>
public static class LlmJsonSchema
{
    public static string Schema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""verdict"": {
      ""type"": ""string"",
      ""enum"": [""OK"", ""WARNING"", ""CRITICAL""],
      ""description"": ""Общий вердикт по состоянию системы""
    },
    ""summary"": {
      ""type"": ""string"",
      ""description"": ""Краткая сводка ключевых проблем (1-2 предложения)""
    },
    ""findings"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""id"": { ""type"": ""string"" },
          ""severity"": {
            ""type"": ""string"",
            ""enum"": [""critical"", ""warning"", ""info""]
          },
          ""title"": { ""type"": ""string"" },
          ""evidence"": { ""type"": ""string"" },
          ""impact"": { ""type"": ""string"" },
          ""recommendation"": { ""type"": ""string"" },
          ""confidence"": {
            ""type"": ""string"",
            ""enum"": [""high"", ""medium"", ""low""]
          }
        },
        ""required"": [""id"", ""severity"", ""title"", ""evidence"", ""impact"", ""recommendation"", ""confidence""]
      }
    },
    ""noise_candidates"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" }
    },
    ""questions_for_owner"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" }
    }
  },
  ""required"": [""verdict"", ""summary"", ""findings""]
}";
}
