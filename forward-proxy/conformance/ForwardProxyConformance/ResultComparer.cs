// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;

namespace DamianH.Http.ForwardProxy.Conformance;

/// <summary>
/// Gates results against <c>expected-results.json</c>, which stores only the outcome per fixture id
/// because observations contain per-run values such as ports and trace ids.
/// </summary>
internal static class ResultComparer
{
    public static IReadOnlyDictionary<string, string> LoadBaseline(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"{path} is empty.");

    public static void WriteBaseline(IReadOnlyDictionary<string, FixtureResult> results, string path)
    {
        var outcomes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, result) in results)
        {
            outcomes[id] = result.Outcome;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(outcomes, ConformanceJson.Options).ReplaceLineEndings("\n") + "\n");
    }

    /// <summary>Returns 0 when there are no regressions and no missing fixtures, otherwise 1.</summary>
    public static int Compare(IReadOnlyDictionary<string, FixtureResult> results, IReadOnlyDictionary<string, string> baseline, TextWriter output)
    {
        var passCount = results.Values.Count(r => r.Outcome == Outcomes.Pass);
        output.WriteLine($"Results: {results.Count} fixtures, {passCount} passing.");

        var known = new List<string>();
        var newPasses = new List<string>();
        var changed = new List<string>();
        var missing = new List<string>();
        var regressions = new List<string>();
        foreach (var (id, expected) in baseline.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!results.TryGetValue(id, out var result))
            {
                missing.Add(id);
            }
            else if (result.Outcome == expected)
            {
                if (expected != Outcomes.Pass)
                {
                    known.Add($"{id}: {string.Join("; ", result.Reasons)}");
                }
            }
            else if (expected == Outcomes.Pass)
            {
                regressions.Add($"{id}: pass -> {result.Outcome}: {string.Join("; ", result.Reasons)}");
            }
            else if (result.Outcome == Outcomes.Pass)
            {
                newPasses.Add($"{id} (was {expected})");
            }
            else
            {
                changed.Add($"{id}: {expected} -> {result.Outcome}");
            }
        }
        var unknown = results.Keys.Where(id => !baseline.ContainsKey(id)).Order(StringComparer.Ordinal)
            .Select(id => $"{id}: {results[id].Outcome}").ToList();

        Section($"Known baseline failures ({known.Count}), not gating:", known);
        Section($"🎉 New passes ({newPasses.Count}) — consider updating the baseline:", newPasses);
        Section($"ℹ️ Failure type changed ({changed.Count}), not gating:", changed);
        Section($"ℹ️ Fixtures not in baseline ({unknown.Count}), not gating:", unknown);
        Section($"⚠️ Baseline fixtures missing from results ({missing.Count}), gating:", missing);
        Section($"⛔ Regressions ({regressions.Count}):", regressions);

        if (missing.Count > 0 || regressions.Count > 0)
        {
            return 1;
        }
        output.WriteLine();
        output.WriteLine("✅ No regressions against baseline.");
        return 0;

        void Section(string heading, List<string> lines)
        {
            if (lines.Count == 0)
            {
                return;
            }
            output.WriteLine();
            output.WriteLine(heading);
            foreach (var line in lines)
            {
                output.WriteLine($"  {line}");
            }
        }
    }
}
