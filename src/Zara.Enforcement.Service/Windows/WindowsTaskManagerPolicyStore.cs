using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Zara.Supervision.Contracts;

namespace Zara.Enforcement.Service.Windows;

internal sealed class WindowsTaskManagerPolicyStore : ITaskManagerPolicyStore
{
    private const string PolicySubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PolicyValueName = "DisableTaskMgr";
    private const string RecoveryRoot =
        @"SOFTWARE\ZARA\Recovery\TaskManagerRestriction";
    private const string RecoveryValueName = "Record";

    // Registry policy client-side extension. GetAppliedGPOList returns only GPOs that registered
    // data for this extension, which is the narrow supported signal available without evaluating
    // arbitrary Group Policy files ourselves.
    private static readonly Guid RegistryPolicyExtension =
        new("35378EAC-683F-11D2-A89A-00C04FBBCFA2");

    private readonly string _userSid;
    private readonly SecurityIdentifier _securityIdentifier;
    private readonly string _recoveryPath;

    internal WindowsTaskManagerPolicyStore(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        _securityIdentifier = new SecurityIdentifier(userSid);
        _userSid = _securityIdentifier.Value;
        if (!string.Equals(_userSid, userSid, StringComparison.Ordinal))
        {
            throw new ArgumentException("The user SID must be in canonical form.", nameof(userSid));
        }

        _recoveryPath = $@"{RecoveryRoot}\{_userSid}";
    }

    public TaskManagerPolicyValue ReadPolicy()
    {
        using RegistryKey users = RegistryKey.OpenBaseKey(
            RegistryHive.Users,
            RegistryView.Default);
        using RegistryKey userHive = users.OpenSubKey(_userSid, writable: false) ??
            throw new IOException("The target user registry hive is not loaded.");
        using RegistryKey? key = userHive.OpenSubKey(PolicySubKey, writable: false);
        if (key is null)
        {
            return new TaskManagerPolicyValue(
                KeyExists: false,
                ValueExists: false,
                RegistryValueKind.None,
                Value: null);
        }

        bool valueExists = key.GetValueNames().Contains(
            PolicyValueName,
            StringComparer.OrdinalIgnoreCase);
        if (!valueExists)
        {
            return new TaskManagerPolicyValue(
                KeyExists: true,
                ValueExists: false,
                RegistryValueKind.None,
                Value: null);
        }

        RegistryValueKind valueKind = key.GetValueKind(PolicyValueName);
        object? value = key.GetValue(
            PolicyValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            throw new IOException("Task Manager policy disappeared while it was being read.");
        }

        return new TaskManagerPolicyValue(
            KeyExists: true,
            ValueExists: true,
            valueKind,
            CloneRegistryValue(value));
    }

    public bool IsExternallyManaged()
    {
        return HasAppliedRegistryPolicyGpo() ||
            IsDeviceRegisteredWithManagement() ||
            HasTaskManagerPolicyManagerValue();
    }

    public void WritePolicy(TaskManagerPolicyValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        using RegistryKey users = RegistryKey.OpenBaseKey(
            RegistryHive.Users,
            RegistryView.Default);
        using RegistryKey userHive = users.OpenSubKey(_userSid, writable: true) ??
            throw new IOException("The target user registry hive is not loaded.");
        if (value.ValueExists)
        {
            object registryValue = ValidateRegistryValue(value);
            using RegistryKey key = userHive.CreateSubKey(PolicySubKey, writable: true);
            key.SetValue(PolicyValueName, registryValue, value.ValueKind);
            key.Flush();
            return;
        }

        using (RegistryKey? key = userHive.OpenSubKey(PolicySubKey, writable: true))
        {
            if (key is null)
            {
                return;
            }

            key.DeleteValue(PolicyValueName, throwOnMissingValue: false);
            key.Flush();
        }

        // Keep the containing System key even when ZARA created it. Removing a registry key can
        // delete sibling values written by another actor; the owned effect is only DisableTaskMgr.
    }

    public TaskManagerRecoveryRecord? ReadRecovery()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Default);
        using RegistryKey? key = machine.OpenSubKey(_recoveryPath, writable: false);
        object? stored = key?.GetValue(
            RecoveryValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (stored is null)
        {
            return null;
        }

        if (stored is not string serialized)
        {
            throw new InvalidDataException(
                "Task Manager recovery record has an invalid registry type.");
        }

        return TaskManagerRecoveryRecordCodec.Deserialize(serialized);
    }

    public void WriteRecovery(TaskManagerRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(record.UserSid, _userSid, StringComparison.Ordinal) ||
            !string.Equals(record.Owner.UserSid, _userSid, StringComparison.Ordinal) ||
            record.Baseline.ValueExists)
        {
            throw new InvalidOperationException(
                "Task Manager recovery record is not valid for this user and policy.");
        }

        string serialized = TaskManagerRecoveryRecordCodec.Serialize(record);
        using RegistryKey machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Default);
        RegistrySecurity security = CreateRecoverySecurity();
        using RegistryKey key = machine.CreateSubKey(
            _recoveryPath,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryOptions.None,
            security);
        key.SetAccessControl(security);
        key.SetValue(RecoveryValueName, serialized, RegistryValueKind.String);
        key.Flush();
    }

    public void DeleteRecovery()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Default);
        using (RegistryKey? key = machine.OpenSubKey(_recoveryPath, writable: true))
        {
            if (key is null)
            {
                return;
            }

            key.DeleteValue(RecoveryValueName, throwOnMissingValue: false);
            key.Flush();
        }

        // Keep the protected per-user container. The journal is the single named value; deleting
        // the key could discard unrelated diagnostic values if a future version adds any.
    }

    private bool HasAppliedRegistryPolicyGpo()
    {
        byte[] sid = new byte[_securityIdentifier.BinaryLength];
        _securityIdentifier.GetBinaryForm(sid, 0);
        GCHandle pinnedSid = GCHandle.Alloc(sid, GCHandleType.Pinned);
        nint policyList = nint.Zero;
        bool hasAppliedPolicy;
        try
        {
            uint error = TaskManagerPolicyNativeMethods.GetAppliedGpoList(
                flags: 0,
                machineName: null,
                pinnedSid.AddrOfPinnedObject(),
                in RegistryPolicyExtension,
                out policyList);
            if (error != 0)
            {
                throw new Win32Exception(
                    checked((int)error),
                    "Could not determine applied user registry policies.");
            }

            hasAppliedPolicy = policyList != nint.Zero;
        }
        finally
        {
            if (pinnedSid.IsAllocated)
            {
                pinnedSid.Free();
            }

            if (policyList != nint.Zero)
            {
                _ = TaskManagerPolicyNativeMethods.FreeGpoList(policyList);
            }
        }

        return hasAppliedPolicy;
    }

    private static RegistrySecurity CreateRecoverySecurity()
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateFullControlRule(WellKnownSidType.LocalSystemSid));
        security.AddAccessRule(CreateFullControlRule(
            WellKnownSidType.BuiltinAdministratorsSid));
        return security;
    }

    private static RegistryAccessRule CreateFullControlRule(WellKnownSidType sidType)
    {
        return new RegistryAccessRule(
            new SecurityIdentifier(sidType, domainSid: null),
            RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow);
    }

    private static bool IsDeviceRegisteredWithManagement()
    {
        int result = TaskManagerPolicyNativeMethods.IsDeviceRegisteredWithManagement(
            out bool isRegistered,
            userPrincipalNameLength: 0,
            userPrincipalName: nint.Zero);
        if (result < 0)
        {
            throw new InvalidOperationException(
                "Could not determine whether Windows is managed by MDM.",
                Marshal.GetExceptionForHR(result));
        }

        return isRegistered;
    }

    private bool HasTaskManagerPolicyManagerValue()
    {
        string policyManagerPath =
            $@"SOFTWARE\Microsoft\PolicyManager\current\{_userSid}\ADMX_CtrlAltDel";
        using RegistryKey machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Default);
        using RegistryKey? policyManager = machine.OpenSubKey(
            policyManagerPath,
            writable: false);
        return policyManager?.GetValueNames().Contains(
            PolicyValueName,
            StringComparer.OrdinalIgnoreCase) == true;
    }

    private static object ValidateRegistryValue(TaskManagerPolicyValue value)
    {
        return value.ValueKind switch
        {
            RegistryValueKind.DWord when value.Value is int dword => dword,
            RegistryValueKind.QWord when value.Value is long qword => qword,
            RegistryValueKind.String when value.Value is string text => text,
            RegistryValueKind.ExpandString when value.Value is string expandable => expandable,
            RegistryValueKind.MultiString when value.Value is string[] strings =>
                strings.ToArray(),
            RegistryValueKind.Binary when value.Value is byte[] bytes => bytes.ToArray(),
            _ => throw new InvalidDataException(
                "Task Manager policy has an unsupported registry value type."),
        };
    }

    private static object CloneRegistryValue(object value)
    {
        return value switch
        {
            byte[] bytes => bytes.ToArray(),
            string[] strings => strings.ToArray(),
            _ => value,
        };
    }
}

internal static class TaskManagerRecoveryRecordCodec
{
    private const int CurrentVersion = 1;

    internal static string Serialize(TaskManagerRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Baseline.ValueExists)
        {
            throw new InvalidDataException(
                "Task Manager recovery records can only contain an absent baseline.");
        }

        var payload = new RecoveryPayload(
            CurrentVersion,
            record.InstallDirectory,
            record.UserSid,
            record.Owner.ProcessId,
            record.Owner.ProcessStartTimeUtcTicks,
            record.Owner.SessionId,
            record.Owner.UserSid,
            record.Baseline.KeyExists,
            record.Baseline.ValueExists);
        return JsonSerializer.Serialize(payload);
    }

    internal static TaskManagerRecoveryRecord Deserialize(string serialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialized);
        RecoveryPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<RecoveryPayload>(serialized) ??
                throw new InvalidDataException("Task Manager recovery record is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Task Manager recovery record is invalid.",
                exception);
        }

        if (payload.Version != CurrentVersion ||
            string.IsNullOrWhiteSpace(payload.InstallDirectory) ||
            string.IsNullOrWhiteSpace(payload.UserSid) ||
            string.IsNullOrWhiteSpace(payload.OwnerUserSid) ||
            payload.BaselineValueExists)
        {
            throw new InvalidDataException("Task Manager recovery record is incomplete.");
        }

        return new TaskManagerRecoveryRecord(
            payload.InstallDirectory,
            payload.UserSid,
            new SupervisedProcessIdentity(
                payload.ProcessId,
                payload.ProcessStartTimeUtcTicks,
                payload.SessionId,
                payload.OwnerUserSid),
            new TaskManagerPolicyValue(
                payload.BaselineKeyExists,
                ValueExists: false,
                RegistryValueKind.None,
                Value: null));
    }

    private sealed record RecoveryPayload(
        int Version,
        string InstallDirectory,
        string UserSid,
        int ProcessId,
        long ProcessStartTimeUtcTicks,
        int SessionId,
        string OwnerUserSid,
        bool BaselineKeyExists,
        bool BaselineValueExists);
}

internal static partial class TaskManagerPolicyNativeMethods
{
    [LibraryImport(
        "userenv.dll",
        EntryPoint = "GetAppliedGPOListW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetAppliedGpoList(
        uint flags,
        string? machineName,
        nint userSid,
        in Guid extension,
        out nint policyList);

    [LibraryImport("userenv.dll", EntryPoint = "FreeGPOListW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FreeGpoList(nint policyList);

    [LibraryImport(
        "mdmregistration.dll",
        EntryPoint = "IsDeviceRegisteredWithManagement",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int IsDeviceRegisteredWithManagement(
        [MarshalAs(UnmanagedType.Bool)] out bool isRegistered,
        uint userPrincipalNameLength,
        nint userPrincipalName);
}
