using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class CommandChunksTest
{
    [Test]
    public void BigCommandSplitsAndComesBackWhole()
    {
        // Random-ish bytes that don't compress below one chunk
        var data = new byte[200_000];
        var rng = new System.Random(5);
        rng.NextBytes(data);
        BitConverter.GetBytes(42).CopyTo(data, 0); // the sync id

        var chunks = CommandChunks.Split(CommandType.Sync, data, groupId: 7);
        Assert.That(chunks, Has.Count.GreaterThan(1));
        Assert.That(chunks.All(c => c.Length <= 65535), Is.True);

        Assert.That(CommandChunks.TryReadHeader(chunks[0], out var type, out var syncId), Is.True);
        Assert.That(type, Is.EqualTo(CommandType.Sync));
        Assert.That(syncId, Is.EqualTo(42));

        var assembler = new CommandChunks.Assembler();
        ScheduledCommand? whole = null;
        for (int i = 0; i < chunks.Count; i++)
        {
            var part = new ScheduledCommand(CommandType.Chunk, 100 + i, 1, 0, 3, chunks[i]);
            var done = assembler.TryAssemble(part, out var result);
            Assert.That(done, Is.EqualTo(i == chunks.Count - 1));
            if (done) whole = result;
        }

        Assert.That(whole, Is.Not.Null);
        Assert.That(whole!.type, Is.EqualTo(CommandType.Sync));
        Assert.That(whole.ticks, Is.EqualTo(100 + chunks.Count - 1));
        Assert.That(whole.playerId, Is.EqualTo(3));
        Assert.That(whole.data, Is.EqualTo(data));
    }

    [Test]
    public void TooLongStringThrowsWhenWritten()
    {
        var writer = new ByteWriter();
        Assert.Throws<WriterException>(() => writer.WriteString(new string('x', ByteWriter.MaxStringBytes + 1)));
    }
}
