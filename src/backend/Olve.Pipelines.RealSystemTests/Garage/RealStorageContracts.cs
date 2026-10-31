using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.RealSystemTests.Contracts;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.RealSystemTests.Garage;

[InheritsTests]
public class RealSnapshotStore : SnapshotStoreContract
{
    protected override async Task<ContractSubject<ISnapshotStore>> SubjectAsync()
    {
        var server = await TestServer.LeaseAsync();
        return new(new S3SnapshotStore(server.Bucket.Client, new StorageOptions(server.Bucket.Name)), "", () => TestServer.NewName("snap"), server);
    }
}

[InheritsTests]
public class RealRetentionObjectStore : RetentionObjectStoreContract
{
    protected override async Task<ContractSubject<Store>> SubjectAsync()
    {
        var server = await TestServer.LeaseAsync();
        return new(new Store(server.Bucket.Objects, server.Bucket.PutAsync), "", () => TestServer.NewName("list"), server);
    }
}

[InheritsTests]
public class RealHarnessStorage : HarnessStorageContract
{
    protected override async Task<ContractSubject<IHarnessStorage>> SubjectAsync()
    {
        var server = await TestServer.LeaseAsync();
        var storage = new StorageOptions(server.Bucket.Name);
        var collector = new HarnessResultCollector(server.Bucket.Client, storage, new HarnessOptions(), NullLogger<HarnessResultCollector>.Instance);
        var harness = new S3HarnessStorage(server.Bucket.Client, storage, collector, NullLogger<S3HarnessStorage>.Instance);
        return new(harness, "", () => TestServer.NewName("obj"), server);
    }
}

[InheritsTests]
public class RealResultCollection : ResultCollectionContract
{
    protected override async Task<ContractSubject<Store>> SubjectAsync()
    {
        var server = await TestServer.LeaseAsync();
        return new(new Store(server.Bucket.Client, server.Bucket.Name), "", () => TestServer.NewName("obj"), server);
    }
}
