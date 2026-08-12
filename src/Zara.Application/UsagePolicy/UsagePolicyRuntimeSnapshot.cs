using Zara.Core.UsagePolicy;

namespace Zara.Application.UsagePolicy;

/// <summary>
/// Exposes the latest settings, evaluated time-rule state, pending emergency-input state, and
/// display-only times calculated by the application runtime.
/// </summary>
public sealed record UsagePolicyRuntimeSnapshot(
    UsagePolicySettings Settings,
    UsagePolicyEvaluation Evaluation,
    DateTime EvaluatedLocalTime,
    bool HasPendingEmergencyChallenge,
    DateTime? EmergencyUnlockEndLocalTime,
    DateTime? NextLockStartLocalTime);
