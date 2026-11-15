using System;
using System.Threading;

namespace CSBridge
{
    /// <summary>Runs code on the game's simulation thread via SimulationManager.AddAction and waits for the result.</summary>
    public static class GameThread
    {
        public class TimeoutException : Exception
        {
            public TimeoutException(string m) : base(m) { }
        }

        public static T Run<T>(Func<T> func, int timeoutMs)
        {
            T result = default(T);
            Exception error = null;
            ManualResetEvent done = new ManualResetEvent(false);

            SimulationManager.instance.AddAction(delegate ()
            {
                try { result = func(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });

            if (!done.WaitOne(timeoutMs))
                throw new TimeoutException("Game did not process the request within " + timeoutMs + " ms");
            if (error != null) throw error;
            return result;
        }
    }
}
