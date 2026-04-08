using System.Security.Cryptography;

namespace Olve.Pipelines.Jobs;

public class IdProvider
{
    public virtual Id<T> Create<T>() => Id.New<T>();

    public virtual string CreateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
