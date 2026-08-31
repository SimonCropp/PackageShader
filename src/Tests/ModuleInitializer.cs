[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init() =>
        VerifyDiffPlex.Initialize();
}