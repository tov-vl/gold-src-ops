using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.AvailabilityExporter;

internal sealed record AvailabilityRecoveryRehearsalResult(
    EvidenceDownloadReceipt Download,
    AvailabilityEvaluationReport Report);

internal sealed class AvailabilityRecoveryRehearsal(EvidenceArchive archive)
{
    public async Task<AvailabilityRecoveryRehearsalResult> RunAsync(
        string sha256,
        string downloadOutputPath,
        string expectedReportPath,
        string reportOutputPath,
        AvailabilityEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var download = await archive.DownloadVerifiedNewAsync(
            sha256,
            downloadOutputPath,
            cancellationToken).ConfigureAwait(false);
        var records = await AvailabilityJsonFile.ReadJsonLinesAsync(
            downloadOutputPath,
            cancellationToken).ConfigureAwait(false);
        var actualReport = AvailabilityEvaluator.Evaluate(records, request);
        var expectedReport = await AvailabilityJsonFile.ReadJsonAsync<AvailabilityEvaluationReport>(
            expectedReportPath,
            cancellationToken).ConfigureAwait(false);

        if (actualReport != expectedReport)
        {
            throw new InvalidDataException(
                "The downloaded evidence produced a different deterministic evaluation report.");
        }

        await AvailabilityJsonFile.WriteJsonNewAsync(
            reportOutputPath,
            actualReport,
            cancellationToken).ConfigureAwait(false);

        return new AvailabilityRecoveryRehearsalResult(download, actualReport);
    }
}
