using Maus.Core.Workshop.Memory;

namespace Maus.Core.Tests.Workshop;

public class SharedLockTests
{
    [Fact]
    public void Abandoned_bus_lock_is_taken_then_released_instead_of_being_kept_forever()
    {
        // Un programme fermé en pleine lecture laisse le verrou « abandonné » : Windows le donne au suivant avec une
        // exception. Avant la correction, MAUS le gardait alors pour toujours et bloquait les lectures suivantes.
        var name = @"Local\MAUS.Test." + Guid.NewGuid().ToString("N");
        using var mutex = new Mutex(false, name);
        var owner = new Thread(() =>
        {
            using var other = Mutex.OpenExisting(name);
            other.WaitOne();
        });
        owner.Start();
        owner.Join();

        Assert.True(PawnIoSpdSource.TryAcquire(mutex, TimeSpan.FromSeconds(1)));
        mutex.ReleaseMutex();

        var released = false;
        var next = new Thread(() =>
        {
            using var other = Mutex.OpenExisting(name);
            released = other.WaitOne(TimeSpan.FromSeconds(1));
            if (released)
            {
                other.ReleaseMutex();
            }
        });
        next.Start();
        next.Join();
        Assert.True(released);
    }

    [Fact]
    public void Missing_lock_is_not_acquired() => Assert.False(PawnIoSpdSource.TryAcquire(null, TimeSpan.Zero));
}
