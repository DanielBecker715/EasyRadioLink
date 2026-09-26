using System;
using System.Net;
using EasyRadioLink.Common.Network.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EasyRadioLink.Common.Tests.Network;

[TestClass]
public class AuthFailureThrottleTests
{
    private static readonly IPAddress Attacker = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Other = IPAddress.Parse("203.0.113.8");
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void AddressIsLockedOutAfterTooManyWrongPasswords()
    {
        var throttle = new AuthFailureThrottle();

        for (var i = 1; i < AuthFailureThrottle.MaxFailures; i++)
        {
            Assert.IsFalse(throttle.RecordFailure(Attacker, Start.AddSeconds(i)));
            Assert.IsFalse(throttle.IsLockedOut(Attacker, Start.AddSeconds(i)));
        }

        var now = Start.AddSeconds(AuthFailureThrottle.MaxFailures);
        Assert.IsTrue(throttle.RecordFailure(Attacker, now));
        Assert.IsTrue(throttle.IsLockedOut(Attacker, now));
        Assert.IsTrue(throttle.IsLockedOut(Attacker.MapToIPv6(), now));

        // other addresses are not affected
        Assert.IsFalse(throttle.IsLockedOut(Other, now));

        // the lockout ends
        Assert.IsFalse(throttle.IsLockedOut(Attacker, now + AuthFailureThrottle.LockoutDuration));
    }

    [TestMethod]
    public void FailuresOutsideTheWindowAreForgotten()
    {
        var throttle = new AuthFailureThrottle();

        var now = Start;
        for (var i = 0; i < AuthFailureThrottle.MaxFailures * 3; i++)
        {
            Assert.IsFalse(throttle.RecordFailure(Attacker, now));
            now += AuthFailureThrottle.FailureWindow / (AuthFailureThrottle.MaxFailures - 2);
        }

        Assert.IsFalse(throttle.IsLockedOut(Attacker, now));
    }

    [TestMethod]
    public void CorrectPasswordResetsTheCounter()
    {
        var throttle = new AuthFailureThrottle();

        for (var i = 1; i < AuthFailureThrottle.MaxFailures; i++) throttle.RecordFailure(Attacker, Start);

        throttle.RecordSuccess(Attacker);

        Assert.IsFalse(throttle.RecordFailure(Attacker, Start));
        Assert.IsFalse(throttle.IsLockedOut(Attacker, Start));
    }
}
