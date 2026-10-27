using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Research;

public sealed record RawFinalPublication(string Version, string StageSha256, int StageBytes, string ReportSha256,
    int ReportBytes, DateTimeOffset VerifiedAt, string WorkerCommit);

public sealed partial class ResearchStore
{
    private const string RawFinalPublicationVersion = "raw-final-publication-v1";
    private const int RawPublicationHeaderBytes = 4096;
    private const int RawPublicationStageBytes = RawStudyDevelopmentRun.MaximumReportBytes + RawPublicationHeaderBytes + 4;
    // Both immutable copies remain charged. The allowance covers ordinary store bookkeeping, not foreign growth or physical disk exhaustion.
    private const long RawFinalHeadroomBytes = RawPublicationStageBytes + RawStudyDevelopmentRun.MaximumReportBytes + 1024L * 1024;
    private readonly Queue<string> rawPublicationRecovery = new();
    private sealed record RawPublicationHeader(string Version, string StudyId, string RegistryTrialId, string TrialId,
        string ClaimRequestId, string PlanSha256, string DatasetSha256, string RequestSha256, string ResearchSha256,
        string ReportSha256, int ReportBytes, DateTimeOffset VerifiedAt, string WorkerCommit);
    private string RawPublicationPath(string id) => Path.Combine(Root, "runs", id, "raw-study-publication.stage");

    private static bool RawPublicationStateValid(RawResearchStudy study, RawStudyTrialEntry row)
    {
        var final = IsRawFinalTrial(study, row);
        var current = final && study.FinalPublicationVersion == RawFinalPublicationVersion;
        if (row.Status == "publishing" && (!current || row.Publication is null)) return false;
        if (current && row.Status == "succeeded" && row.Publication is null) return false;
        if (row.Publication is not { } p) return true;
        return current && p.Version == RawFinalPublicationVersion && row.Status is ("publishing" or "succeeded" or "cancelled") &&
            IsSha256(p.StageSha256) && IsSha256(p.ReportSha256) && p.StageBytes is > 4 and <= RawPublicationStageBytes &&
            p.ReportBytes is > 0 and <= RawStudyDevelopmentRun.MaximumReportBytes && p.StageBytes - p.ReportBytes is > 4 and <= RawPublicationHeaderBytes + 4 &&
            p.WorkerCommit is not null && p.WorkerCommit == row.WorkerCommit && p.VerifiedAt >= row.StartedAt &&
            (row.FinishedAt is null || row.FinishedAt >= p.VerifiedAt) &&
            (row.ReportSha256 is null || row.ReportSha256 == p.ReportSha256);
    }
    private static void ValidateRawPublicationHeadroom(SqliteConnection db, IReadOnlyDictionary<string, RawResearchStudy> studies,
        IReadOnlyList<RawStudyTrialEntry> rows)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT trial_id,study_id,request_id,bytes FROM raw_final_headroom";
        using var reader = cmd.ExecuteReader(); var found = new HashSet<string>(StringComparer.Ordinal);
        var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        while (reader.Read())
        {
            if (!byId.TryGetValue(reader.GetString(0), out var row) || !studies.TryGetValue(row.StudyId, out var study) || study.FinalPublicationVersion != RawFinalPublicationVersion ||
                !IsRawFinalTrial(study, row) || reader.GetString(1) != study.Id || reader.GetString(2) != study.FinalRequestId ||
                reader.GetInt64(3) != (row.Status is "planned" or "started" or "publishing" ? RawFinalHeadroomBytes : 0) || !found.Add(row.Id))
                throw new ResearchException("raw-study-publication-reservation-integrity");
        }
        foreach (var row in rows.Where(r => studies[r.StudyId].FinalPublicationVersion == RawFinalPublicationVersion && IsRawFinalTrial(studies[r.StudyId], r)))
            if (!found.Contains(row.Id)) throw new ResearchException("raw-study-publication-reservation-integrity");
    }
    private static void UpdateRawPublicationHeadroom(SqliteConnection db, SqliteTransaction tx, RawResearchStudy study, RawStudyTrialEntry row)
    {
        if (study.FinalPublicationVersion != RawFinalPublicationVersion || !IsRawFinalTrial(study, row)) return;
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE raw_final_headroom SET bytes=$bytes WHERE trial_id=$id AND study_id=$study AND request_id=$request";
        cmd.Parameters.AddWithValue("$bytes", row.Status is "planned" or "started" or "publishing" ? RawFinalHeadroomBytes : 0);
        cmd.Parameters.AddWithValue("$id", row.Id); cmd.Parameters.AddWithValue("$study", study.Id); cmd.Parameters.AddWithValue("$request", study.FinalRequestId!);
        if (cmd.ExecuteNonQuery() != 1) throw new ResearchException("raw-study-publication-reservation-integrity");
    }
    private static RawPublicationHeader PublicationHeader(RawResearchStudy study, RawStudyTrialEntry row, string reportSha, int reportBytes, DateTimeOffset verifiedAt) =>
        new(RawFinalPublicationVersion, study.Id, row.Id, row.TrialId, study.FinalRequestId!, study.PlanSha256, row.DatasetSha256,
            row.RequestSha256, row.ResearchSha256, reportSha, reportBytes, verifiedAt, row.WorkerCommit!);
    private (RawStudyTrialEntry Row, byte[] Stage) PrepareRawPublication(RawResearchStudy study, RawStudyTrialEntry row, byte[] report)
    {
        var at = clock.GetUtcNow();
        if (at < row.StartedAt) throw new ResearchException("raw-study-clock-regression");
        var sha = RawHash(report); var header = RawBytes(PublicationHeader(study, row, sha, report.Length, at));
        if (header.Length > RawPublicationHeaderBytes || report.Length > RawStudyDevelopmentRun.MaximumReportBytes)
            throw new ResearchException("raw-study-publication-stage-integrity");
        var stage = new byte[4 + header.Length + report.Length]; BinaryPrimitives.WriteInt32LittleEndian(stage, header.Length);
        header.CopyTo(stage, 4); report.CopyTo(stage, 4 + header.Length);
        row = row with { Status = "publishing", Publication = new(RawFinalPublicationVersion, RawHash(stage), stage.Length, sha, report.Length, at, row.WorkerCommit!) };
        SaveRawFamily(study, [row], "final-publication-prepared"); // The immutable expected hashes precede stage/file IO.
        return (row, stage);
    }
    private void WriteRawPublicationFile(RawStudyTrialEntry row, string path, byte[] bytes)
    {
        if (!File.Exists(path)) EnsureAdditionalArtifactCapacity(bytes.Length, row.Id);
        ImmutableWrite(path, bytes);
    }
    private byte[] ReadRawPublication(RawResearchStudy study, RawStudyTrialEntry row)
    {
        var publication = row.Publication ?? throw new ResearchException("raw-study-publication-stage-integrity");
        byte[] stage;
        try
        {
            using var file = File.OpenRead(RawPublicationPath(row.Id));
            if (file.Length != publication.StageBytes) throw new ResearchException("raw-study-publication-stage-integrity");
            stage = new byte[publication.StageBytes]; file.ReadExactly(stage);
            if (file.ReadByte() != -1) throw new ResearchException("raw-study-publication-stage-integrity");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ResearchException("raw-study-publication-stage-unavailable"); }
        if (RawHash(stage) != publication.StageSha256) throw new ResearchException("raw-study-publication-stage-integrity");
        var length = BinaryPrimitives.ReadInt32LittleEndian(stage);
        if (length is <= 0 or > RawPublicationHeaderBytes || length != stage.Length - publication.ReportBytes - 4)
            throw new ResearchException("raw-study-publication-stage-integrity");
        RawPublicationHeader? header;
        try { header = JsonSerializer.Deserialize<RawPublicationHeader>(stage.AsSpan(4, length), RawReplayJson.Options); }
        catch (JsonException) { throw new ResearchException("raw-study-publication-stage-integrity"); }
        if (header != PublicationHeader(study, row, publication.ReportSha256, publication.ReportBytes, publication.VerifiedAt))
            throw new ResearchException("raw-study-publication-stage-integrity");
        var report = stage.AsSpan(4 + length).ToArray();
        if (RawHash(report) != publication.ReportSha256) throw new ResearchException("raw-study-publication-stage-integrity");
        return report;
    }
    private void PublishRawFinal(RawResearchStudy study, RawStudyTrialEntry row, byte[] bytes, RawStudyDevelopmentReport report, bool recovered)
    {
        lock (gate)
        {
            var current = ListRawTrials().Single(r => r.Id == row.Id); var parent = RawStudies().Single(s => s.Id == study.Id);
            if (parent.Status != "final-evaluation" || current.Status != "publishing" || current.Publication != row.Publication)
                throw new ResearchException("raw-study-publication-state");
            if (report.Version != RawStudyFinalRun.Version || report.PlanSha256 != study.PlanSha256 || report.TrialId != row.TrialId ||
                report.DatasetSha256 != row.DatasetSha256 || RawHash(bytes) != row.Publication!.ReportSha256)
                throw new ResearchException("raw-study-publication-stage-integrity");
            var at = clock.GetUtcNow(); if (at < row.Publication.VerifiedAt) throw new ResearchException("raw-study-clock-regression");
            var path = RawReportPath(row.Id);
            if (File.Exists(path) && !DatasetArchive.ReadBounded(path).AsSpan().SequenceEqual(bytes))
                throw new ResearchException("raw-study-publication-report-conflict");
            WriteRawPublicationFile(row, path, bytes);
            current = current with { Status = "succeeded", FinishedAt = at, ReportSha256 = row.Publication.ReportSha256, ErrorCode = null };
            parent = CompleteRawFinal(parent, current, report);
            SaveRawFamily(parent, [current], recovered ? "final-publication-recovered" : "trial-succeeded");
        }
    }
    private static void CheckRawPublicationScope(RawResearchStudy study, RawStudyTrialEntry row, byte[] bytes)
    {
        try
        {
            var report = JsonSerializer.Deserialize<RawStudyDevelopmentReport>(bytes, RawReplayJson.Options) ?? throw new JsonException("Missing report.");
            if (report.Version != RawStudyFinalRun.Version || report.PlanSha256 != study.PlanSha256 || report.TrialId != row.TrialId ||
                report.DatasetSha256 != row.DatasetSha256 || report.Strategies.IsDefault || report.Strategies.Any(s => s is null) ||
                !report.Strategies.Select(s => s.Strategy).SequenceEqual(new[] { "momentum", "equal_weight", "cash" }))
                throw new ResearchException("raw-study-publication-stage-integrity");
            foreach (var account in report.Strategies)
            {
                var receipt = account.Receipt;
                if (receipt is null || receipt.Version != RawReplayArchive.Version || receipt.RequestBase64 is not { Length: > 0 } text ||
                    text.Length > 4L * ((RawReplayArchive.MaximumRequestBytes + 2L) / 3))
                    throw new ResearchException("raw-study-publication-stage-integrity");
                var requestBytes = Convert.FromBase64String(text);
                if (requestBytes.Length > RawReplayArchive.MaximumRequestBytes || RawHash(requestBytes) != receipt.RequestSha256)
                    throw new ResearchException("raw-study-publication-stage-integrity");
                var request = JsonSerializer.Deserialize<RawReplayDocument>(requestBytes, RawReplayJson.Options) ?? throw new JsonException("Missing request.");
                if (request.Version != RawReplayArchive.Version || request.EngineVersion != RawEventReplay.Version || request.Build != study.Plan.Build ||
                    request.Study is not { } binding || binding.Version != report.Version || binding.PlanSha256 != study.PlanSha256 ||
                    binding.PlanBase64 != study.PlanBase64 || binding.TrialId != row.TrialId || binding.Strategy != account.Strategy || request.Schedule is not null ||
                    request.Events.IsDefaultOrEmpty || request.Events.Any(e => e is null || e.DatasetSha256 != row.DatasetSha256))
                    throw new ResearchException("raw-study-publication-stage-integrity");
            }
        }
        catch (Exception e) when (e is JsonException or FormatException) { throw new ResearchException("raw-study-publication-stage-integrity"); }
    }
    private static string RawPublicationError(Exception error) => error is ResearchException re ? re.Code :
        error is OperationCanceledException ? "raw-study-publication-interrupted" : "raw-study-publication-write";
    private void RetainRawPublicationError(string id, Exception error)
    {
        lock (gate)
        {
            var row = ListRawTrials().Single(r => r.Id == id); var study = RawStudies().Single(s => s.Id == row.StudyId);
            // A concurrent explicit stop is terminal; never convert it back into pending work.
            var code = RawPublicationError(error);
            if (row.Status == "publishing" && study.Status == "final-evaluation" && row.ErrorCode != code)
                SaveRawFamily(study, [row with { ErrorCode = code }], "final-publication-pending");
        }
    }
    private bool RecoverRawPublication(string id, CancellationToken cancellationToken, out string? error)
    {
        try
        {
            RawResearchStudy study; RawStudyTrialEntry row;
            lock (gate)
            {
                row = ListRawTrials().Single(r => r.Id == id); study = RawStudies().Single(s => s.Id == row.StudyId);
                if (row.Status != "publishing" || study.Status != "final-evaluation") throw new ResearchException("raw-study-publication-state");
                CurrentRawPlan(study); _ = RawTrialRequestBytes(row.Id);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var bytes = ReadRawPublication(study, row); // Hash/header checks precede any dataset read.
            CheckRawPublicationScope(study, row, bytes); // Every nested receipt must name this original claim's plan/trial before replay can read prices.
            byte[] Dataset(string sha)
            {
                if (sha != row.DatasetSha256) throw new ResearchException("raw-replay-dataset-hash");
                try { return RawStudyOriginalBytes(study.Id, study.Originals.Single(o => o.Kind == "dataset").OriginalSha256); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { throw new ResearchException("raw-study-publication-original-unavailable"); }
            }
            var report = RawStudyDevelopmentRun.Verify(bytes, Dataset, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            PublishRawFinal(study, row, bytes, report, recovered: true); error = null; return true;
        }
        catch (Exception e) when (e is ResearchException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            InvalidOperationException or OverflowException or SqliteException or OperationCanceledException)
        { RetainRawPublicationError(id, e); error = RawPublicationError(e); return false; }
    }
    public RawResearchStudy RecoverRawFinalPublication(string id, StudyEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !Guid.TryParseExact(request.RequestId, "D", out var guid)) throw new ResearchException("study-evaluation-request");
        string[] pending;
        lock (gate)
        {
            var study = RawStudies().SingleOrDefault(s => s.Id == id) ?? throw new ResearchException("study-not-found");
            if (request.PlanSha256 != study.PlanSha256) throw new ResearchException("raw-study-plan-integrity");
            if (study.FinalRequestId is null || Guid.Parse(study.FinalRequestId) != guid) throw new ResearchException("study-holdout-spent");
            pending = ListRawTrials().Where(r => r.StudyId == id && r.Status == "publishing").Select(r => r.Id).ToArray();
            if (pending.Length == 0) return study; // Historical/terminal metadata replay is independent of today's science.
            if (!TryBeginComputation()) throw new ResearchException("research-worker-busy");
        }
        try
        {
            foreach (var trial in pending) if (!RecoverRawPublication(trial, cancellationToken, out var error)) throw new ResearchException(error!);
            return RawStudies().Single(s => s.Id == id);
        }
        finally { EndComputation(); }
    }
}
