using PhoneTransfer.Application.Pairing;
using Xunit;

namespace PhoneTransfer.Tests;

public class PairingChallengeTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private DateTimeOffset utc = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => utc;
        public void Advance(TimeSpan duration) { ticks += duration.Ticks; utc += duration; }
        public void SetUtc(DateTimeOffset value) => utc = value;
    }

    [Fact]
    public void RejectsReplayAndDoesNotLogToken()
    {
        var store = new PairingChallengeStore(new Clock());
        var challenge = store.Issue();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", challenge.Token);
        Assert.DoesNotContain(challenge.Token, challenge.ToString());
        var created = 0;
        Assert.True(store.TryConsume(challenge.Token, () => created++));
        Assert.False(store.TryConsume(challenge.Token, () => created++));
        Assert.Equal(1, created);
    }

    [Fact]
    public void NewQrAndExplicitInvalidationRejectOldTokens()
    {
        var store = new PairingChallengeStore(new Clock());
        var first = store.Issue();
        var second = store.Issue();
        Assert.NotEqual(first.Token, second.Token);
        Assert.False(store.TryConsume(first.Token, () => Assert.Fail("Old QR accepted")));
        store.Invalidate();
        Assert.False(store.TryConsume(second.Token, () => Assert.Fail("Closed QR accepted")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidTokenDoesNotConsumeRealChallenge(string? candidate)
    {
        var store = new PairingChallengeStore(new Clock());
        var challenge = store.Issue();
        Assert.False(store.TryConsume(candidate, () => Assert.Fail("Invalid token accepted")));
        Assert.True(store.TryConsume(challenge.Token, () => { }));
    }

    [Fact]
    public void ExpiresAtBoundaryEvenWhenWallClockMovesBackward()
    {
        var clock = new Clock();
        var store = new PairingChallengeStore(clock);
        var challenge = store.Issue();
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), challenge.ExpiresAt);
        clock.Advance(TimeSpan.FromSeconds(120));
        clock.SetUtc(clock.GetUtcNow().AddDays(-1));
        Assert.False(store.TryConsume(challenge.Token, () => Assert.Fail("Expired token accepted")));
    }

    [Fact]
    public void ValidImmediatelyBeforeExpiry()
    {
        var clock = new Clock();
        var store = new PairingChallengeStore(clock);
        var challenge = store.Issue();
        clock.Advance(TimeSpan.FromSeconds(120) - TimeSpan.FromTicks(1));
        Assert.True(store.TryConsume(challenge.Token, () => { }));
    }

    [Fact]
    public async Task ConcurrentConsumptionCreatesExactlyOnePendingRequest()
    {
        var store = new PairingChallengeStore(new Clock());
        var challenge = store.Issue();
        var created = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            store.TryConsume(challenge.Token, () => Interlocked.Increment(ref created)))));
        Assert.Single(results, success => success);
        Assert.Equal(1, created);
    }

    [Fact]
    public void CallbackFailureCannotMakeTokenReplayable()
    {
        var store = new PairingChallengeStore(new Clock());
        var challenge = store.Issue();
        Assert.Throws<IOException>(() => store.TryConsume(challenge.Token, () => throw new IOException()));
        Assert.False(store.TryConsume(challenge.Token, () => Assert.Fail("Token replayed")));
    }
}
