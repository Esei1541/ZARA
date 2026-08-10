using Zara.Core.Continuity;

namespace Zara.Core.Tests;

[TestClass]
public sealed class RestartContinuityPolicyTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LockRequiredAlwaysRestartsAndRecoversLock(bool restartWhenAvailable)
    {
        RestartContinuityDecision decision = RestartContinuityPolicy.Decide(
            lockRequired: true,
            restartWhenAvailable);

        Assert.IsTrue(decision.RestartRequired);
        Assert.IsTrue(decision.RecoverLock);
    }

    [TestMethod]
    public void LockRequiredWithMissingSettingAlwaysRestartsAndRecoversLock()
    {
        RestartContinuityDecision decision = RestartContinuityPolicy.Decide(
            lockRequired: true,
            restartWhenAvailable: null);

        Assert.IsTrue(decision.RestartRequired);
        Assert.IsTrue(decision.RecoverLock);
    }

    [TestMethod]
    public void LockNotRequiredWithRestartEnabledRestartsWithoutRecoveringLock()
    {
        RestartContinuityDecision decision = RestartContinuityPolicy.Decide(
            lockRequired: false,
            restartWhenAvailable: true);

        Assert.IsTrue(decision.RestartRequired);
        Assert.IsFalse(decision.RecoverLock);
    }

    [TestMethod]
    public void LockNotRequiredWithRestartDisabledDoesNotRestartOrRecoverLock()
    {
        RestartContinuityDecision decision = RestartContinuityPolicy.Decide(
            lockRequired: false,
            restartWhenAvailable: false);

        Assert.IsFalse(decision.RestartRequired);
        Assert.IsFalse(decision.RecoverLock);
    }

    [TestMethod]
    public void LockNotRequiredWithMissingSettingUsesEnabledDefault()
    {
        RestartContinuityDecision decision = RestartContinuityPolicy.Decide(
            lockRequired: false,
            restartWhenAvailable: null);

        Assert.IsTrue(decision.RestartRequired);
        Assert.IsFalse(decision.RecoverLock);
    }
}
