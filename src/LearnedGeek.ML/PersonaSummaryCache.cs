using Microsoft.Extensions.Logging;

namespace LearnedGeek.ML;

/// <summary>
/// Cached persona summary for confabulation verification.
/// Loaded once from character state, updated only when character state changes.
/// This summary is NEVER injected into the generative model's prompt.
/// It goes only to the confabulation classifier as ground truth to verify against.
/// </summary>
public class PersonaSummaryCache
{
    private readonly ILogger<PersonaSummaryCache> _log;
    private string _summary = string.Empty;

    public string Summary => _summary;
    public bool IsLoaded => !string.IsNullOrEmpty(_summary);

    public PersonaSummaryCache(ILogger<PersonaSummaryCache> log)
    {
        _log = log;
    }

    /// <summary>
    /// Build the persona summary from character state fields.
    /// Called once at startup and whenever character state is updated.
    /// </summary>
    public void LoadFrom(
        string name,
        string occupation,
        string primaryContactName,
        IReadOnlyList<string> selfConcept,
        IReadOnlyList<string> familyContext,
        IReadOnlyList<string> learnedAboutContact)
    {
        var parts = new List<string>();

        parts.Add($"Name: {name}. Works at: {occupation}.");

        if (!string.IsNullOrEmpty(primaryContactName))
            parts.Add($"Contact: {primaryContactName}.");

        // Key self facts (first 3 — keep it compact)
        foreach (var fact in selfConcept.Take(3))
            parts.Add($"Self: {fact}");

        // Key contact facts (first 3)
        foreach (var fact in learnedAboutContact.Take(3))
            parts.Add($"About {primaryContactName}: {fact}");

        // Family (first 2)
        foreach (var fact in familyContext.Take(2))
            parts.Add($"Family: {fact}");

        _summary = string.Join(" ", parts);
        _log.LogInformation("PersonaSummaryCache loaded ({Length} chars): {Preview}",
            _summary.Length, _summary.Length > 100 ? _summary[..100] + "..." : _summary);
    }
}
