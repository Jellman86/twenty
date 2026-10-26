using Microsoft.Data.Sqlite;
using Twenty.Core.Research;

namespace Twenty.Tests;

internal static class SharedFinalClaimFixture
{
    public static void AssertCaseAliasConflict(string root, Action<string> attempt)
    {
        const string request = "a0b1c2d3-e4f5-4678-9abc-def012345678";
        var foreign = Guid.NewGuid().ToString("N");
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "research.db")); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO study_holdout_claims VALUES($id,$request,'2000-01-01','2001-01-01',1,'2026-01-01T00:00:00.0000000+00:00')";
        cmd.Parameters.AddWithValue("$id", foreign); cmd.Parameters.AddWithValue("$request", request.ToUpperInvariant()); cmd.ExecuteNonQuery();
        try { Assert.Equal("request-id-conflict", Assert.Throws<ResearchException>(() => attempt(request)).Code); }
        finally { cmd.CommandText = "DELETE FROM study_holdout_claims WHERE study_id=$id"; cmd.ExecuteNonQuery(); }
    }
}
