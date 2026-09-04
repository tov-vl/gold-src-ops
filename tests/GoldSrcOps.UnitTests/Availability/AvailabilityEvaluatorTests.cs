using AwesomeAssertions;
using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class AvailabilityEvaluatorTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_generates_1440_slots_independently_of_exported_row_count()
    {
        var records = Enumerable.Range(0, 1_439)
            .Select(index => CreateResult(WindowStart.AddMinutes(index), AvailabilityOutcome.Good, $"run-{index}"));
        var request = CreateRequest(WindowStart.AddDays(1), WindowStart.AddDays(1).AddMinutes(5));

        var result = AvailabilityEvaluator.Evaluate(records, request);

        result.ExpectedSlotCount.Should().Be(1_440);
        result.EvaluatedSlotCount.Should().Be(1_440);
        result.GoodSlotCount.Should().Be(1_439);
        result.BadSlotCount.Should().Be(1);
        result.MissingSlotCount.Should().Be(1);
        result.Outcomes.Missing.Should().Be(1);
    }

    [Fact]
    public void Evaluate_deduplicates_exports_and_keeps_the_earliest_failed_attempt()
    {
        var slot = WindowStart;
        var failed = CreateResult(
            slot,
            AvailabilityOutcome.Timeout,
            "failed",
            startedAtUtc: slot.AddSeconds(5));
        var retry = CreateResult(
            slot,
            AvailabilityOutcome.Good,
            "retry",
            startedAtUtc: slot.AddSeconds(20));
        var request = CreateRequest(slot.AddMinutes(1), slot.AddMinutes(6));

        var result = AvailabilityEvaluator.Evaluate([failed, failed, retry], request);

        result.GoodSlotCount.Should().Be(0);
        result.BadSlotCount.Should().Be(1);
        result.Outcomes.Timeout.Should().Be(1);
        result.DuplicateRecordCount.Should().Be(1);
        result.IgnoredNonCanonicalAttemptCount.Should().Be(1);
    }

    [Theory]
    [InlineData(216, true)]
    [InlineData(217, false)]
    public void Evaluate_enforces_the_30_day_error_budget_boundary(int badSlots, bool expectedResult)
    {
        const int slotCount = 43_200;
        var records = Enumerable.Range(0, slotCount)
            .Select(index => CreateResult(
                WindowStart.AddMinutes(index),
                index < badSlots ? AvailabilityOutcome.HttpError : AvailabilityOutcome.Good,
                $"run-{index}"));
        var endUtc = WindowStart.AddMinutes(slotCount);
        var request = CreateRequest(endUtc, endUtc.AddMinutes(5));

        var result = AvailabilityEvaluator.Evaluate(records, request);

        result.ExpectedSlotCount.Should().Be(slotCount);
        result.AllowedBadSlotCount.Should().Be(216);
        result.BadSlotCount.Should().Be(badSlots);
        result.MeetsTarget.Should().Be(expectedResult);
    }

    [Fact]
    public void Evaluate_leaves_the_target_pending_during_the_ingestion_grace_period()
    {
        var endUtc = WindowStart.AddMinutes(10);
        var request = CreateRequest(endUtc, endUtc);

        var result = AvailabilityEvaluator.Evaluate([], request);

        result.ExpectedSlotCount.Should().Be(10);
        result.EvaluatedSlotCount.Should().Be(6);
        result.PendingSlotCount.Should().Be(4);
        result.MissingSlotCount.Should().Be(6);
        result.MeetsTarget.Should().BeNull();
    }

    [Fact]
    public void Evaluate_rejects_an_outcome_that_conflicts_with_its_measurement()
    {
        var record = CreateResult(WindowStart, AvailabilityOutcome.Good, "forged") with
        {
            HttpStatus = 503,
        };
        var request = CreateRequest(WindowStart.AddMinutes(1), WindowStart.AddMinutes(6));

        var act = () => AvailabilityEvaluator.Evaluate([record], request);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*inconsistent outcome*");
    }

    private static AvailabilityEvaluationRequest CreateRequest(
        DateTimeOffset endUtc,
        DateTimeOffset evaluatedAtUtc) =>
        new(
            WindowStart,
            endUtc,
            evaluatedAtUtc,
            "v2-4-shadow-001",
            "region-a",
            TimeSpan.FromMinutes(5),
            0.995m);

    private static CanonicalAvailabilityResult CreateResult(
        DateTimeOffset slot,
        AvailabilityOutcome outcome,
        string executionId,
        DateTimeOffset? startedAtUtc = null) =>
        new(
            slot,
            startedAtUtc ?? slot.AddSeconds(10),
            (startedAtUtc ?? slot.AddSeconds(10)).AddMilliseconds(100),
            "v2-4-shadow-001",
            "region-a",
            AvailabilityProbeRole.Primary,
            executionId,
            outcome,
            outcome switch
            {
                AvailabilityOutcome.Good => 200,
                AvailabilityOutcome.HttpError => 503,
                _ => null,
            },
            100);
}
