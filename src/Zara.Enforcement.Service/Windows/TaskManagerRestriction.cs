using Microsoft.Win32;
using Zara.Supervision.Contracts;

namespace Zara.Enforcement.Service.Windows;

internal interface ITaskManagerRestriction
{
    void Apply(SupervisedProcessIdentity process);

    void Restore();
}

internal interface ITaskManagerPolicyStore
{
    TaskManagerPolicyValue ReadPolicy();

    bool IsExternallyManaged();

    void WritePolicy(TaskManagerPolicyValue value);

    TaskManagerRecoveryRecord? ReadRecovery();

    void WriteRecovery(TaskManagerRecoveryRecord record);

    void DeleteRecovery();
}

internal sealed record TaskManagerPolicyValue(
    bool KeyExists,
    bool ValueExists,
    RegistryValueKind ValueKind,
    object? Value);

internal sealed record TaskManagerRecoveryRecord(
    string InstallDirectory,
    string UserSid,
    SupervisedProcessIdentity Owner,
    TaskManagerPolicyValue Baseline);

internal sealed class TaskManagerRestriction : ITaskManagerRestriction
{
    private static readonly TaskManagerPolicyValue AppliedPolicy = new(
        KeyExists: true,
        ValueExists: true,
        RegistryValueKind.DWord,
        Value: 1);

    private readonly ITaskManagerPolicyStore _store;
    private readonly string _installDirectory;
    private readonly string _userSid;
    private readonly object _sync = new();

    internal TaskManagerRestriction(
        ITaskManagerPolicyStore store,
        string installDirectory,
        string userSid)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        _store = store;
        _installDirectory = NormalizeDirectory(installDirectory);
        _userSid = userSid;
    }

    internal static ITaskManagerRestriction Create(
        string installDirectory,
        string userSid)
    {
        var restriction = new TaskManagerRestriction(
            new WindowsTaskManagerPolicyStore(userSid),
            installDirectory,
            userSid);

        // A service crash can leave a prepared journal behind. Recover it before a new desktop
        // generation captures a baseline, so the value written by the previous generation is not
        // mistaken for an externally owned value.
        restriction.Restore();
        return restriction;
    }

    public void Apply(SupervisedProcessIdentity process)
    {
        lock (_sync)
        {
            ApplyCore(process);
        }
    }

    private void ApplyCore(SupervisedProcessIdentity process)
    {
        ValidateOwner(process);

        try
        {
            TaskManagerRecoveryRecord? recovery = _store.ReadRecovery();
            if (recovery is not null)
            {
                ValidateRecoveryTarget(recovery);
                if (_store.IsExternallyManaged())
                {
                    _store.DeleteRecovery();
                    throw new InvalidOperationException(
                        "Task Manager policy is externally managed.");
                }

                TaskManagerPolicyValue current = _store.ReadPolicy();
                if (SameOwner(recovery.Owner, process) && IsAppliedPolicy(current))
                {
                    return;
                }

                Restore();
            }

            if (_store.IsExternallyManaged())
            {
                throw new InvalidOperationException(
                    "Task Manager policy is externally managed.");
            }

            TaskManagerPolicyValue baseline = _store.ReadPolicy();
            if (baseline.ValueExists)
            {
                if (IsAppliedPolicy(baseline))
                {
                    return;
                }

                throw new InvalidOperationException(
                    "Task Manager policy already has an externally owned value.");
            }

            _store.WriteRecovery(new TaskManagerRecoveryRecord(
                _installDirectory,
                _userSid,
                process,
                baseline));
            _store.WritePolicy(AppliedPolicy);

            TaskManagerPolicyValue applied = _store.ReadPolicy();
            if (!IsAppliedPolicy(applied))
            {
                throw new InvalidOperationException(
                    "Task Manager policy write could not be verified.");
            }
        }
        catch (Exception applyFailure)
        {
            try
            {
                Restore();
            }
            catch (Exception restoreFailure)
            {
                throw new AggregateException(applyFailure, restoreFailure);
            }

            throw;
        }
    }

    public void Restore()
    {
        lock (_sync)
        {
            RestoreCore();
        }
    }

    private void RestoreCore()
    {
        TaskManagerRecoveryRecord? recovery = _store.ReadRecovery();
        if (recovery is null)
        {
            return;
        }

        ValidateRecoveryTarget(recovery);

        // Once a management source appears, ZARA cannot distinguish its same-value refresh from
        // the value ZARA wrote. Relinquish ownership without changing the managed setting.
        if (_store.IsExternallyManaged())
        {
            _store.DeleteRecovery();
            return;
        }

        TaskManagerPolicyValue current = _store.ReadPolicy();
        if (PolicyEquals(current, recovery.Baseline))
        {
            _store.DeleteRecovery();
            return;
        }

        if (!IsAppliedPolicy(current))
        {
            // Another actor changed the value after ZARA applied it. Do not overwrite that value.
            _store.DeleteRecovery();
            return;
        }

        _store.WritePolicy(recovery.Baseline);
        TaskManagerPolicyValue restored = _store.ReadPolicy();
        if (!PolicyEquals(restored, recovery.Baseline))
        {
            throw new InvalidOperationException(
                "Task Manager policy restoration could not be verified.");
        }

        _store.DeleteRecovery();
    }

    private void ValidateOwner(SupervisedProcessIdentity process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.ProcessId <= 0 ||
            process.ProcessStartTimeUtcTicks <= 0 ||
            process.SessionId < 0 ||
            !string.Equals(process.UserSid, _userSid, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The supervised process identity does not match the target user.",
                nameof(process));
        }
    }

    private void ValidateRecoveryTarget(TaskManagerRecoveryRecord recovery)
    {
        bool ownerIsValid = recovery.Owner.ProcessId > 0 &&
            recovery.Owner.ProcessStartTimeUtcTicks > 0 &&
            recovery.Owner.SessionId >= 0 &&
            string.Equals(recovery.Owner.UserSid, _userSid, StringComparison.Ordinal);

        string normalizedRecoveryDirectory;
        try
        {
            normalizedRecoveryDirectory = NormalizeDirectory(recovery.InstallDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                "Task Manager recovery record has an invalid installation directory.",
                exception);
        }

        if (recovery.Baseline.ValueExists ||
            !ownerIsValid ||
            !string.Equals(recovery.UserSid, _userSid, StringComparison.Ordinal) ||
            !string.Equals(
                normalizedRecoveryDirectory,
                _installDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Task Manager recovery record does not belong to this installation and user.");
        }
    }

    private static bool SameOwner(
        SupervisedProcessIdentity first,
        SupervisedProcessIdentity second)
    {
        return first.ProcessId == second.ProcessId &&
            first.ProcessStartTimeUtcTicks == second.ProcessStartTimeUtcTicks &&
            first.SessionId == second.SessionId &&
            string.Equals(first.UserSid, second.UserSid, StringComparison.Ordinal);
    }

    private static bool IsAppliedPolicy(TaskManagerPolicyValue value)
    {
        return value.ValueExists &&
            value.ValueKind == RegistryValueKind.DWord &&
            value.Value is int dword &&
            dword == 1;
    }

    private static bool PolicyEquals(
        TaskManagerPolicyValue first,
        TaskManagerPolicyValue second)
    {
        if (first.ValueExists != second.ValueExists)
        {
            return false;
        }

        if (!first.ValueExists)
        {
            // The policy value is the owned effect. If another actor adds a sibling value while
            // ZARA is active, the System key must remain and absence of DisableTaskMgr is enough.
            return true;
        }

        if (first.ValueKind != second.ValueKind)
        {
            return false;
        }

        return first.Value switch
        {
            byte[] firstBytes when second.Value is byte[] secondBytes =>
                firstBytes.AsSpan().SequenceEqual(secondBytes),
            string[] firstStrings when second.Value is string[] secondStrings =>
                firstStrings.SequenceEqual(secondStrings, StringComparer.Ordinal),
            _ => Equals(first.Value, second.Value),
        };
    }

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
