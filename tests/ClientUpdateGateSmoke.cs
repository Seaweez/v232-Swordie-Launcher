using System;
using System.Threading.Tasks;
using v232.Launcher.WPF.Services;

internal static class ClientUpdateGateSmoke
{
#if HAS_UPDATE_GATE
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<int> Main()
    {
        int checks = 0;
        var pending = new TaskCompletionSource<PatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new ClientUpdateGate(() => { checks++; return pending.Task; });
        var startup = gate.EnsureReadyAsync();
        var play = gate.EnsureReadyAsync();
        Check(!play.IsCompleted && checks == 1, "PLAY must wait for the same in-flight check, without a second download");
        pending.SetResult(PatchResult.Done(0));
        Check((await startup).Success && (await play).Success, "successful verification must release both callers");
        Check((await gate.EnsureReadyAsync()).Success && checks == 1, "repeated PLAY must not repeat a successful session check");

        int attempts = 0;
        var retry = new ClientUpdateGate(() => Task.FromResult(++attempts == 1 ? PatchResult.Failed("incomplete") : PatchResult.Done(1)));
        Check(!(await retry.EnsureReadyAsync()).Success, "partial update must block PLAY");
        Check((await retry.EnsureReadyAsync()).Success && attempts == 2, "next PLAY must retry a failed check once");

        var broken = new ClientUpdateGate(() => throw new InvalidOperationException("cannot verify"));
        Check(!(await broken.EnsureReadyAsync()).Success, "unexpected verification error must not permit launch");
        Console.WriteLine("PASS: PLAY waits; checks are single-flight; failure blocks and can retry");
        return 0;
    }
#else
    private static int Main() { Console.WriteLine("FAIL: PLAY update barrier is missing"); return 1; }
#endif
}
