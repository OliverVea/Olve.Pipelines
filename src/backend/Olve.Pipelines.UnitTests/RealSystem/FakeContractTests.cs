using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Retention;
using Olve.Pipelines.RealSystemTests.Contracts;
using Olve.Pipelines.Shared.Persistence;
using Olve.Pipelines.UnitTests.Harness;

namespace Olve.Pipelines.UnitTests.RealSystem;

[InheritsTests]
public class FakeSecretsContract : KubernetesSecretsContract
{
    protected override IReadOnlyDictionary<string, string> KnownDisagreements { get; } = new Dictionary<string, string>
    {
        [nameof(Get_OfASecretWithNoValues_IsNull)] = "FakeSecrets returns an empty dictionary; the API omits `data`, so the real client returns null.",
    };

    protected override Task<ContractSubject<IKubernetesClient>> SubjectAsync() =>
        Task.FromResult(new ContractSubject<IKubernetesClient>(new PipelineSecretCopyServiceTests.FakeSecrets(), "ns", () => $"secret-{Guid.NewGuid():N}"));
}

[InheritsTests]
public class FakeKubernetesContract : HarnessClusterContract
{
    protected override IReadOnlyDictionary<string, string> KnownDisagreements { get; } = new Dictionary<string, string>
    {
        [nameof(CreateJob_IsTrueThenFalseForTheSameName)] = "FakeKubernetes always answers true; the real client answers false on 409, which is how the executor reattaches.",
        [nameof(PodState_OfAnUnknownJob_IsNull_WhileOtherJobsExist)] = "FakeKubernetes keeps one scripted state for all Jobs, so any name answers once one Job exists.",
        [nameof(DeleteJob_MakesItsStateNull_AndAMissingOneIsFine)] = "FakeKubernetes records the delete and keeps answering the scripted state.",
        [nameof(UpsertSecret_CreatesThenReplaces)] = "FakeKubernetes records upserts and GetSecretAsync always answers null.",
        [nameof(Logs_OfAnUnknownJob_AreNull)] = "FakeKubernetes throws for a log it has no script for; the real client returns null for a Job without a pod.",
    };

    protected override Task<ContractSubject<Cluster>> SubjectAsync()
    {
        var fake = new FakeKubernetes();
        return Task.FromResult(new ContractSubject<Cluster>(new Cluster(fake, fake), "ns", () => $"job-{Guid.NewGuid():N}"));
    }

    protected override Task<HarnessPodState> RunningStateAsync() => Task.FromResult(HarnessPodStates.Running);

    protected override Task<HarnessPodState> ExitedCleanlyStateAsync() => Task.FromResult(HarnessPodStates.Exited0);
}

[InheritsTests]
public class FakeSnapshotStoreContract : SnapshotStoreContract
{
    protected override Task<ContractSubject<ISnapshotStore>> SubjectAsync() =>
        Task.FromResult(new ContractSubject<ISnapshotStore>(new MemorySnapshotStore(), "", () => $"snap-{Guid.NewGuid():N}"));
}

[InheritsTests]
public class FakeRetentionObjectsContract : RetentionObjectStoreContract
{
    protected override Task<ContractSubject<Store>> SubjectAsync()
    {
        var fake = new RetentionServiceTests.FakeObjects();
        Task PutAsync(string key, string content)
        {
            lock (fake)
                fake.Objects.Add(new StoredObject(key, DateTimeOffset.UtcNow, content.Length));
            return Task.CompletedTask;
        }
        return Task.FromResult(new ContractSubject<Store>(new Store(fake, PutAsync), "", () => $"list-{Guid.NewGuid():N}"));
    }
}

[InheritsTests]
public class FakeHarnessStorageContract : HarnessStorageContract
{
    protected override Task<ContractSubject<IHarnessStorage>> SubjectAsync() =>
        Task.FromResult(new ContractSubject<IHarnessStorage>(new FakeStorage(), "", () => $"obj-{Guid.NewGuid():N}"));
}

[InheritsTests]
public class FakeS3Contract : ResultCollectionContract
{
    protected override IReadOnlyDictionary<string, string> KnownDisagreements { get; } = new Dictionary<string, string>
    {
        [nameof(GetObject_ReportsTheLastModifiedTime)] = "FakeS3 answers the Unix epoch for every object.",
    };

    protected override Task<ContractSubject<Store>> SubjectAsync() =>
        Task.FromResult(new ContractSubject<Store>(new Store(new FakeS3().Client(), FakeS3.Bucket), "", () => $"obj-{Guid.NewGuid():N}"));
}

[InheritsTests]
public class FakeConfigSourceContract : ConfigSourceContract
{
    protected override Task<ContractSubject<Repository>> SubjectAsync()
    {
        var source = new FakeConfigSource { BranchHeadSha = "head-sha", ConfigSha = "config-sha" };
        return Task.FromResult(new ContractSubject<Repository>(new Repository(source, source.BranchHeadSha, source.BranchHeadMessage, source.ConfigSha), "", () => ""));
    }
}
