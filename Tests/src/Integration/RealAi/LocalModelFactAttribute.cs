namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class LocalModelFactAttribute : FactAttribute
{
    public LocalModelFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AUTO_REMOTE_REAL_AI") != "1" || string.IsNullOrWhiteSpace(Model))
        {
            Skip = "Runs a prompt on a local model through OpenCode; set AUTO_REMOTE_REAL_AI=1 and AUTO_REMOTE_LOCAL_MODEL to a model OpenCode offers (such as ollama/gemma4:e2b) that is fast enough on this machine and can call tools.";
        }
    }

    public static string? Model => Environment.GetEnvironmentVariable("AUTO_REMOTE_LOCAL_MODEL");
}
