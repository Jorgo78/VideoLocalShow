namespace VideoLocalShow.ShareExtension;

// An app extension bundle is loaded as a plugin, not launched as a running executable the way a
// full app is - iOS enters it via NSExtensionPrincipalClass in Info.plist (ShareViewController),
// never through this Main. It exists purely because OutputType=Exe still makes the C# compiler
// require *some* entry point to exist.
public static class Program
{
    private static void Main()
    {
    }
}
