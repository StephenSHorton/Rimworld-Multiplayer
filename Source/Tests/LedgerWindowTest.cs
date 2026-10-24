using Multiplayer.Common.Desyncs;

namespace Tests;

[TestFixture]
public class LedgerWindowTest
{
    [Test]
    public void SwitchesOnAtFromAndOffAtUntil()
    {
        var w = new LedgerWindow();
        Assert.That(w.Arm(100, 200, 50), Is.EqualTo(LedgerWindow.ArmResult.Armed));
        Assert.That(w.Tick(99), Is.False);
        Assert.That(w.On, Is.False);
        Assert.That(w.Tick(100), Is.True);
        Assert.That(w.On, Is.True);
        Assert.That(w.Tick(150), Is.False);
        Assert.That(w.Tick(200), Is.True);
        Assert.That(w.On, Is.False);
        Assert.That(w.Armed, Is.False);
    }

    [Test]
    public void TooLateOrEmptyStaysOff()
    {
        var w = new LedgerWindow();
        Assert.That(w.Arm(100, 200, 100), Is.EqualTo(LedgerWindow.ArmResult.TooLate));
        Assert.That(w.Arm(100, 100, 50), Is.EqualTo(LedgerWindow.ArmResult.Empty));
        Assert.That(w.Armed, Is.False);
    }

    [Test]
    public void ArmingAgainExtendsFromTheSameStart()
    {
        var w = new LedgerWindow();
        w.Arm(100, 200, 50);
        Assert.That(w.Arm(150, 300, 60), Is.EqualTo(LedgerWindow.ArmResult.Extended));
        Assert.That(w.From, Is.EqualTo(100));
        Assert.That(w.Until, Is.EqualTo(300));
        w.Arm(150, 250, 60);
        Assert.That(w.Until, Is.EqualTo(300));
    }

    [Test]
    public void ResetDropsAnOpenWindowSoTheNextSessionStartsOff()
    {
        var w = new LedgerWindow();
        w.Arm(200000, 210000, 199000);
        w.Tick(200000);
        Assert.That(w.On, Is.True);

        Assert.That(w.Reset(), Is.True);
        Assert.That(w.On, Is.False);
        Assert.That(w.Until, Is.EqualTo(-1));

        // The next session's timer starts near 0: nothing switches on
        for (int t = 0; t < 100; t++)
            Assert.That(w.Tick(t), Is.False);
        Assert.That(w.On, Is.False);
        Assert.That(w.Reset(), Is.False);
    }
}
