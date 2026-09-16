using System;
using System.Threading.Tasks;

namespace v232.Launcher.WPF.Services
{
    // One verification per launcher session. A failed attempt can be retried by
    // PLAY, but concurrent startup/PLAY callers always share the same check.
    public sealed class ClientUpdateGate
    {
        private readonly Func<Task<PatchResult>> _verify;
        private readonly object _sync = new object();
        private Task<PatchResult> _pending;

        public ClientUpdateGate(Func<Task<PatchResult>> verify)
        {
            _verify = verify ?? throw new ArgumentNullException(nameof(verify));
        }

        public Task<PatchResult> EnsureReadyAsync()
        {
            lock (_sync)
            {
                if (_pending == null || (_pending.IsCompleted && !_pending.Result.Success))
                    _pending = VerifyAsync();
                return _pending;
            }
        }

        private async Task<PatchResult> VerifyAsync()
        {
            try { return await _verify().ConfigureAwait(false) ?? PatchResult.Failed("No verification result."); }
            catch (Exception ex) { return PatchResult.Failed("Could not verify game files: " + ex.Message); }
        }
    }
}
