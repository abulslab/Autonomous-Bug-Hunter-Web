using System.Runtime.CompilerServices;

namespace BugHunter.Tests;

// The demo apps and simulators read their keys from the environment; tests use fresh random ones, so no key is written
// in the repository.
static class TestKeys
{
    public static readonly string Demo = "test-" + Guid.NewGuid().ToString("N");
    public static readonly string Sim = "test-" + Guid.NewGuid().ToString("N");

    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("DEMO_SHOP_KEY", Demo);
        Environment.SetEnvironmentVariable("SIM_TOKEN", Sim);
    }
}
