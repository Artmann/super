using System.Reflection;
using Xunit.Sdk;

namespace Super.Testing;

/// <summary>Runs a [Theory] once per seed, passing the seed as its int parameter.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SeedsAttribute(int count) : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
    {
        for (int seed = 1; seed <= count; seed++)
        {
            yield return [seed];
        }
    }
}
