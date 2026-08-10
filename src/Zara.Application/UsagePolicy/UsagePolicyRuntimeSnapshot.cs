using Zara.Core.UsagePolicy;

namespace Zara.Application.UsagePolicy;

/// <summary>
/// Exposes the latest settings, evaluated time-rule state, and pending emergency-input state.
/// </summary>
public sealed record UsagePolicyRuntimeSnapshot(
    UsagePolicySettings Settings,
    UsagePolicyEvaluation Evaluation,
    bool HasPendingEmergencyChallenge);
