using System.Security.Cryptography;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventQueueInitializationTests
{
    [Fact]
    public void Fresh_initialization_creates_a_readable_empty_queue_and_refuses_reuse()
    {
        using var database = new TemporaryAgentDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
        var options = database.CreateOptions();

        GameEventQueueInitialization.CreateFresh(options);
        var outbox = new SqliteGameEventOutbox(options);
        var identity = outbox.GetState();
        var bytes = SHA256.HashData(File.ReadAllBytes(options.DatabasePath));

        var repeat = () => GameEventQueueInitialization.CreateFresh(options);
        repeat.Should().Throw<IOException>();
        outbox.GetState().Should().Be(identity);
        outbox.GetStatistics().Should().Be(new GameEventQueueStatistics(0, 0, 0, 0, 1));
        SHA256.HashData(File.ReadAllBytes(options.DatabasePath)).Should().Equal(bytes);
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void Fresh_initialization_preserves_orphaned_coordination_files(string suffix)
    {
        using var database = new TemporaryAgentDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
        File.WriteAllText(database.DatabasePath + suffix, "recovery evidence");
        var options = database.CreateOptions();

        var action = () => GameEventQueueInitialization.CreateFresh(options);

        action.Should().Throw<InvalidOperationException>();
        File.Exists(database.DatabasePath).Should().BeFalse();
        File.ReadAllText(database.DatabasePath + suffix).Should().Be("recovery evidence");
    }

    [Fact]
    public void Fresh_initialization_requires_the_prepared_directory_and_explicit_command()
    {
        using var database = new TemporaryAgentDatabase();
        var options = database.CreateOptions();
        var action = () => GameEventQueueInitialization.CreateFresh(options);
        action.Should().Throw<InvalidOperationException>();
        Directory.Exists(Path.GetDirectoryName(database.DatabasePath)).Should().BeFalse();
        GameEventAgentCommandLine.TryParse(["initialize-queue"], out _, out _).Should().BeFalse();
        GameEventAgentCommandLine.TryParse(["initialize-queue", "--fresh"], out var command, out _).Should().BeTrue();
        command.Should().BeOfType<InitializeQueueCommand>();
    }
}
