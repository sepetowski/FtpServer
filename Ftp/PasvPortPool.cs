namespace FtpServer.Ftp
{
    public class PasvPortPool
    {
        private readonly int min, max;
        private readonly HashSet<int> used = new HashSet<int>();
        private readonly object gate = new object();
        public PasvPortPool(int min, int max) { this.min = min; this.max = max; }

        // Try to acquire an available port from the pool
        public bool TryAcquire(out int port)
        {
            lock (gate)
            {
                for (int p = min; p <= max; p++)
                {
                    if (!used.Contains(p))
                    {
                        used.Add(p);
                        port = p;
                        return true;
                    }
                }
                port = 0; return false;
            }
        }
        public void Release(int port)
        {
            lock (gate) used.Remove(port);
        }
    }
}
