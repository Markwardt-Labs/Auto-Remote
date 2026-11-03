namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class RealAiFactAttribute : FactAttribute
{
    public RealAiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AUTO_REMOTE_REAL_AI") != "1")
        {
            Skip = "Runs the real AI tools on your subscription; set AUTO_REMOTE_REAL_AI=1 to run it.";
        }
    }
}
