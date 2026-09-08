using System;
using System.IO;
using System.Threading.Tasks;
using v232.Launcher.WPF.Services;

internal static class LiveGameSmoke
{
    private static int Main()
    {
        return RunAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        string username = Environment.GetEnvironmentVariable("MSTORY_QA_USER");
        string password = Environment.GetEnvironmentVariable("MSTORY_QA_PASSWORD");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("MSTORY_QA_USER and MSTORY_QA_PASSWORD are required.");
            return 2;
        }

        var service = new LoginService(username, password);
        TextWriter output = Console.Out;
        bool authenticated;
        try
        {
            // Handlers logs the token on success. Suppress that output during
            // this live smoke so credentials/tokens never enter the report.
            Console.SetOut(TextWriter.Null);
            authenticated = await service.Authenticate();
        }
        finally
        {
            Console.SetOut(output);
        }

        if (!authenticated)
        {
            Console.WriteLine("AUTH_FAILED");
            return 10;
        }

        Console.WriteLine("AUTH_OK");
        bool launched = await service.LaunchMapleAsync();
        Console.WriteLine(launched ? "CLIENT_LAUNCH_ACCEPTED" : "CLIENT_LAUNCH_FAILED");
        return launched ? 0 : 20;
    }
}
