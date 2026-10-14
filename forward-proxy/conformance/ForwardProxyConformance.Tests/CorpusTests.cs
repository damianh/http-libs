// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace DamianH.Http.ForwardProxy.Conformance;

public class CorpusTests
{
    private static readonly IReadOnlyList<Fixture> Fixtures =
        FixtureCorpus.Load(Path.Combine(ConformancePaths.Directory, "fixtures.json")).Fixtures;

    [Fact]
    public void Checked_in_baseline_covers_exactly_the_fixture_corpus()
    {
        var ids = Fixtures.Select(f => f.Id).ToList();
        ids.Distinct().Count().ShouldBe(ids.Count, "fixture ids must be unique");
        var baseline = ResultComparer.LoadBaseline(Path.Combine(ConformancePaths.Directory, "expected-results.json"));
        ids.Order(StringComparer.Ordinal).ShouldBe(baseline.Keys.Order(StringComparer.Ordinal));
        baseline.Values.ShouldAllBe(v => v == Outcomes.Pass || v == Outcomes.Fail || v == Outcomes.Error);
    }

    [Fact]
    public void Fixtures_are_well_formed()
    {
        foreach (var fixture in Fixtures)
        {
            Expectations.All.ShouldContain(fixture.Expect, fixture.Id);
            fixture.Reference.ShouldNotBeNullOrWhiteSpace(fixture.Id);
            fixture.Description.ShouldNotBeNullOrWhiteSpace(fixture.Id);
            fixture.Payload.ShouldNotBeEmpty(fixture.Id);
            foreach (var pattern in fixture.Checks.SelectMany(c => new[] { c.Matches, c.NotMatches }).OfType<string>())
            {
                Should.NotThrow(() => new Regex(pattern), fixture.Id);
            }
        }
    }
}
