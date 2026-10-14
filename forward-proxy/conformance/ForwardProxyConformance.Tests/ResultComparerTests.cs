// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy.Conformance;

public class ResultComparerTests
{
    private static readonly FixtureResult Pass = new(Outcomes.Pass, Expectations.Forward, "RFC", []);
    private static readonly FixtureResult Fail = new(Outcomes.Fail, Expectations.Forward, "RFC", ["nothing was forwarded"]);

    private static (int Code, string Output) Compare(Dictionary<string, FixtureResult> results, Dictionary<string, string> baseline)
    {
        using var output = new StringWriter();
        var code = ResultComparer.Compare(results, baseline, output);
        return (code, output.ToString());
    }

    [Fact]
    public void Complete_unchanged_baseline_succeeds_including_known_failures()
    {
        var (code, output) = Compare(new() { ["a"] = Pass, ["b"] = Fail }, new() { ["a"] = "pass", ["b"] = "fail" });
        code.ShouldBe(0);
        output.ShouldContain("Known baseline failures (1)");
        output.ShouldContain("No regressions");
    }

    [Fact]
    public void Missing_passing_fixture_fails()
    {
        var (code, output) = Compare(new() { ["a"] = Pass }, new() { ["a"] = "pass", ["b"] = "pass" });
        code.ShouldBe(1);
        output.ShouldContain("missing from results");
        output.ShouldNotContain("No regressions");
    }

    [Fact]
    public void Missing_known_failure_also_fails() =>
        Compare(new() { ["a"] = Pass }, new() { ["a"] = "pass", ["b"] = "fail" }).Code.ShouldBe(1);

    [Fact]
    public void Empty_results_cannot_pass_a_nonempty_baseline() =>
        Compare([], new() { ["a"] = "pass" }).Code.ShouldBe(1);

    [Fact]
    public void Previously_passing_fixture_that_fails_is_a_regression()
    {
        var (code, output) = Compare(new() { ["a"] = Fail }, new() { ["a"] = "pass" });
        code.ShouldBe(1);
        output.ShouldContain("pass -> fail: nothing was forwarded");
    }

    [Fact]
    public void Driver_error_on_a_passing_fixture_is_a_regression() =>
        Compare(new() { ["a"] = new(Outcomes.Error, Expectations.Forward, "RFC", ["SocketException"]) }, new() { ["a"] = "pass" })
            .Code.ShouldBe(1);

    [Theory]
    [InlineData("fail")]
    [InlineData("error")]
    public void Driver_errors_gate_even_when_baseline_is_not_pass(string expected) =>
        Compare(new() { ["a"] = new(Outcomes.Error, Expectations.Forward, "RFC", ["SocketException"]) }, new() { ["a"] = expected })
            .Code.ShouldBe(1);

    [Fact]
    public void Driver_errors_in_new_fixtures_also_gate() =>
        Compare(new() { ["a"] = new(Outcomes.Error, Expectations.Forward, "RFC", ["SocketException"]) }, []).Code.ShouldBe(1);

    [Fact]
    public void WriteBaseline_refuses_errors_without_modifying_the_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "reviewed baseline");
            Should.Throw<InvalidDataException>(() => ResultComparer.WriteBaseline(
                new Dictionary<string, FixtureResult> { ["a"] = Pass, ["b"] = new(Outcomes.Error, Expectations.Forward, "RFC", ["SocketException"]) }, path));
            File.ReadAllText(path).ShouldBe("reviewed baseline");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void New_passes_are_allowed_without_changing_the_baseline()
    {
        var (code, output) = Compare(new() { ["a"] = Pass }, new() { ["a"] = "fail" });
        code.ShouldBe(0);
        output.ShouldContain("New passes");
    }

    [Fact]
    public void Fixtures_not_in_the_baseline_are_reported_but_do_not_gate()
    {
        var (code, output) = Compare(new() { ["a"] = Pass, ["b"] = Fail }, new() { ["a"] = "pass" });
        code.ShouldBe(0);
        output.ShouldContain("not in baseline (1)");
    }

    [Fact]
    public void WriteBaseline_writes_sorted_outcomes_only()
    {
        var path = Path.GetTempFileName();
        try
        {
            ResultComparer.WriteBaseline(new Dictionary<string, FixtureResult> { ["b"] = Fail, ["a"] = Pass }, path);
            File.ReadAllText(path).ShouldBe("{\n  \"a\": \"pass\",\n  \"b\": \"fail\"\n}\n");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
