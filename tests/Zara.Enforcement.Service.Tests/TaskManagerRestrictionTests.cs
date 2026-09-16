using Microsoft.Win32;
using Zara.Enforcement.Service.Windows;
using Zara.Supervision.Contracts;

namespace Zara.Enforcement.Service.Tests;

[TestClass]
public sealed class TaskManagerRestrictionTests
{
    private const string InstallDirectory = @"C:\Program Files\ZARA";
    private const string UserSid = "S-1-5-21-100-200-300-1001";

    [TestMethod]
    public void AbsentPolicyIsJournaledBeforeApplyAndRestoredToAbsent()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false));
        var restriction = CreateRestriction(store);

        restriction.Apply(CreateOwner());

        Assert.IsTrue(IsApplied(store.Policy));
        Assert.IsNotNull(store.Recovery);
        Assert.IsLessThan(
            store.Operations.IndexOf("policy-write"),
            store.Operations.IndexOf("recovery-write"));

        restriction.Restore();

        Assert.IsFalse(store.Policy.ValueExists);
        Assert.IsTrue(
            store.Policy.KeyExists,
            "The native store keeps the containing key to avoid deleting sibling policy values.");
        Assert.IsNull(store.Recovery);
    }

    [TestMethod]
    public void ExistingDwordOneIsAcceptedWithoutTakingOwnership()
    {
        var store = new FakeTaskManagerPolicyStore(ValuePolicy(
            RegistryValueKind.DWord,
            1));
        var restriction = CreateRestriction(store);

        restriction.Apply(CreateOwner());

        Assert.AreEqual(0, store.PolicyWriteCount);
        Assert.AreEqual(0, store.RecoveryWriteCount);
        Assert.IsNull(store.Recovery);
    }

    [TestMethod]
    public void ExistingValuesOfEverySupportedRegistryTypeAreNeverOverwritten()
    {
        string[] multiStringValue = ["one", "two"];
        byte[] binaryValue = [1, 2, 3];
        TaskManagerPolicyValue[] existingValues =
        [
            ValuePolicy(RegistryValueKind.DWord, 0),
            ValuePolicy(RegistryValueKind.QWord, 5L),
            ValuePolicy(RegistryValueKind.String, "1"),
            ValuePolicy(RegistryValueKind.ExpandString, "%TEMP%"),
            ValuePolicy(RegistryValueKind.MultiString, multiStringValue),
            ValuePolicy(RegistryValueKind.Binary, binaryValue),
        ];

        foreach (TaskManagerPolicyValue existing in existingValues)
        {
            var store = new FakeTaskManagerPolicyStore(existing);
            var restriction = CreateRestriction(store);

            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => restriction.Apply(CreateOwner()));

            Assert.AreSame(existing, store.Policy);
            Assert.AreEqual(0, store.PolicyWriteCount);
            Assert.AreEqual(0, store.RecoveryWriteCount);
        }
    }

    [TestMethod]
    public void ExternallyManagedPolicyIsRejectedWithoutPolicyOrJournalWrites()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false))
        {
            ExternallyManaged = true,
        };
        var restriction = CreateRestriction(store);

        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => restriction.Apply(CreateOwner()));

        Assert.AreEqual(0, store.PolicyWriteCount);
        Assert.AreEqual(0, store.RecoveryWriteCount);
        Assert.AreEqual(0, store.RecoveryDeleteCount);
    }

    [TestMethod]
    public void RepeatedApplyForSameOwnerDoesNotReplaceBaselineJournal()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: true));
        var restriction = CreateRestriction(store);
        SupervisedProcessIdentity owner = CreateOwner();

        restriction.Apply(owner);
        TaskManagerRecoveryRecord firstRecovery = store.Recovery ??
            throw new AssertFailedException("Apply should prepare a recovery record.");
        restriction.Apply(owner);

        Assert.AreSame(firstRecovery, store.Recovery);
        Assert.AreEqual(1, store.PolicyWriteCount);
        Assert.AreEqual(1, store.RecoveryWriteCount);
        Assert.AreEqual(0, store.RecoveryDeleteCount);
    }

    [TestMethod]
    public void NewOwnerRestoresPreviousGenerationBeforeTakingOwnership()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false));
        var restriction = CreateRestriction(store);
        SupervisedProcessIdentity firstOwner = CreateOwner(processId: 40, startTicks: 400);
        SupervisedProcessIdentity secondOwner = CreateOwner(processId: 41, startTicks: 401);

        restriction.Apply(firstOwner);
        restriction.Apply(secondOwner);

        Assert.IsTrue(IsApplied(store.Policy));
        Assert.AreEqual(secondOwner, store.Recovery?.Owner);
        Assert.AreEqual(3, store.PolicyWriteCount);
        Assert.AreEqual(2, store.RecoveryWriteCount);
        Assert.AreEqual(1, store.RecoveryDeleteCount);
    }

    [TestMethod]
    public void CrashAfterJournalBeforePolicyWriteDeletesOnlyTheJournal()
    {
        var baseline = AbsentPolicy(keyExists: false);
        var store = new FakeTaskManagerPolicyStore(baseline)
        {
            Recovery = CreateRecovery(CreateOwner(), baseline),
        };
        var restriction = CreateRestriction(store);

        restriction.Restore();

        Assert.AreSame(baseline, store.Policy);
        Assert.IsNull(store.Recovery);
        Assert.AreEqual(0, store.PolicyWriteCount);
        Assert.AreEqual(1, store.RecoveryDeleteCount);
    }

    [TestMethod]
    public void PolicyWriteThatThrowsAfterMutationRollsBackFromJournal()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false))
        {
            FailureAfterNextPolicyWrite = new IOException("write failed after mutation"),
        };
        var restriction = CreateRestriction(store);

        IOException actual = Assert.ThrowsExactly<IOException>(
            () => restriction.Apply(CreateOwner()));

        Assert.AreEqual("write failed after mutation", actual.Message);
        Assert.IsFalse(store.Policy.ValueExists);
        Assert.IsNull(store.Recovery);
        Assert.AreEqual(2, store.PolicyWriteCount);
    }

    [TestMethod]
    public void RestoreFailureKeepsJournalForALaterRetry()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false));
        var restriction = CreateRestriction(store);
        restriction.Apply(CreateOwner());
        store.FailureBeforeNextPolicyWrite = new IOException("restore write failed");

        _ = Assert.ThrowsExactly<IOException>(() => restriction.Restore());

        Assert.IsTrue(IsApplied(store.Policy));
        Assert.IsNotNull(store.Recovery);
        Assert.AreEqual(0, store.RecoveryDeleteCount);
    }

    [TestMethod]
    public void ExternalValueChangeIsNotOverwrittenDuringRestore()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false));
        var restriction = CreateRestriction(store);
        restriction.Apply(CreateOwner());
        TaskManagerPolicyValue external = ValuePolicy(RegistryValueKind.DWord, 0);
        store.Policy = external;

        restriction.Restore();

        Assert.AreSame(external, store.Policy);
        Assert.IsNull(store.Recovery);
        Assert.AreEqual(1, store.PolicyWriteCount);
    }

    [TestMethod]
    public void ManagementThatAppearsAfterApplyTakesOwnershipWithoutAPolicyWrite()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false));
        var restriction = CreateRestriction(store);
        restriction.Apply(CreateOwner());
        store.ExternallyManaged = true;

        restriction.Restore();

        Assert.IsTrue(IsApplied(store.Policy));
        Assert.IsNull(store.Recovery);
        Assert.AreEqual(1, store.PolicyWriteCount);
        Assert.AreEqual(1, store.RecoveryDeleteCount);
    }

    [TestMethod]
    public void InvalidRecoveryTargetIsRejectedWithoutWritesOrDeletion()
    {
        TaskManagerRecoveryRecord[] invalidRecords =
        [
            CreateRecovery(CreateOwner(), AbsentPolicy(false)) with
            {
                InstallDirectory = @"C:\OtherZara",
            },
            CreateRecovery(CreateOwner(), AbsentPolicy(false)) with
            {
                UserSid = "S-1-5-21-100-200-300-1002",
            },
            CreateRecovery(CreateOwner(processId: 0), AbsentPolicy(false)),
            CreateRecovery(
                CreateOwner(),
                ValuePolicy(RegistryValueKind.String, "not-an-allowed-baseline")),
        ];

        foreach (TaskManagerRecoveryRecord invalidRecord in invalidRecords)
        {
            var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false))
            {
                Recovery = invalidRecord,
            };
            var restriction = CreateRestriction(store);

            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => restriction.Restore());

            Assert.AreSame(invalidRecord, store.Recovery);
            Assert.AreEqual(0, store.PolicyWriteCount);
            Assert.AreEqual(0, store.RecoveryDeleteCount);
        }
    }

    [TestMethod]
    public void ApplyReadBackMismatchRelinquishesJournalWithoutOverwritingNewValue()
    {
        var replacement = ValuePolicy(RegistryValueKind.DWord, 0);
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false))
        {
            ReplacementForNextPolicyWrite = replacement,
        };
        var restriction = CreateRestriction(store);

        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => restriction.Apply(CreateOwner()));

        Assert.AreSame(replacement, store.Policy);
        Assert.IsNull(store.Recovery);
        Assert.AreEqual(1, store.PolicyWriteCount);
        Assert.AreEqual(1, store.RecoveryDeleteCount);
    }

    [TestMethod]
    public void RestoreReadBackAcceptsAbsentValueWhenContainingKeyMustRemain()
    {
        var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false))
        {
            KeepPolicyKeyWhenDeletingValue = true,
        };
        var restriction = CreateRestriction(store);
        restriction.Apply(CreateOwner());

        restriction.Restore();

        Assert.IsTrue(store.Policy.KeyExists);
        Assert.IsFalse(store.Policy.ValueExists);
        Assert.IsNull(store.Recovery);
    }

    [TestMethod]
    public void InvalidOwnerIsRejectedBeforeStoreAccess()
    {
        SupervisedProcessIdentity[] invalidOwners =
        [
            CreateOwner(processId: 0),
            CreateOwner(startTicks: 0),
            CreateOwner(sessionId: -1),
            CreateOwner(userSid: "S-1-5-21-100-200-300-1002"),
        ];

        foreach (SupervisedProcessIdentity owner in invalidOwners)
        {
            var store = new FakeTaskManagerPolicyStore(AbsentPolicy(keyExists: false));
            var restriction = CreateRestriction(store);

            _ = Assert.ThrowsExactly<ArgumentException>(
                () => restriction.Apply(owner));

            Assert.IsEmpty(store.Operations);
        }
    }

    [TestMethod]
    public void RecoveryCodecRoundTripsOnlyAnAbsentBaseline()
    {
        TaskManagerRecoveryRecord expected = CreateRecovery(
            CreateOwner(),
            AbsentPolicy(keyExists: false));

        string serialized = TaskManagerRecoveryRecordCodec.Serialize(expected);
        TaskManagerRecoveryRecord actual =
            TaskManagerRecoveryRecordCodec.Deserialize(serialized);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void RecoveryCodecRejectsAValueBearingBaseline()
    {
        const string malformed = """
            {"Version":1,"InstallDirectory":"C:\\\\Program Files\\\\ZARA","UserSid":"S-1-5-21-100-200-300-1001","ProcessId":40,"ProcessStartTimeUtcTicks":400,"SessionId":2,"OwnerUserSid":"S-1-5-21-100-200-300-1001","BaselineKeyExists":true,"BaselineValueExists":true}
            """;

        _ = Assert.ThrowsExactly<InvalidDataException>(
            () => TaskManagerRecoveryRecordCodec.Deserialize(malformed));
    }

    private static TaskManagerRestriction CreateRestriction(
        FakeTaskManagerPolicyStore store)
    {
        return new TaskManagerRestriction(store, InstallDirectory, UserSid);
    }

    private static SupervisedProcessIdentity CreateOwner(
        int processId = 40,
        long startTicks = 400,
        int sessionId = 2,
        string userSid = UserSid)
    {
        return new SupervisedProcessIdentity(
            processId,
            startTicks,
            sessionId,
            userSid);
    }

    private static TaskManagerRecoveryRecord CreateRecovery(
        SupervisedProcessIdentity owner,
        TaskManagerPolicyValue baseline)
    {
        return new TaskManagerRecoveryRecord(
            InstallDirectory,
            UserSid,
            owner,
            baseline);
    }

    private static TaskManagerPolicyValue AbsentPolicy(bool keyExists)
    {
        return new TaskManagerPolicyValue(
            keyExists,
            ValueExists: false,
            RegistryValueKind.None,
            Value: null);
    }

    private static TaskManagerPolicyValue ValuePolicy(
        RegistryValueKind kind,
        object value)
    {
        return new TaskManagerPolicyValue(
            KeyExists: true,
            ValueExists: true,
            kind,
            value);
    }

    private static bool IsApplied(TaskManagerPolicyValue value)
    {
        return value.ValueExists &&
            value.ValueKind == RegistryValueKind.DWord &&
            value.Value is int dword &&
            dword == 1;
    }

    private sealed class FakeTaskManagerPolicyStore : ITaskManagerPolicyStore
    {
        internal FakeTaskManagerPolicyStore(TaskManagerPolicyValue policy)
        {
            Policy = policy;
        }

        internal TaskManagerPolicyValue Policy { get; set; }

        internal TaskManagerRecoveryRecord? Recovery { get; set; }

        internal bool ExternallyManaged { get; set; }

        internal bool KeepPolicyKeyWhenDeletingValue { get; set; } = true;

        internal Exception? FailureBeforeNextPolicyWrite { get; set; }

        internal Exception? FailureAfterNextPolicyWrite { get; set; }

        internal TaskManagerPolicyValue? ReplacementForNextPolicyWrite { get; set; }

        internal List<string> Operations { get; } = [];

        internal int PolicyWriteCount { get; private set; }

        internal int RecoveryWriteCount { get; private set; }

        internal int RecoveryDeleteCount { get; private set; }

        public TaskManagerPolicyValue ReadPolicy()
        {
            Operations.Add("policy-read");
            return Policy;
        }

        public bool IsExternallyManaged()
        {
            Operations.Add("managed-read");
            return ExternallyManaged;
        }

        public void WritePolicy(TaskManagerPolicyValue value)
        {
            Operations.Add("policy-write");
            PolicyWriteCount++;
            if (FailureBeforeNextPolicyWrite is Exception beforeFailure)
            {
                FailureBeforeNextPolicyWrite = null;
                throw beforeFailure;
            }

            if (ReplacementForNextPolicyWrite is TaskManagerPolicyValue replacement)
            {
                ReplacementForNextPolicyWrite = null;
                Policy = replacement;
            }
            else if (!value.ValueExists && KeepPolicyKeyWhenDeletingValue)
            {
                Policy = value with { KeyExists = true };
            }
            else
            {
                Policy = value;
            }

            if (FailureAfterNextPolicyWrite is Exception afterFailure)
            {
                FailureAfterNextPolicyWrite = null;
                throw afterFailure;
            }
        }

        public TaskManagerRecoveryRecord? ReadRecovery()
        {
            Operations.Add("recovery-read");
            return Recovery;
        }

        public void WriteRecovery(TaskManagerRecoveryRecord record)
        {
            Operations.Add("recovery-write");
            RecoveryWriteCount++;
            Recovery = record;
        }

        public void DeleteRecovery()
        {
            Operations.Add("recovery-delete");
            RecoveryDeleteCount++;
            Recovery = null;
        }
    }
}
