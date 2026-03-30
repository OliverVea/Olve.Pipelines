using Olve.Utilities.Ids;

namespace Olve.Pipelines.Jobs;

public class IdProvider
{
    public Id<T> Create<T>() => Id.New<T>();
}
