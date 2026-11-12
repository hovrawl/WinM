using System.Text.Json;
using Xunit;

namespace HomeDesk.Windows.Tests;

public sealed class RecoveryJsonTests
{
    [Fact]
    public void GeneratedMetadataRoundTripsRecoveryCommandAndJournal()
    {
        var record = new RecoveryRecord(1234, 42, 5678, 3, "WinManager.Recovery.0123456789abcdef0123456789abcdef",
            HideConfirmed: true, Minimization: true);
        var command = new RecoveryCommand("register", record);
        var commandJson = JsonSerializer.Serialize(command, RecoveryJsonContext.Default.RecoveryCommand);
        var restoredCommand = JsonSerializer.Deserialize(commandJson, RecoveryJsonContext.Default.RecoveryCommand);

        Assert.Equal(command, restoredCommand);

        var journal = new RecoveryJournal(42, 5678, [record]);
        var journalJson = JsonSerializer.Serialize(journal, RecoveryJsonContext.Default.RecoveryJournal);
        var restoredJournal = JsonSerializer.Deserialize(journalJson, RecoveryJsonContext.Default.RecoveryJournal);

        Assert.NotNull(restoredJournal);
        Assert.Equal(journal.HostId, restoredJournal.HostId);
        Assert.Equal(journal.HostCreationTime, restoredJournal.HostCreationTime);
        Assert.Equal<List<RecoveryRecord>>(journal.Records, restoredJournal.Records);
        Assert.Contains("\"HostId\"", journalJson);
    }
}
