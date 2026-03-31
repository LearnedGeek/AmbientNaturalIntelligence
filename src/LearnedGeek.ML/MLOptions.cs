namespace LearnedGeek.ML;

/// <summary>
/// Configuration for LearnedGeek.ML classification services.
/// </summary>
public class MLOptions
{
    public const string SectionName = "LMKit";

    public bool Enabled { get; set; } = true;
    public string EmotionModelPath { get; set; } = string.Empty;
    public string SarcasmModelPath { get; set; } = string.Empty;
    public string CustomClassifierPath { get; set; } = string.Empty;
    public int MaxConcurrentClassifications { get; set; } = 4;
    public int ClassificationTimeoutMs { get; set; } = 500;
    public bool VoiceTagsUseMLClassification { get; set; } = true;
    public bool ConfabulationUseMLClassification { get; set; }
    public bool EmergenceUseMLClassification { get; set; }
}
