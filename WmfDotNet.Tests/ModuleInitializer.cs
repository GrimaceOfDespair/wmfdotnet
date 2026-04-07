using System.Runtime.CompilerServices;

namespace WmfDotNet.Tests
{
    public static class ModuleInitializer
    {
        [ModuleInitializer]
        public static void Init() =>
            VerifyTests.VerifyImageHash.Initialize();
    }
}
