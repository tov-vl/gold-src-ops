namespace GoldSrcOps.WebTests.Browser;

internal sealed class BrowserFactAttribute : FactAttribute
{
    private const string RunBrowserTestsVariable = "GOLDSRCOPS_RUN_BROWSER_TESTS";

    public BrowserFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunBrowserTestsVariable),
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Set {RunBrowserTestsVariable}=true to run browser tests.";
        }
    }
}
