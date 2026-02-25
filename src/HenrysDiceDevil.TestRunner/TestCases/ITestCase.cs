namespace HenrysDiceDevil.Tests.TestCases;

internal interface ITestCase
{
    string Name { get; }

    void Run();
}
